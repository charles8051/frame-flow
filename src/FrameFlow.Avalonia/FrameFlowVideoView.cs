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
        bool strandedFrame;

        lock (_lock)
        {
            binding = _binding;
            if (binding is null)
                return;

            binding.Detached = true;
            _binding = null;

            strandedFrame = _backPending;
            _backPending = false;
            _backBinding = null;
        }

        binding.Sink.FrameArrived = null;
        if (strandedFrame)
            binding.Sink.RecordPreSwapDrop();
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

                // Wrong size or nothing allocated yet: the WriteableBitmaps are created on
                // the UI thread, so post the allocation and let this frame go. Costs one
                // frame on the first frame and on each resize, not per frame.
                if (_back is null || _bitmapWidth != data.Width || _bitmapHeight != data.Height)
                {
                    RequestBuffers(data.Width, data.Height);
                    binding.Sink.RecordPreSwapDrop();
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
    /// Posts a back/front buffer allocation to the UI thread. Must be called under
    /// <see cref="_lock"/>.
    /// </summary>
    private void RequestBuffers(int width, int height)
    {
        if (_allocationPosted)
            return;
        _allocationPosted = true;

        Dispatcher.UIThread.Post(
            () =>
            {
                lock (_lock)
                {
                    _allocationPosted = false;
                    AllocateBuffers(width, height);
                }
            },
            DispatcherPriority.Render
        );
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
        lock (_lock)
        {
            _front?.Dispose();
            _front = null;
            _back?.Dispose();
            _back = null;
            _bitmapWidth = 0;
            _bitmapHeight = 0;
            _backPending = false;
            _backBinding = null;
        }

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    /// <summary>
    /// Allocates both buffers at the given size. UI thread only (posted from
    /// <see cref="RequestBuffers"/>), and must be called under <see cref="_lock"/> — the
    /// producer may be mid-copy into the buffer being replaced.
    /// </summary>
    private void AllocateBuffers(int width, int height)
    {
        if (_bitmapWidth == width && _bitmapHeight == height && _back is not null)
            return;

        _back?.Dispose();
        _front?.Dispose();
        _backPending = false;

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
