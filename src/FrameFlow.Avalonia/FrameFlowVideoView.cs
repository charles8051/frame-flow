// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlow.Avalonia.Core;
using FrameFlow.Media;
using FrameFlow.Media.Core;
using FrameFlow.Media.Diagnostics;
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
/// The UI thread copies no pixels frame by frame. At 1080p60 the copy is ~8.3 MB per frame;
/// doing it in <see cref="Render"/> spent most of the ~16 ms present budget on memcpy and made
/// the view miss ticks under load, which shed frames all the way back to the decoder. The
/// producer thread can afford it, and is where ADR-0016 put it. The UI thread copies pixels in
/// two places only, neither per frame: once per size change, the blit of the frame that asked
/// for new bitmaps into them; and once per attach or rebind, the copy of a frame the sink kept
/// while the view did not present it (ADR-0085), which may stage pixels for that blit.
/// </para>
/// <para>
/// Redraws are driven by frame arrival rather than a free-running timer, so a paused or
/// stalled source costs nothing.
/// </para>
/// <para>
/// <b>Pixel format:</b> expects BGRA32 packed data in <see cref="CpuFrameData.PlaneY"/>,
/// which maps directly to Avalonia's <c>PixelFormat.Bgra8888</c> with no conversion.
/// </para>
/// <para>
/// <b>Lifecycle (ADR-0085).</b> The view keeps one sink for its whole life: its own, which the
/// first <see cref="EnsureSink"/> or attach makes, or one assigned through <see cref="Sink"/>. It
/// presents that sink while it is in the visual tree, and copies nothing before its first attach.
/// Leaving the tree stops presenting and keeps the sink, so a player built with it keeps working.
/// While the view does not present, before its first attach as after a detach, the sink keeps
/// only its newest frame, and only while a player holds its claim (see
/// <see cref="AvaloniaVideoSink"/>). The picture the view drew last stays in its own bitmaps, which
/// hold none of the player's frames, so it is drawn again when the view rejoins the tree. The
/// rejoin then presents a frame the sink kept, if any, and goes on with the same sink. Only
/// <see cref="DisposeAsync"/> is final, and only it disposes the view's own sink.
/// </para>
/// <para>
/// Hiding the view with <c>IsVisible = false</c> is not leaving the tree: a hidden view stays
/// attached and goes on copying every frame. Removing it from the visual tree is what stops it.
/// </para>
/// </remarks>
public sealed partial class FrameFlowVideoView : Control, IVideoSurface, IInBoxVideoSurface, IAsyncDisposable
{
    private static readonly Meter ViewMeter = new("FrameFlow.Avalonia.VideoView", "1.0.0");
    private static readonly Counter<long> FramesRenderedCounter = ViewMeter.CreateCounter<long>(
        "frameflow.avalonia.view.frames_rendered",
        description: "Total video frames rendered to the Avalonia video view."
    );

    private readonly ILogger<FrameFlowVideoView> _logger;

    // The view's own logger when the constructor was given none, so it follows LoggerFactory.
    private readonly SwitchableLogger<FrameFlowVideoView>? _ownLogger;

    // What the sink this view owns logs through. The sink is built with this handle rather than
    // with a logger made from the factory at that moment, so a factory assigned after the sink
    // exists still reaches it (#630). The view makes its sink when EnsureSink asks for it or when
    // it joins the visual tree without one, and a caller usually wires the logger later, from
    // OnLoaded or a click handler.
    private readonly SwitchableLogger<AvaloniaVideoSink> _sinkLogger = new();
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
    // blit reaches the swap, dropped when a newer frame supersedes it, and discarded off screen
    // when the view stops presenting its sink first.
    private byte[]? _staged;
    private bool _stagedPending;
    private int _stagedStride;
    private int _stagedWidth;
    private int _stagedHeight;
    private TimeSpan _stagedPts;
    private TimelineId _stagedTimeline;
    private AvaloniaVideoSink? _stagedSink;

    // How each buffer's frame is meant to be shown (#542). They travel with the pixels: staged,
    // copied into _back, and swapped with _front. Under _lock.
    private (SampleAspectRatio Shape, VideoRotation Rotation) _stagedGeometry;
    private (SampleAspectRatio Shape, VideoRotation Rotation) _backGeometry;
    private (SampleAspectRatio Shape, VideoRotation Rotation) _frontGeometry;

    // The sink the view holds: its own or a caller's, as the lifecycle's target says. UI thread only.
    private AvaloniaVideoSink? _sink;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2213", Justification = "The caller's factory: the view logs through it and does not own it.")]
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private int _renderedFrameCount;

    // Where the view is in its life, its binding number with it; the pure core decides every
    // move (ADR-0085). Only the UI thread moves it, and only under _lock, so a copy on the
    // producer thread compares the number it was raised under with this one under the same lock
    // (decision 5). The UI thread reads it without the lock. Nothing reads it without a lock off
    // the UI thread, so the view publishes no snapshot.
    private SurfaceLifecycle _lifecycle = SurfaceLifecycle.Initial;

    // True while _back holds a copied frame the UI thread has not swapped to the front yet.
    // Guarded by _lock. Copying over a pending back buffer is correct (latest-wins), but the
    // frame it held never drew, so the sink counts it as a drop.
    private bool _backPending;


    // PTS and timeline of the frame sitting in _back, carried to the swap so the sink can stamp
    // and report them when the frame actually becomes the front buffer. Guarded by _lock.
    private TimeSpan _backPts;
    private TimelineId _backTimeline;

    // The sink that produced the frame sitting in _back, so the swap credits the sink that
    // actually made it. Guarded by _lock, and only meaningful while _backPending.
    private AvaloniaVideoSink? _backSink;

    // True while a swap is queued on the dispatcher. Guarded by _lock.
    //
    // View-level, and the queued delegate takes no binding: it publishes whatever is in the
    // back buffer when it runs, crediting _backSink. That is what keeps the queue bounded
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
    /// Runs on the UI thread as soon as a transition has moved the binding number, before the rest
    /// of its work. For tests: it stands in for a producer thread that delivers a frame at that
    /// moment.
    /// </summary>
    internal Action? TransitionPublished { get; set; }

    /// <summary>
    /// The bitmap <see cref="Render"/> draws, or <see langword="null"/> when it draws none. For
    /// tests: the headless backend records no bitmap draws.
    /// </summary>
    internal WriteableBitmap? Picture
    {
        get
        {
            lock (_lock)
                return _front;
        }
    }

    /// <summary>
    /// Gets or sets the <see cref="AvaloniaVideoSink"/> that supplies frames to this view: its
    /// own, which <see cref="EnsureSink"/> or the first attach creates and the view disposes, or
    /// one a caller assigned, which the caller owns and the view never disposes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The view keeps its sink across a detach and presents it again when it rejoins the visual
    /// tree, whoever owns it (ADR-0085, decision 2), so this does not read <see langword="null"/>
    /// after a detach. Assigning another sink disposes the view's own or lets go of a caller's, as
    /// <see cref="DisposeAsync"/> does. After <see cref="DisposeAsync"/> this reads
    /// <see langword="null"/>. Assigning <see langword="null"/> stops the view presenting until a
    /// sink is assigned or made again; the picture it drew last stays.
    /// </para>
    /// <para>
    /// The assignment first claims the outgoing sink, as a player does, so that no player can be
    /// built against it while it is replaced, and releases the claim once it is disposed or let go.
    /// A caller's sink the view lets go of keeps a frame only for a player that holds its claim.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A player or pass holds the outgoing sink, and would be left feeding a sink no view presents.
    /// The message names the holder, and nothing has changed. Dispose that player first.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// A sink is assigned after <see cref="DisposeAsync"/>: nothing would present it or end its
    /// binding.
    /// </exception>
    public AvaloniaVideoSink? Sink
    {
        get => _sink;
        set
        {
            if (ReferenceEquals(_sink, value))
                return;

            var transition = _lifecycle.Assign(value is not null);
            ObjectDisposedException.ThrowIf(transition.Refused, this);

            // Before anything changes, and released once the outgoing sink is disposed or let go
            // (ADR-0085, decision 2).
            using var claim = ClaimOutgoingSink();

            Complete(Apply(transition, value));
        }
    }

    // How the view's own claim names it, in the error a build gets while the view replaces its sink.
    private const string ReplacingClaimant = "a FrameFlowVideoView replacing its sink";

    /// <summary>
    /// Claims the sink the <see cref="Sink"/> setter replaces, as a player would: while the view
    /// holds the claim no build can take the sink, and when a player already holds it, the
    /// assignment stops here. Returns the lease, or <see langword="null"/> when there is no
    /// outgoing sink or its owner has disposed it.
    /// </summary>
    private IDisposable? ClaimOutgoingSink()
    {
        if (_sink is not IClaimableSink outgoing)
            return null;

        IDisposable? lease;
        try
        {
            lease = outgoing.TryClaim(ReplacingClaimant);
        }
        catch (ObjectDisposedException)
        {
            // A caller's sink that its owner disposed while the view held it presents nothing,
            // whoever still feeds it, so there is nothing for a claim to protect.
            return null;
        }

        return lease
            ?? throw new InvalidOperationException(
                $"The video sink {nameof(AvaloniaVideoSink)} is held by "
                    + $"{outgoing.Claimant ?? "a player or pass"}. Replacing it would leave that player "
                    + "feeding a sink no view presents: await DisposeAsync on the player or pass built "
                    + "against it before assigning Sink."
            );
    }

    /// <summary>
    /// Gets or sets the logger factory used by the sink the view owns, and by the view itself
    /// when it was constructed without a logger. Defaults to <see cref="NullLoggerFactory"/>.
    /// </summary>
    /// <remarks>
    /// Takes effect whenever it is assigned, before or after the view joins the visual tree and
    /// before or after <see cref="EnsureSink"/>: the owned sink logs through the newest factory
    /// from then on. A sink assigned through <see cref="Sink"/> keeps the logger its owner gave it.
    /// </remarks>
    public ILoggerFactory LoggerFactory
    {
        get => _loggerFactory;
        set
        {
            _loggerFactory = value ?? NullLoggerFactory.Instance;
            _sinkLogger.Use(_loggerFactory);
            _ownLogger?.Use(_loggerFactory);
        }
    }

    /// <summary>
    /// Initializes a new <see cref="FrameFlowVideoView"/>.
    /// </summary>
    public FrameFlowVideoView()
        : this(null) { }

    /// <summary>
    /// Initializes a new <see cref="FrameFlowVideoView"/> with an optional logger. Without one,
    /// the view logs through <see cref="LoggerFactory"/>.
    /// </summary>
    public FrameFlowVideoView(ILogger<FrameFlowVideoView>? logger)
    {
        if (logger is null)
        {
            _ownLogger = new SwitchableLogger<FrameFlowVideoView>();
            _logger = _ownLogger;
        }
        else
        {
            _logger = logger;
        }

        ClipToBounds = true;
    }

    /// <summary>
    /// Returns the sink this view presents: the view's own, which the first call makes, or one
    /// assigned through <see cref="Sink"/>. It is the same sink for the view's whole life, across
    /// trips out of the visual tree, unless <see cref="Sink"/> is assigned (ADR-0085, decision 2).
    /// The view's own sink logs through <see cref="LoggerFactory"/>, including a factory assigned
    /// after this call, and the view disposes it in <see cref="DisposeAsync"/>, and only there.
    /// </summary>
    /// <returns>The view's current <see cref="AvaloniaVideoSink"/> instance.</returns>
    /// <remarks>
    /// Callers that need a sink reference before the view attaches (e.g. fluent
    /// player builders) should invoke this method to materialize the sink eagerly. The view
    /// presents nothing until it is attached: until then the sink keeps its newest frame for a
    /// player that holds its claim, and the first attach presents it (ADR-0085, decision 3).
    /// </remarks>
    /// <exception cref="ObjectDisposedException"><see cref="DisposeAsync"/> has run.</exception>
    public AvaloniaVideoSink EnsureSink()
    {
        var ensure = _lifecycle.EnsureOwned();
        ObjectDisposedException.ThrowIf(ensure.Refused, this);
        Complete(Apply(ensure));
        return _sink!;
    }

    // ── IVideoSurface ─────────────────────────────────────────────
    // Lets FrameFlowPlayerView host this CPU surface or a GPU presenter
    // interchangeably (the chrome binds to the player, not the surface).
    Control IVideoSurface.Control => this;
    IVideoSink IVideoSurface.AttachSink(ILoggerFactory loggerFactory)
    {
        ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
        LoggerFactory = loggerFactory;
        return EnsureSink();
    }

    // The player view claims it before it swaps this view out (ADR-0085, decision 7).
    IClaimableSink? IInBoxVideoSurface.Sink => _sink;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A disposed view makes, binds and presents nothing, and its attach is not an error.
        if (_lifecycle.IsDisposed)
            return;

        Complete(Apply(_lifecycle.Attach()));

        // Materialize an internal sink when no caller has assigned one.
        // Gives consumers a zero-ceremony "drop the view in XAML and it
        // just works" experience. The same one on every later attach.
        EnsureSink();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Stops presenting and keeps the sink. A step that throws does not stop the others, and
        // what it threw is logged rather than thrown into the visual tree's detach.
        if (Fault(Apply(_lifecycle.Detach())) is { } fault)
            LogDetachFailed(_logger, fault);

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Tears the view down for good: it stops presenting, disposes its own sink, even while a
    /// player still feeds it, which then has every frame disposed on arrival, and lets go of a
    /// caller's sink without disposing it. It releases its bitmaps, as <see cref="Clear"/> does.
    /// <see cref="Sink"/> then reads <see langword="null"/>, and <see cref="EnsureSink"/>,
    /// <see cref="IVideoSurface.AttachSink"/> and assigning a sink throw
    /// <see cref="ObjectDisposedException"/>. A second call does nothing. Call on the UI thread.
    /// </summary>
    /// <remarks>
    /// Avalonia never disposes a control, so a view that is never disposed keeps its own sink for
    /// as long as it is referenced (ADR-0085, decision 2).
    /// </remarks>
    /// <returns>
    /// A completed task, or one faulted with what a step threw, such as a frame that failed to
    /// release, after every step has run.
    /// </returns>
    public ValueTask DisposeAsync()
    {
        if (_lifecycle.IsDisposed)
            return ValueTask.CompletedTask;

        var faults = Apply(_lifecycle.Dispose());
        Run(ReleaseBuffers, ref faults);
        LogDisposed(_logger);

        return Fault(faults) is { } fault ? ValueTask.FromException(fault) : ValueTask.CompletedTask;
    }

    /// <summary>
    /// Moves the view to the state the lifecycle core chose and does the work it named, in the
    /// core's order (ADR-0085): unbind, stop presenting, dispose or let go of the sink, make or
    /// take up a sink, bind, start presenting. Stopping, which on this view only takes the sink off
    /// screen, runs first, before the number moves. UI thread.
    /// </summary>
    /// <param name="transition">What the core decided.</param>
    /// <param name="assigned">The sink a caller assigned, which the borrow step takes up.</param>
    /// <returns>
    /// What the steps threw, or <see langword="null"/>. Every step runs even when one before it
    /// threw: the core has moved on and will not ask for that work again, so a frame that fails to
    /// release must not leave the sink undisposed or the next sink unbound.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The core refused the event: the view is disposed.</exception>
    private List<Exception>? Apply(SurfaceTransition transition, AvaloniaVideoSink? assigned = null)
    {
        ObjectDisposedException.ThrowIf(transition.Refused, this);

        var work = transition.Work;
        List<Exception>? faults = null;

        // The view stops presenting the held sink, or lets it go: the sink goes off screen first,
        // before the number moves, which is all stopping takes on this view. A frame arriving in
        // between is then kept or discarded by the sink's off-screen rule, or copied under the
        // binding that is ending and let go of by the unbind, and either way counted as discarded
        // off screen (ADR-0085, decision 2). After the number had moved, the sink would still be on
        // screen with no callback left to take from it, so a second frame would replace the first
        // as a drop.
        if ((work & (SurfaceWork.StopPresenting | SurfaceWork.DisposeTarget | SurfaceWork.ForgetTarget)) != 0)
            Run(StopPresenting, ref faults);

        // Before the rest of the work, under the lock a copy on the producer thread compares it
        // under: a callback raised under a binding this ends copies nothing from here on.
        lock (_lock)
            _lifecycle = transition.Next;

        TransitionPublished?.Invoke();

        if (work.HasFlag(SurfaceWork.Unbind))
            Run(UnbindSink, ref faults);
        if (work.HasFlag(SurfaceWork.DisposeTarget))
            Run(DisposeSink, ref faults);
        if (work.HasFlag(SurfaceWork.ForgetTarget))
            Run(ForgetSink, ref faults);
        if (work.HasFlag(SurfaceWork.Create))
            Run(() => TakeUp(new AvaloniaVideoSink(_sinkLogger)), ref faults);
        if (work.HasFlag(SurfaceWork.Borrow))
            Run(() => TakeUp(assigned!), ref faults);
        if (work.HasFlag(SurfaceWork.Bind))
            Run(BindSink, ref faults);
        if (work.HasFlag(SurfaceWork.StartPresenting))
            Run(StartPresenting, ref faults);

        return faults;
    }

    private static void Run(Action step, ref List<Exception>? faults)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            (faults ??= []).Add(ex);
        }
    }

    private static Exception? Fault(List<Exception>? faults) =>
        faults switch
        {
            null => null,
            [var one] => one,
            _ => new AggregateException(faults),
        };

    /// <summary>Throws what a transition a caller asked for threw, once every step has run.</summary>
    private static void Complete(List<Exception>? faults)
    {
        if (Fault(faults) is { } fault)
            ExceptionDispatchInfo.Throw(fault);
    }

    // ── The sink steps (ADR-0085, decision 2). UI thread, in Apply's order. ──
    // The sink the view holds is presented exactly while the view presents: StartPresenting and
    // StopPresenting set it, and a sink taken up or let go of in between is set to match.

    private void TakeUp(AvaloniaVideoSink sink)
    {
        _sink = sink;
        sink.SetPresented(_lifecycle.Presents);
    }

    private void DisposeSink()
    {
        var sink = _sink!;
        _sink = null;

        // AvaloniaVideoSink.DisposeAsync returns a completed ValueTask.
        // Guard with IsCompleted so the analyzer accepts the synchronous
        // GetResult call.
        var disposeTask = sink.DisposeAsync();
        if (disposeTask.IsCompleted)
            disposeTask.GetAwaiter().GetResult();
        else
            disposeTask.AsTask().GetAwaiter().GetResult();
    }

    // The sink is already off screen (Apply), so it keeps a frame only while a claim on it stands.
    // The Sink setter releases its own claim as it returns, which lets go of the frame.
    private void ForgetSink() => _sink = null;

    /// <summary>
    /// Binds the held sink's arrival callback to this view, under the binding number the
    /// transition has just set: a callback the sink raises later carries it, and copies nothing
    /// once the number has moved (ADR-0085, decision 5).
    /// </summary>
    private void BindSink()
    {
        var sink = _sink!;
        var binding = _lifecycle.Binding;

        // A view that presents as it binds, on a reattach, presents the sink from this point: a
        // frame arriving before StartPresenting is not one to drop.
        if (_lifecycle.Presents)
            sink.SetPresented(true);

        sink.FrameArrived = () => OnFrameArrived(sink, binding);

        // A frame may already be waiting: fed before the sink was bound, or installed in the
        // instant before the hook went on. No later arrival is guaranteed to come and collect it:
        // on a paused or ended stream that frame is the one meant to be on screen. A view not
        // presenting yet leaves it in the sink.
        if (sink.HasPendingFrame)
            OnFrameArrived(sink, binding);
    }

    /// <summary>
    /// Takes the arrival callback off the held sink, and lets go of what the binding left
    /// undrawn: a copy waiting for its swap and pixels waiting for their buffers. A callback
    /// already running holds <see cref="_lock"/> for its whole copy, so it finished before the
    /// number moved; one that starts later sees the number moved under the same lock, and copies
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The two are different frames and each is counted, against the sink that produced it, as
    /// discarded off screen, not as a drop: the view stopped presenting that sink before they drew
    /// (ADR-0085, decision 2). One flag covering both once counted one frame for two.
    /// </remarks>
    private void UnbindSink()
    {
        AvaloniaVideoSink? strandedCopy;
        AvaloniaVideoSink? strandedPixels;

        lock (_lock)
        {
            strandedCopy = _backPending ? _backSink : null;
            _backPending = false;
            _backSink = null;

            strandedPixels = _stagedPending ? _stagedSink : null;
            _stagedPending = false;
            _stagedSink = null;
        }

        _sink!.FrameArrived = null;

        // Outside the lock: the sink belongs to another component.
        strandedCopy?.RecordDiscardedOffScreen();
        strandedPixels?.RecordDiscardedOffScreen();
    }

    /// <summary>
    /// Starts presenting the held sink: it keeps what it is given for the view again, and a frame
    /// it kept while the view did not present is copied first.
    /// </summary>
    /// <remarks>
    /// That frame is the one meant to be on screen, and no later arrival is sure to come for it:
    /// the player may be paused or ended, or the item may have one frame (#287). Copied here, on
    /// the UI thread, once per attach; every other copy stays on the producer thread
    /// (ADR-0016, decision 1).
    /// </remarks>
    private void StartPresenting()
    {
        var sink = _sink!;
        sink.SetPresented(true);

        if (sink.HasPendingFrame)
            OnFrameArrived(sink, _lifecycle.Binding);
    }

    /// <summary>
    /// Stops presenting the held sink: it goes off screen, and from here keeps a frame only for a
    /// player that holds its claim, and lets go of the one it holds now when no player does.
    /// <see cref="Apply"/> runs this before it moves the binding number, for a sink it stops
    /// presenting, disposes or lets go of. The view keeps its bitmaps: they are its own copies and
    /// hold none of the player's frames, so the picture it drew last is drawn again when it rejoins
    /// the tree (ADR-0085, decision 2).
    /// </summary>
    private void StopPresenting() => _sink!.SetPresented(false);

    /// <summary>
    /// Takes the frame the sink just installed and copies it into the back buffer. Runs on
    /// the presenting (graph) thread — never the UI thread (ADR-0016 Decision 1) — except for
    /// the sweeps as the view binds and starts presenting.
    /// </summary>
    /// <param name="sink">
    /// The sink the callback was bound to. Everything is read from it rather than from
    /// <c>_sink</c>, so the frame is taken from, and accounted to, the sink that produced it.
    /// </param>
    /// <param name="binding">
    /// The binding number the callback was bound under. A callback that outlives its binding, or
    /// one raised before the view presents, copies nothing, and the frame stays with the sink,
    /// which keeps or drops it (ADR-0085, decisions 2, 3 and 5).
    /// </param>
    private void OnFrameArrived(AvaloniaVideoSink sink, long binding)
    {
        // The producer must not fault because a presenter could not draw. The sink also
        // catches, but recording the drop needs to happen here where the frame is known.
        try
        {
            CopyArrivedFrame(sink, binding);
        }
        catch (Exception ex)
        {
            LogCopyFailed(_logger, ex);
        }
    }

    private void CopyArrivedFrame(AvaloniaVideoSink sink, long binding)
    {
        bool post = false;

        // Held across the whole body so a detach cannot land mid-copy: a transition moves the
        // binding number under the same lock, so it either wins the race outright or waits
        // here. The number is compared here, where the copy commits, not read earlier: a read
        // acted on later would race the detach (ADR-0085, decision 5).
        lock (_lock)
        {
            if (binding != _lifecycle.Binding || !_lifecycle.Presents)
                return;

            // Take without counting: whether this frame is presented or dropped is not known
            // until its dimensions are in hand, and every path below records exactly one of
            // the two. RenderPendingFrame would have counted it presented on the way out.
            var frame = sink.TakePendingFrame();
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
                    sink.RecordPreSwapDrop();
                    return;
                }

                var data = cpu.Value;

                // Nothing can be drawn at a non-positive size and no bitmap can be allocated
                // for one, so staging it would only fail the allocation on the UI thread.
                // Charged here so it still lands in exactly one bucket.
                if (data.Width <= 0 || data.Height <= 0)
                {
                    sink.RecordPreSwapDrop();
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
                        _stagedSink?.RecordPreSwapDrop();
                    }

                    StagePixelsLocked(data, frame.Pts, frame.Timeline, (frame.SampleAspectRatio, frame.Rotation), sink);
                    RequestBuffersForStagedFrame();
                    return;
                }

                // Latest-wins into the back buffer. If the UI thread has not swapped the
                // previous copy in yet, the frame it held never drew — count that one as a
                // drop. It was never counted presented; that happens at the swap.
                if (_backPending)
                    sink.RecordPreSwapDrop();

                copyStarted = true;
                using (var fb = _back.Lock())
                    CopyPixels(data.PlaneY.Span, data.StrideY, fb);
                FrameCopyMetrics.Record(FrameCopySite.PresenterUpload);

                _backPts = frame.Pts;
                _backTimeline = frame.Timeline;
                _backGeometry = (frame.SampleAspectRatio, frame.Rotation);
                _backSink = sink;
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
                sink.RecordPreSwapDrop();
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
            // supersedes it and charges the drop, or the unbind charges it. Clearing it here
            // would discard a newer copy that landed while this post was failing, and
            // charging it here would double-count one that the unbind had already charged.
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
    private void StagePixelsLocked(
        CpuFrameData data,
        TimeSpan pts,
        TimelineId timeline,
        (SampleAspectRatio Shape, VideoRotation Rotation) geometry,
        AvaloniaVideoSink sink)
    {
        var needed = data.StrideY * data.Height;
        if (_staged is null || _staged.Length < needed)
            _staged = new byte[needed];

        data.PlaneY.Span[..needed].CopyTo(_staged);
        FrameCopyMetrics.Record(FrameCopySite.PresenterUpload);
        _stagedPending = true;
        _stagedStride = data.StrideY;
        _stagedWidth = data.Width;
        _stagedHeight = data.Height;
        _stagedPts = pts;
        _stagedTimeline = timeline;
        _stagedGeometry = geometry;
        _stagedSink = sink;
    }

    /// <summary>
    /// Blits the staged pixels into the back buffer, if any are staged and the buffer that
    /// landed is the size they asked for. Returns whether there is now something to swap.
    /// Under <see cref="_lock"/>, on the UI thread, immediately after the allocation.
    /// </summary>
    /// <remarks>
    /// This is one of the two pixel copies the UI thread does, and it happens once per size
    /// change rather than per frame — no more than the cost the mismatch branch already
    /// accepted when it discarded the frame. The other is the copy of a frame the sink kept
    /// while the view did not present it, once per attach or rebind (<c>BindSink</c> and
    /// <c>StartPresenting</c>); when that frame needs new bitmaps it is staged and blitted here
    /// too, so a first attach can make both. Every other copy stays on the producer thread,
    /// which is what ADR-0016 moved it there for.
    /// </remarks>
    private bool BlitStagedFrameLocked()
    {
        if (!_stagedPending || _staged is null)
            return false;

        var sink = _stagedSink;
        var stride = _stagedStride;
        var width = _stagedWidth;
        var height = _stagedHeight;
        var pts = _stagedPts;

        _stagedPending = false;
        _stagedSink = null;

        try
        {
            // An allocation for a newer size that superseded these pixels: they are owed their
            // drop rather than drawn at the wrong geometry. A binding that ended between the
            // request and this callback has already emptied the slot, under this lock.
            if (_back is null || sink is null || _bitmapWidth != width || _bitmapHeight != height)
            {
                sink?.RecordPreSwapDrop();
                return false;
            }

            using (var fb = _back.Lock())
                CopyPixels(_staged.AsSpan(0, stride * height), stride, fb);
            FrameCopyMetrics.Record(FrameCopySite.PresenterUpload);

            _backPts = pts;
            _backTimeline = _stagedTimeline;
            _backGeometry = _stagedGeometry;
            _backSink = sink;
            _backPending = true;
            return true;
        }
        catch
        {
            // Same rule as the producer-side copy: a throw partway through leaves _back
            // unpublishable, and these pixels land in exactly one bucket either way.
            _backPending = false;
            sink?.RecordPreSwapDrop();
            throw;
        }
    }

    /// <summary>
    /// Publishes the back buffer and asks for a redraw. UI thread only; the swap is the
    /// ADR-0016 hand-off point, and the only pixel-buffer work the UI thread does per frame. It
    /// copies pixels only once per size change (the blit) and once per attach or rebind (a frame
    /// the sink kept).
    /// </summary>
    /// <remarks>
    /// Takes no binding: it publishes whatever the back buffer holds when it runs and credits
    /// the sink that produced it. Every transition that ends a binding (a detach, a sink
    /// replaced or cleared, a dispose) clears <c>_backPending</c> and charges the stranded frame
    /// under <see cref="_lock"/>, which this takes too, so what is here is always the current
    /// binding's: nothing is published, and no <c>FramePresented</c> raised, for a binding that
    /// has gone, and nothing is misfiled onto a replacement (ADR-0085, decision 5).
    /// </remarks>
    private void SwapAndInvalidate()
    {
        TimeSpan pts;
        TimelineId timeline;
        AvaloniaVideoSink? producer;

        lock (_lock)
        {
            _swapPosted = false;

            // Nothing to publish: superseded and charged, or cleared as a binding ended.
            if (!_backPending)
                return;

            (_front, _back) = (_back, _front);
            (_frontGeometry, _backGeometry) = (_backGeometry, _frontGeometry);
            _backPending = false;
            pts = _backPts;
            timeline = _backTimeline;
            producer = _backSink;
        }

        // Count the present HERE, not at the copy, and against the sink that produced it. A
        // frame overwritten in the back buffer before this ran never drew and is counted a
        // drop instead, so no frame lands in both counters and FramesPresented means what it
        // says.
        producer?.RecordPresented(pts, timeline);
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

                        // A binding that ended, or a Clear, between the request and here
                        // empties the slot.
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
            _stagedSink = null;
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
        (SampleAspectRatio Shape, VideoRotation Rotation) geometry;
        lock (_lock)
        {
            bitmapToRender = _front;
            geometry = _frontGeometry;
        }

        if (bitmapToRender is null)
        {
            base.Render(context);
            return;
        }

        // Letterboxed at the frame's display shape, and turned upright (#542).
        var srcW = bitmapToRender.PixelSize.Width;
        var srcH = bitmapToRender.PixelSize.Height;
        var placement = VideoPlacement.Fit(Bounds.Width, Bounds.Height, srcW, srcH, geometry.Shape, geometry.Rotation);
        if (placement.IsEmpty)
            return;

        var srcRect = new Rect(0, 0, srcW, srcH);
        var drawRect = new Rect(placement.DrawX, placement.DrawY, placement.DrawWidth, placement.DrawHeight);
        if (placement.RotationRadians == 0)
        {
            context.DrawImage(bitmapToRender, srcRect, drawRect);
            return;
        }

        var centre = drawRect.Center;
        var turn = Matrix.CreateTranslation(-centre.X, -centre.Y)
            * Matrix.CreateRotation(placement.RotationRadians)
            * Matrix.CreateTranslation(centre.X, centre.Y);
        using (context.PushTransform(turn))
            context.DrawImage(bitmapToRender, srcRect, drawRect);
    }

    /// <summary>
    /// Clears the rendered surface and releases bitmap resources.
    /// </summary>
    /// <remarks>
    /// The view keeps its bitmaps across a trip out of the visual tree, so that the picture it
    /// drew last is drawn again when it rejoins (ADR-0085, decision 2). This and
    /// <see cref="DisposeAsync"/> are what release them.
    /// </remarks>
    public void Clear()
    {
        AvaloniaVideoSink? strandedCopy;
        AvaloniaVideoSink? strandedPixels;

        lock (_lock)
        {
            ReleaseBuffersLocked();

            // Both a copy waiting for its swap and pixels waiting for their buffers are
            // thrown away here, and each is owed its drop. The copy used to go uncharged:
            // clearing the flag published nothing and counted nothing, which is the one way
            // a frame could leave this view counted by nobody.
            strandedCopy = _backPending ? _backSink : null;
            _backPending = false;
            _backSink = null;

            strandedPixels = _stagedPending ? _stagedSink : null;
            _stagedPending = false;
            _stagedSink = null;
        }

        // Charged outside the lock, as the unbind does. The sink belongs to another
        // component, and calling into it while holding this one's lock is how lock orders
        // get crossed.
        strandedCopy?.RecordPreSwapDrop();
        strandedPixels?.RecordPreSwapDrop();

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    /// <summary>
    /// Releases the bitmaps as <see cref="DisposeAsync"/>'s last step, and draws nothing from
    /// then on. UI thread. The unbind before it has already let go of a copy or pixels the
    /// binding left undrawn.
    /// </summary>
    private void ReleaseBuffers()
    {
        lock (_lock)
            ReleaseBuffersLocked();

        InvalidateVisual();
    }

    private void ReleaseBuffersLocked()
    {
        _front?.Dispose();
        _front = null;
        _back?.Dispose();
        _back = null;
        _bitmapWidth = 0;
        _bitmapHeight = 0;

        // Released with the bitmaps it existed to feed; at 1080p it is 8 MB.
        _staged = null;
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
            _backSink?.RecordPreSwapDrop();

        _backPending = false;
        _backSink = null;

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

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to copy a presented frame into the back buffer; the frame is counted dropped."
    )]
    private static partial void LogCopyFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FrameFlowVideoView disposed.")]
    private static partial void LogDisposed(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A step of leaving the visual tree threw; the view stopped presenting and kept its sink all the same."
    )]
    private static partial void LogDetachFailed(ILogger logger, Exception ex);
}
