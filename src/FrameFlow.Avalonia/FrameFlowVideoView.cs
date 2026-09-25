// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia;

/// <summary>
/// Avalonia control that renders decoded video frames from an <see cref="AvaloniaVideoSink"/>.
/// Drop this control into your XAML and wire it to a sink instance.
/// </summary>
/// <remarks>
/// <para>
/// The sink hands frames to this view on the presenting (graph) thread, via its
/// <c>FrameArrived</c> hook. The frame's <see cref="IVideoFrame.AsCpu()"/> method provides
/// <see cref="CpuFrameData"/> for pixel copying into the <see cref="WriteableBitmap"/>
/// back buffer.
/// </para>
/// <para>
/// <b>Threading model (ADR-0016 Decision 1):</b> double-buffered
/// <see cref="WriteableBitmap"/> instances, with the copy on the producer's thread.
/// </para>
/// <code>
/// graph thread   PresentAsync → FrameArrived → Lock(_back) → copy BGRA → Post(swap)
/// UI thread      swap (_front, _back) → InvalidateVisual → Render → DrawImage(_front)
/// </code>
/// <para>
/// The UI thread never touches pixel data. At 1080p60 the copy is ~8.3 MB per frame; doing
/// it in <see cref="Render"/> spent most of the ~16 ms present budget on memcpy and made the
/// view miss ticks under load, which shed frames all the way back to the decoder. The
/// producer thread can afford it, and is where ADR-0016 put it.
/// </para>
/// <para>
/// Redraws are driven by frame arrival rather than a free-running timer, so a paused or
/// stalled source costs nothing.
/// </para>
/// <para>
/// <b>Pixel format:</b> expects BGRA32 packed data in <see cref="CpuFrameData.PlaneY"/>,
/// which maps directly to Avalonia's <c>PixelFormat.Bgra8888</c> with no conversion.
/// </para>
/// </remarks>
public sealed partial class FrameFlowVideoView : Control, IVideoSurface
{
    private static readonly Meter ViewMeter = new("FrameFlow.Avalonia.VideoView", "1.0.0");
    private static readonly Counter<long> FramesRenderedCounter = ViewMeter.CreateCounter<long>(
        "frameflow.avalonia.view.frames_rendered",
        description: "Total video frames rendered to the Avalonia video view."
    );

    private readonly ILogger<FrameFlowVideoView> _logger;
    private readonly object _lock = new();

    // Double-buffer: render pass writes to _back then swaps to _front for drawing.
    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private int _bitmapWidth;
    private int _bitmapHeight;

    // The pixels of the frame whose dimensions asked for a reallocation, kept so that frame
    // can be drawn once the buffers exist. They used to be discarded: the WriteableBitmaps are
    // created on the UI thread, so the producer posted the allocation and let the frame go, on
    // the reasoning that this costs one frame on the first frame and on each resize. A clip
    // supplies another 33 ms later and never notices. An item with one frame has nothing to
    // follow it, so the frame that triggered the resize was the only frame it would ever offer
    // and the view kept drawing whatever was there before, for as long as that item lasted
    // (#287).
    //
    // Pixels rather than the frame itself. The view has never held a frame past the present
    // call, whose buffer goes back to the pool when it returns, so the copy happens now, on
    // the producer thread where every other copy happens, and the UI thread only blits it into
    // the bitmap it just allocated.
    //
    // Held under _lock and accounted exactly once like every other frame: presented when the
    // blit reaches the swap, dropped when a newer frame supersedes it or a detach strands it.
    private byte[]? _staged;
    private bool _stagedPending;
    private int _stagedStride;
    private int _stagedWidth;
    private int _stagedHeight;
    private TimeSpan _stagedPts;
    private SinkBinding? _stagedBinding;

    private AvaloniaVideoSink? _sink;

    // True when the sink (and its pool) was created by the view via EnsureSink
    // and must be disposed when the view detaches. False when the user assigned
    // a sink externally — in that case the caller owns disposal.
    private bool _sinkIsOwned;
    private CpuFramePool? _ownedPool;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private int _renderedFrameCount;

    /// <summary>
    /// One sink's attachment to this view. Every producer callback and every swap it posts
    /// carries the binding it was raised under, so work in flight across a detach or a sink
    /// swap is attributed to the sink that produced it — or discarded, never misfiled onto a
    /// replacement.
    /// </summary>
    private sealed class SinkBinding(AvaloniaVideoSink sink)
    {
        public readonly AvaloniaVideoSink Sink = sink;

        /// <summary>Set under <c>_lock</c> when the attachment ends. Read under it too.</summary>
        public bool Detached;
    }

    private SinkBinding? _binding;

    // True while _back holds a copied frame the UI thread has not swapped to the front yet.
    // Guarded by _lock. Copying over a pending back buffer is correct (latest-wins), but the
    // frame it held never drew, so the sink counts it as a drop.
    private bool _backPending;


    // PTS of the frame sitting in _back, carried to the swap so the sink can stamp it when
    // the frame actually becomes the front buffer. Guarded by _lock.
    private TimeSpan _backPts;

    // The binding that produced the frame sitting in _back, so the swap credits the sink that
    // actually made it. Guarded by _lock, and only meaningful while _backPending.
    private SinkBinding? _backBinding;

    // True while a swap is queued on the dispatcher. Guarded by _lock.
    //
    // View-level, and the queued delegate takes no binding: it publishes whatever is in the
    // back buffer when it runs, crediting _backBinding. That is what keeps the queue bounded
    // at one across sink churn — a per-binding claim bounded the queue per binding but not
    // across replacements, and made a stale delegate able to strand its successor's frame.
    // There is no stale delegate here; there is one, and it always services current state.
    private bool _swapPosted;

    // Set while a buffer (re)allocation is posted to the UI thread but has not run. Stops the
    // producer queueing a post per frame across the round-trip on first frame and on resize.
    private bool _allocationPosted;

    /// <summary>Gets the total number of frames swapped to the front buffer and drawn.</summary>
    public int RenderedFrameCount => Volatile.Read(ref _renderedFrameCount);

    /// <summary>
    /// Gets or sets the <see cref="AvaloniaVideoSink"/> that supplies frames to this view.
    /// </summary>
    /// <remarks>
    /// Setting this property assigns an externally-owned sink — the view will
    /// not dispose it on detach. If left unset, calling <see cref="EnsureSink"/>
    /// (or attaching the view to the visual tree) constructs an internal sink
    /// that the view will dispose when it detaches.
    /// </remarks>
    public AvaloniaVideoSink? Sink
    {
        get => _sink;
        set
        {
            if (ReferenceEquals(_sink, value))
                return;
            EndBinding();
            DisposeOwnedSinkIfAny();
            _sink = value;
            _sinkIsOwned = false;
            if (_sink is not null)
                BeginBinding(_sink);
        }
    }

    /// <summary>
    /// Gets or sets the logger factory used by the view and any sink/pool it
    /// owns. Defaults to <see cref="NullLoggerFactory"/>. Assign before the
    /// view attaches (or before calling <see cref="EnsureSink"/>) for the
    /// factory to apply to the owned sink.
    /// </summary>
    public ILoggerFactory LoggerFactory
    {
        get => _loggerFactory;
        set => _loggerFactory = value ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Initializes a new <see cref="FrameFlowVideoView"/>.
    /// </summary>
    public FrameFlowVideoView()
        : this(null) { }

    /// <summary>
    /// Initializes a new <see cref="FrameFlowVideoView"/> with an optional logger.
    /// </summary>
    public FrameFlowVideoView(ILogger<FrameFlowVideoView>? logger)
    {
        _logger = logger ?? NullLogger<FrameFlowVideoView>.Instance;
        ClipToBounds = true;
    }

    /// <summary>
    /// Ensures the view has a video sink ready to receive frames. If <see cref="Sink"/>
    /// is already set, this is a no-op. Otherwise, a <see cref="CpuFramePool"/> and
    /// an <see cref="AvaloniaVideoSink"/> are constructed using <see cref="LoggerFactory"/>;
    /// both are owned by the view and disposed when it detaches from the visual tree.
    /// </summary>
    /// <returns>The view's current <see cref="AvaloniaVideoSink"/> instance.</returns>
    /// <remarks>
    /// Callers that need a sink reference before the view attaches (e.g. fluent
    /// player builders) should invoke this method to materialize the sink eagerly.
    /// </remarks>
    public AvaloniaVideoSink EnsureSink()
    {
        if (_sink is not null)
            return _sink;

        var pool = new CpuFramePool(_loggerFactory.CreateLogger<CpuFramePool>());
        var sink = new AvaloniaVideoSink(pool, _loggerFactory.CreateLogger<AvaloniaVideoSink>());
        _ownedPool = pool;
        _sink = sink;
        _sinkIsOwned = true;
        BeginBinding(sink);
        return sink;
    }

    // ── IVideoSurface ─────────────────────────────────────────────
    // Lets FrameFlowPlayerView host this CPU surface or a GPU presenter
    // interchangeably (the chrome binds to the player, not the surface).
    Control IVideoSurface.Control => this;
    bool IVideoSurface.PrefersHardwareFrames => false;
    IVideoSink IVideoSurface.AttachSink(ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory;
        return EnsureSink();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Materialize an internal sink when no caller has assigned one.
        // Gives consumers a zero-ceremony "drop the view in XAML and it
        // just works" experience.
        if (_sink is null)
            EnsureSink();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Stop the producer calling into a detached view before anything else.
        EndBinding();

        DisposeOwnedSinkIfAny();

        base.OnDetachedFromVisualTree(e);
    }

    private void DisposeOwnedSinkIfAny()
    {
        if (!_sinkIsOwned)
            return;

        var sink = _sink;
        var pool = _ownedPool;
        EndBinding();
        _sink = null;
        _ownedPool = null;
        _sinkIsOwned = false;

        // AvaloniaVideoSink.DisposeAsync returns a completed ValueTask.
        // Guard with IsCompleted so the analyzer accepts the synchronous
        // GetResult call. CpuFramePool is plain IDisposable.
        if (sink is not null)
        {
            var disposeTask = sink.DisposeAsync();
            if (disposeTask.IsCompleted)
                disposeTask.GetAwaiter().GetResult();
            else
                disposeTask.AsTask().GetAwaiter().GetResult();
        }
        pool?.Dispose();
    }

    /// <summary>
    /// Starts an attachment to <paramref name="sink"/> and points its producer callback at
    /// this view. UI thread (property setters and visual-tree attach).
    /// </summary>
    private void BeginBinding(AvaloniaVideoSink sink)
    {
        var binding = new SinkBinding(sink);
        lock (_lock)
            _binding = binding;

        sink.FrameArrived = () => OnFrameArrived(binding);
    }

    /// <summary>
    /// Ends the current attachment: no further callback does anything, and a frame sitting
    /// unswapped in the back buffer is charged to the sink that produced it rather than
    /// vanishing.
    /// </summary>
    /// <remarks>
    /// Marking <c>Detached</c> under <see cref="_lock"/> — which <see cref="OnFrameArrived"/>
    /// holds for its whole body — is what makes detach safe against a callback already
    /// running. Clearing <c>FrameArrived</c> alone would not: the delegate can already be on
    /// the producer's stack, and it would go on copying into buffers this view is about to
    /// drop while the caller disposes the sink's frame pool underneath it.
    /// </remarks>
    private void EndBinding()
    {
        SinkBinding? binding;
        SinkBinding? strandedCopy;
        SinkBinding? strandedPixels;

        lock (_lock)
        {
            binding = _binding;
            if (binding is null)
                return;

            binding.Detached = true;
            _binding = null;

            // A copy waiting for its swap and pixels waiting for their buffers are two
            // different frames. Both are stranded by the detach and each is owed its own
            // drop, so they are counted apart: one flag covering both charged one drop for
            // two frames.
            strandedCopy = _backPending ? _backBinding ?? binding : null;
            _backPending = false;
            _backBinding = null;

            strandedPixels = _stagedPending ? _stagedBinding ?? binding : null;
            _stagedPending = false;
            _stagedBinding = null;
        }

        binding.Sink.FrameArrived = null;
        strandedCopy?.Sink.RecordPreSwapDrop();
        strandedPixels?.Sink.RecordPreSwapDrop();
    }

    /// <summary>
    /// Takes the frame the sink just installed and copies it into the back buffer. Runs on
    /// the presenting (graph) thread — never the UI thread (ADR-0016 Decision 1).
    /// </summary>
    /// <param name="binding">
    /// The attachment this callback was raised under. Everything is read from it rather than
    /// from <c>_sink</c>, so a callback that outlives a sink swap drains and accounts to the
    /// sink that actually produced the frame.
    /// </param>
    private void OnFrameArrived(SinkBinding binding)
    {
        // The producer must not fault because a presenter could not draw. The sink also
        // catches, but recording the drop needs to happen here where the frame is known.
        try
        {
            CopyArrivedFrame(binding);
        }
        catch (Exception ex)
        {
            LogCopyFailed(_logger, ex);
        }
    }

    private void CopyArrivedFrame(SinkBinding binding)
    {
        bool post = false;

        // Held across the whole body so a detach cannot land mid-copy: EndBinding takes the
        // same lock to set Detached, so it either wins the race outright or waits here.
        lock (_lock)
        {
            if (binding.Detached)
                return;

            // Take without counting: whether this frame is presented or dropped is not known
            // until its dimensions are in hand, and every path below records exactly one of
            // the two. RenderPendingFrame would have counted it presented on the way out.
            var frame = binding.Sink.TakePendingFrame();
            if (frame is null)
                return;

            // Set once _back is being written, so the catch below knows whether the back
            // buffer is now garbage or still holds an intact earlier frame.
            bool copyStarted = false;

            try
            {
                var cpu = frame.AsCpu();
                if (cpu is null)
                {
                    binding.Sink.RecordPreSwapDrop();
                    return;
                }

                var data = cpu.Value;

                // Nothing can be drawn at a non-positive size and no bitmap can be allocated
                // for one, so staging it would only fail the allocation on the UI thread.
                // Charged here so it still lands in exactly one bucket.
                if (data.Width <= 0 || data.Height <= 0)
                {
                    binding.Sink.RecordPreSwapDrop();
                    return;
                }

                // Wrong size or nothing allocated yet: the WriteableBitmaps are created on
                // the UI thread, so the allocation is posted. The frame is kept rather than
                // let go, because it may be the only one this item will offer (#287); the
                // posted callback draws it once the buffers exist.
                if (_back is null || _bitmapWidth != data.Width || _bitmapHeight != data.Height)
                {
                    // Latest wins, as everywhere else here: a newer frame replaces the one
                    // staged, and the one it replaces is charged the drop it is owed. The
                    // slot is emptied first: if the staging copy below throws, the pixels
                    // just charged must not still be there for a second charge.
                    if (_stagedPending)
                    {
                        _stagedPending = false;
                        _stagedBinding?.Sink.RecordPreSwapDrop();
                    }

                    StagePixelsLocked(data, frame.Pts, binding);
                    RequestBuffersForStagedFrame();
                    return;
                }

                // Latest-wins into the back buffer. If the UI thread has not swapped the
                // previous copy in yet, the frame it held never drew — count that one as a
                // drop. It was never counted presented; that happens at the swap.
                if (_backPending)
                    binding.Sink.RecordPreSwapDrop();

                copyStarted = true;
                using (var fb = _back.Lock())
                    CopyPixels(data.PlaneY.Span, data.StrideY, fb);

                _backPts = frame.Pts;
                _backBinding = binding;
                _backPending = true;

                // At most one queued swap for the view: a later copy just overwrites _back and
                // the pending swap publishes the newer frame. Without this a producer running
                // ahead of a stalled UI thread queues one delegate per frame — ~600 across a
                // 10 s stall at 60 fps — all but the first a no-op.
                if (!_swapPosted)
                {
                    _swapPosted = true;
                    post = true;
                }
            }
            catch
            {
                // Anything that throws while handling this frame — pixel access, the bitmap
                // lock, the copy — must still land it in exactly one bucket. A throw partway
                // through the copy also leaves _back garbage, so it stops being publishable;
                // a throw before the copy leaves an earlier pending frame intact.
                if (copyStarted)
                    _backPending = false;
                binding.Sink.RecordPreSwapDrop();
                throw;
            }
            finally
            {
                frame.Dispose();
            }
        }

        if (!post)
            return;

        try
        {
            Dispatcher.UIThread.Post(SwapAndInvalidate, DispatcherPriority.Render);
        }
        catch
        {
            // The post never landed, so nothing will clear the claim; leaving it set would
            // wedge the view permanently, since every later copy would overwrite _back and
            // skip posting.
            //
            // Deliberately not touching _backPending or the drop count. The frame stays
            // pending and is accounted exactly once by whoever reaches it: the next copy
            // supersedes it and charges the drop, or EndBinding charges it. Clearing it here
            // would discard a newer copy that landed while this post was failing, and
            // charging it here would double-count one that EndBinding had already charged.
            //
            // Re-post if anything is still waiting: this frame, or a newer one that landed
            // while the post was failing and skipped queuing behind the claim. Releasing the
            // claim alone leaves that frame unpublished when no later frame arrives — the
            // last frame of a paused or ended stream, which is exactly when it is on screen.
            bool retry;
            lock (_lock)
            {
                retry = _backPending;
                _swapPosted = retry;
            }

            if (retry)
            {
                try
                {
                    Dispatcher.UIThread.Post(SwapAndInvalidate, DispatcherPriority.Render);
                }
                catch
                {
                    // Twice is enough: the dispatcher is gone, so nothing can render anyway.
                    // Release the claim so a later frame can try again if it comes back.
                    lock (_lock)
                        _swapPosted = false;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Copies a frame's pixels aside while its buffer is still valid, for the blit that
    /// happens once the bitmap it needs has been allocated. Under <see cref="_lock"/>, on the
    /// producer thread.
    /// </summary>
    /// <remarks>
    /// The copy is here rather than in the posted callback because the view does not hold the
    /// frame past the present call, and its buffer returns to the pool when that call ends. The
    /// array is reused across resizes and only grows.
    /// </remarks>
    private void StagePixelsLocked(CpuFrameData data, TimeSpan pts, SinkBinding binding)
    {
        var needed = data.StrideY * data.Height;
        if (_staged is null || _staged.Length < needed)
            _staged = new byte[needed];

        data.PlaneY.Span[..needed].CopyTo(_staged);
        _stagedPending = true;
        _stagedStride = data.StrideY;
        _stagedWidth = data.Width;
        _stagedHeight = data.Height;
        _stagedPts = pts;
        _stagedBinding = binding;
    }

    /// <summary>
    /// Blits the staged pixels into the back buffer, if any are staged and the buffer that
    /// landed is the size they asked for. Returns whether there is now something to swap.
    /// Under <see cref="_lock"/>, on the UI thread, immediately after the allocation.
    /// </summary>
    /// <remarks>
    /// This is the one pixel copy the UI thread does, and it happens once per size change
    /// rather than per frame — no more than the cost the mismatch branch already accepted
    /// when it discarded the frame. Every other copy stays on the producer thread, which is
    /// what ADR-0016 moved it there for.
    /// </remarks>
    private bool BlitStagedFrameLocked()
    {
        if (!_stagedPending || _staged is null)
            return false;

        var binding = _stagedBinding;
        var stride = _stagedStride;
        var width = _stagedWidth;
        var height = _stagedHeight;
        var pts = _stagedPts;

        _stagedPending = false;
        _stagedBinding = null;

        try
        {
            // A detach between the request and this callback, or an allocation for a newer
            // size that superseded these pixels: either way they are owed their drop rather
            // than drawn at the wrong geometry.
            if (_back is null || binding is null || binding.Detached
                || _bitmapWidth != width || _bitmapHeight != height)
            {
                binding?.Sink.RecordPreSwapDrop();
                return false;
            }

            using (var fb = _back.Lock())
                CopyPixels(_staged.AsSpan(0, stride * height), stride, fb);

            _backPts = pts;
            _backBinding = binding;
            _backPending = true;
            return true;
        }
        catch
        {
            // Same rule as the producer-side copy: a throw partway through leaves _back
            // unpublishable, and these pixels land in exactly one bucket either way.
            _backPending = false;
            binding?.Sink.RecordPreSwapDrop();
            throw;
        }
    }

    /// <summary>
    /// Publishes the back buffer and asks for a redraw. UI thread only; the swap is the
    /// ADR-0016 hand-off point, and the only pixel-buffer work the UI thread does.
    /// </summary>
    /// <remarks>
    /// Takes no binding: it publishes whatever the back buffer holds when it runs and credits
    /// the binding that produced it. A detach or a sink replacement clears <c>_backPending</c>
    /// and charges the stranded frame, so there is nothing here to publish for a binding that
    /// has gone — and nothing to misfile onto a replacement.
    /// </remarks>
    private void SwapAndInvalidate()
    {
        TimeSpan pts;
        SinkBinding? producer;

        lock (_lock)
        {
            _swapPosted = false;

            // Nothing to publish: superseded and charged, or cleared by EndBinding.
            if (!_backPending)
                return;

            (_front, _back) = (_back, _front);
            _backPending = false;
            pts = _backPts;
            producer = _backBinding;
        }

        // Count the present HERE, not at the copy, and against the sink that produced it. A
        // frame overwritten in the back buffer before this ran never drew and is counted a
        // drop instead, so no frame lands in both counters and FramesPresented means what it
        // says.
        producer?.Sink.RecordPresented(pts);
        Interlocked.Increment(ref _renderedFrameCount);
        FramesRenderedCounter.Add(1);

        InvalidateVisual();
    }

    /// <summary>
    /// Posts an allocation of the buffer pair the staged pixels need, and the blit of those
    /// pixels into it. Must be called under <see cref="_lock"/>, with pixels already staged.
    /// </summary>
    /// <remarks>
    /// At most one allocation is in flight, so a producer running ahead of a stalled UI
    /// thread does not queue a round-trip per frame. The size is read from the staging slot
    /// when the callback runs rather than captured when it is posted. A frame at a third size
    /// arriving before the callback supersedes the staged pixels and finds this claim already
    /// taken, so a captured size would allocate for a frame that no longer exists, mismatch
    /// the pixels that are actually waiting, and drop them. Nothing re-requests after that,
    /// and for an item with a single frame nothing else is coming (#287).
    /// </remarks>
    private void RequestBuffersForStagedFrame()
    {
        if (_allocationPosted)
            return;
        _allocationPosted = true;

        try
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    bool publish;
                    lock (_lock)
                    {
                        _allocationPosted = false;

                        // A detach or a Clear between the request and here empties the slot.
                        if (_stagedPending)
                            AllocateBuffers(_stagedWidth, _stagedHeight);

                        publish = BlitStagedFrameLocked();
                    }

                    // Already on the UI thread, so the swap is a call rather than another
                    // post.
                    if (publish)
                        SwapAndInvalidate();
                },
                DispatcherPriority.Render
            );
        }
        catch
        {
            // The post never landed, so nothing will clear the claim; leaving it set would
            // wedge every later allocation request behind a callback that does not exist.
            //
            // The staging slot is emptied without charging it. The throw propagates into
            // CopyArrivedFrame, whose catch charges this frame its one drop; leaving the
            // pixels staged would let a later detach or Clear charge the same frame again.
            _allocationPosted = false;
            _stagedPending = false;
            _stagedBinding = null;
            throw;
        }
    }

    /// <summary>
    /// Renders the current front buffer. Called by Avalonia on the UI thread. Does no pixel
    /// copying — that already happened on the producer thread.
    /// </summary>
    /// <remarks>
    /// Taking <see cref="_lock"/> here can still make the UI thread wait on the tail of a
    /// copy already in progress, because <see cref="Clear"/> may dispose the front buffer
    /// from any thread and drawing a disposed bitmap would crash. That is a bounded wait on
    /// part of one copy, not the whole copy this method used to perform itself.
    /// </remarks>
    public override void Render(DrawingContext context)
    {
        WriteableBitmap? bitmapToRender;
        lock (_lock)
            bitmapToRender = _front;

        if (bitmapToRender is null)
        {
            base.Render(context);
            return;
        }

        // Compute destination rect preserving aspect ratio (letterboxed)
        var srcW = bitmapToRender.PixelSize.Width;
        var srcH = bitmapToRender.PixelSize.Height;
        var destRect = ComputeLetterboxRect(srcW, srcH, Bounds.Width, Bounds.Height);
        var srcRect = new Rect(0, 0, srcW, srcH);

        context.DrawImage(bitmapToRender, srcRect, destRect);
    }

    /// <summary>
    /// Clears the rendered surface and releases bitmap resources.
    /// </summary>
    public void Clear()
    {
        SinkBinding? strandedCopy;
        SinkBinding? strandedPixels;

        lock (_lock)
        {
            _front?.Dispose();
            _front = null;
            _back?.Dispose();
            _back = null;
            _bitmapWidth = 0;
            _bitmapHeight = 0;

            // Both a copy waiting for its swap and pixels waiting for their buffers are
            // thrown away here, and each is owed its drop. The copy used to go uncharged:
            // clearing the flag published nothing and counted nothing, which is the one way
            // a frame could leave this view counted by nobody.
            strandedCopy = _backPending ? _backBinding : null;
            _backPending = false;
            _backBinding = null;

            strandedPixels = _stagedPending ? _stagedBinding : null;
            _stagedPending = false;
            _stagedBinding = null;

            // Released with the bitmaps it existed to feed; at 1080p it is 8 MB.
            _staged = null;
        }

        // Charged outside the lock, as EndBinding does. The sink belongs to another
        // component, and calling into it while holding this one's lock is how lock orders
        // get crossed.
        strandedCopy?.Sink.RecordPreSwapDrop();
        strandedPixels?.Sink.RecordPreSwapDrop();

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    /// <summary>
    /// Allocates both buffers at the given size. UI thread only (posted from
    /// <see cref="RequestBuffersForStagedFrame"/>), and must be called under
    /// <see cref="_lock"/> — the producer may be mid-copy into the buffer being replaced.
    /// </summary>
    private void AllocateBuffers(int width, int height)
    {
        if (_bitmapWidth == width && _bitmapHeight == height && _back is not null)
            return;

        _back?.Dispose();
        _front?.Dispose();

        // The buffer being replaced can hold a copy that never reached a swap, and throwing
        // it away silently would lose it from the accounting. On the ordinary path there is
        // nothing here: the copy posts its swap before the next size change posts this
        // allocation, both at DispatcherPriority.Render, so the swap has already published it
        // (AResizeBehindAQueuedSwap_DrawsBothFrames pins that, and would go red if this
        // charged a drop on the ordinary path). What this covers is the path where the swap
        // post itself threw and its retry threw too: the claim is released, no swap is
        // queued, and the frame sits pending until an allocation replaces the buffer under
        // it.
        if (_backPending)
            _backBinding?.Sink.RecordPreSwapDrop();

        _backPending = false;
        _backBinding = null;

        _back = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            global::Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Premul
        );

        _front = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            global::Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Premul
        );

        _bitmapWidth = width;
        _bitmapHeight = height;
    }

    /// <summary>
    /// Copies pixel data from the decoded frame into the locked framebuffer.
    /// Handles stride mismatch between source and destination.
    /// </summary>
    private static unsafe void CopyPixels(
        ReadOnlySpan<byte> src,
        int srcStride,
        ILockedFramebuffer fb
    )
    {
        int height = fb.Size.Height;
        int dstStride = fb.RowBytes;
        int copyWidth = Math.Min(srcStride, dstStride);

        if (srcStride == dstStride && src.Length >= dstStride * height)
        {
            // Fast path: strides match, single copy
            fixed (byte* srcPtr = src)
            {
                Buffer.MemoryCopy(
                    srcPtr,
                    fb.Address.ToPointer(),
                    (long)dstStride * height,
                    (long)dstStride * height
                );
            }
        }
        else
        {
            // Slow path: line-by-line copy
            fixed (byte* srcBase = src)
            {
                byte* dstBase = (byte*)fb.Address.ToPointer();
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        srcBase + (long)y * srcStride,
                        dstBase + (long)y * dstStride,
                        dstStride,
                        copyWidth
                    );
                }
            }
        }
    }

    private static Rect ComputeLetterboxRect(double srcW, double srcH, double dstW, double dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            return default;

        double scale = Math.Min(dstW / srcW, dstH / srcH);
        double w = srcW * scale;
        double h = srcH * scale;
        double x = (dstW - w) / 2;
        double y = (dstH - h) / 2;
        return new Rect(x, y, w, h);
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to copy a presented frame into the back buffer; the frame is counted dropped."
    )]
    private static partial void LogCopyFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FrameFlowVideoView disposed.")]
    private static partial void LogDisposed(ILogger logger);
}
