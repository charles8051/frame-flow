// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FrameFlow.Avalonia.Core;
using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Avalonia.Windows.Diagnostics;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Core;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// An Avalonia <see cref="Control"/> that presents hardware-decoded
/// <see cref="GpuVideoFrame"/>s without leaving the GPU: it color-converts the frame into a
/// shared keyed-mutex BGRA texture and imports that into the compositor via
/// <see cref="ICompositionGpuInterop"/> (ADR-0016 amendment). A D3D11VA frame is copied into a
/// shareable staging texture and converted on D3D11; a D3D12VA frame is converted on its decoder's
/// D3D12 device and copied across by a D3D11 device (#429). One GPU copy and no GPU→CPU readback,
/// no <c>WriteableBitmap</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pairs with <see cref="CompositionInteropVideoSink"/>: the sink holds the latest incoming
/// frame and signals its arrival; this control then presents it on the UI thread. Use
/// <see cref="EnsureSink"/> to get the <see cref="IVideoSink"/> to hand to the player.
/// </para>
/// <para>
/// Windows / D3D11 only. Latest-frame-wins, converted on the UI thread with a pixel shader
/// (ADR-0063), or through the video processor while <see cref="DriverSuperResolution"/> engages
/// (#560). NV12 and P010 frames are converted on either path (#559); HDR is not (#548).
/// Device-lost handling and ordered imported-image teardown are implemented (investigation
/// 2026-06-12).
/// </para>
/// <para>
/// <b>Lifecycle (ADR-0085).</b> The view keeps one sink for its whole life: its own, which the
/// first <see cref="EnsureSink"/> or <see cref="Initialize"/> makes, or one assigned through
/// <see cref="Sink"/>. It presents that sink while it is in the visual tree and initialized.
/// Leaving the tree stops presenting: the compositor side goes and the stall watchdog stops, and
/// the sink stays, so a player built with it keeps working. While the view does not present, before
/// its first attach as after a detach, the sink keeps only its newest frame, and only while a player
/// holds its claim (see <see cref="CompositionInteropVideoSink"/>). Rejoining the tree sets the
/// surface up again, presents that frame first and goes on with the same sink. Only
/// <see cref="DisposeAsync"/> is final, and only it disposes the view's own sink.
/// </para>
/// <para>
/// <b>Teardown ordering (investigation 2026-06-12).</b> The producer's shared keyed-mutex
/// ring is co-owned with the compositor, which acquires it on its render thread with an
/// effectively infinite timeout. Destroying the producer ring while the compositor still
/// holds a copy — during a display/composition transition (e.g. a remote-desktop connect)
/// where the compositor is wedged — hangs the destroying <c>Release</c> forever. So a detach
/// or <see cref="DisposeAsync"/> tears down in a strict order: stop producing, detach from the
/// compositor and dispose the imported images <i>through the compositor render thread</i>,
/// drain in-flight presents with a bounded wait, and only then dispose the producer; if the
/// drain times out (compositor wedged) the producer disposal is deferred to a background
/// reaper rather than blocking the UI thread or leaking. Each teardown takes its own
/// attachment's ring and producers on the UI thread before it waits, so a view that rejoins the
/// tree meanwhile builds new ones, and the teardown never disposes or presents those.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoView : Control, IVideoSurface, IAsyncDisposable
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2213", Justification = "The caller's factory: the view logs through it and does not own it.")]
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    // Handed out rather than replaced, so Initialize can move the logging after the stall
    // watchdog, the converters and the owned sink were built with it. EnsureSink readies the
    // view without a logger when Initialize has not run (#613), and a factory that comes later
    // still reaches all of them.
    private readonly SwitchableLogger<CompositionInteropVideoView> _logger = new();
    private readonly SwitchableLogger<CompositionInteropVideoSink> _sinkLogger = new();

    // The sink the view holds: its own or a caller's, as the lifecycle's target says. UI thread only.
    private CompositionInteropVideoSink? _sink;

    private Compositor? _compositor;
    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _surfaceVisual;

    private D3D11Nv12SharedConverter? _gpuConverter; // GPU zero-copy source, D3D11VA frames
    private D3D12YuvSharedConverter? _d3d12Converter; // GPU zero-copy source, D3D12VA frames
    private D3D11BgraUploader? _cpuUploader; // CPU upload fallback source
    private bool? _activeIsGpu; // which source the imported[] ring is currently bound to

    private readonly ICompositionImportedGpuImage?[] _imported =
        new ICompositionImportedGpuImage?[D3D11Nv12SharedConverter.BufferCount];
    private readonly Task?[] _presentTasks = new Task?[D3D11Nv12SharedConverter.BufferCount];
    private int _nextBuffer;
    private int _videoWidth;
    private int _videoHeight;

    // ── Driver super resolution (#560). UI thread only, except the status. ──
    private bool _driverSuperResolution;
    private bool _superResolutionToggled;
    private bool _superResolutionFailed;
    private OutputSettle _outputSettle;
    private int _superResolutionStatus; // a SuperResolutionStatus, read from any thread
    private nint _vendorDevice;
    private uint _vendorId;

    // How long a new output has to be wanted, unchanged, before the converter is rebuilt for it.
    private static readonly long OutputSettleTicks = Stopwatch.Frequency / 5;

    // How the frames are meant to be shown (#542): the layout fits the display shape and turns
    // the surface upright. UI thread only, like the size above.
    private SampleAspectRatio _videoShape = SampleAspectRatio.Square;
    private VideoRotation _videoRotation;

    // ── Two-stage present accounting (ADR-0064 §Observability) ────────────────
    // _framesPresented counts at ENQUEUE: the frame's UpdateWithKeyedMutexAsync hand-off was
    // posted to the compositor. _framesCommitted counts at COMMIT: that hand-off task actually
    // completed (the compositor acquired the shared texture, snapshotted it, and released the
    // keyed mutex back to the producer). The gap between them is the diagnostic: if enqueued
    // climbs while committed stays flat, frames are reaching the compositor's queue but the
    // compositor is not draining them — "frames not reaching the screen", a class the old
    // enqueue-only counter (and the present-stall watchdog that keyed off it) was blind to.
    private int _framesPresented;
    private int _framesCommitted;
    private int _framesDropped;

    // Last-presented PTS + wallclock, stamped on each successful compositor present (enqueue)
    // so the sink's diagnostics snapshot can report them (A/V drift). -1 = none yet.
    private long _lastPresentedPtsTicks = -1;
    private long _lastPresentedAtUtcTicks = -1;
    // Wallclock of the most recent committed present (the compositor actually drained a
    // hand-off). -1 = none yet. Pairs with _framesCommitted for the
    // ADR-0064 §Observability "output stalled" view.
    private long _lastCommittedAtUtcTicks = -1;

    private bool _loggedGpuLive;
    private bool _loggedD3D12Live;
    private bool _loggedCpuLive;
    private bool _warnedUnpresentable;
    private bool _warnedPresentFailure;

    // Where the view is in its life, its binding number with it; the pure core decides every
    // move (ADR-0085). Only the UI thread moves it, and only under _presentGate, so work that
    // commits on another thread compares the number it started under with this one under the same
    // gate (decision 5). The UI thread reads it without the gate.
    private SurfaceLifecycle _lifecycle = SurfaceLifecycle.AwaitingInitialize;

    /// <summary>
    /// What a reader that takes no lock sees of the view (ADR-0085, decision 5): whether it
    /// presents, its binding number and its sink. Published whole, with <see cref="Volatile"/>,
    /// after each transition's work. The stall watchdog's timer reads it.
    /// </summary>
    private sealed record Snapshot(bool Presents, long Binding, CompositionInteropVideoSink? Sink);

    private Snapshot _snapshot = new(Presents: false, Binding: 0, Sink: null);

    // Guards the binding number and the queued-present claim. Taken briefly by the producer
    // thread (to claim a post), by a present's commit (to check its binding), and by the UI
    // thread (to move the number and to release the claim); never held across a present or
    // a handler.
    private readonly object _presentGate = new();

    // True while a present is queued on the dispatcher. At most one: a frame arriving while
    // one is pending needs no second post, because the queued present takes whatever the
    // slot holds when it runs — which is the newer frame. Without this, a producer running
    // ahead of a busy UI thread queues one delegate per frame.
    private bool _presentPosted;
    private int _presentsPosted;

    // Completes once the D3D12 converter the last teardown took has been disposed, or at once when
    // it took none. UI thread only. A new attachment presents nothing until then (ADR-0085,
    // decision 2): that converter holds decoder frames for its draws until it is disposed.
    private Task _d3d12Retired = Task.CompletedTask;

    // Present cadence/cost instrumentation (#128). UI thread only — PresentPending is the
    // sole caller — so it needs no synchronization. Reports about every 2 s at 60 Hz, and is
    // reset on Initialize and when an attachment starts presenting, so a window cannot span a
    // detach.
    private readonly PresenterTickMeter _tickMeter = new(Stopwatch.Frequency);

    // Present-stall watchdog (investigation 2026-06-12 §9). _lastBltStartedTicks is stamped before
    // each VideoProcessorBlt, so a hung Blt leaves it stale; the watchdog samples it off the UI thread.
    private long _lastBltStartedTicks;
    private PresenterStallWatchdog? _stallWatchdog;

    /// <summary>Whether the stall watchdog is running. For tests.</summary>
    internal bool IsWatchingForStalls => _stallWatchdog is not null;

    /// <summary>How many presents the view has posted to the dispatcher. For tests.</summary>
    internal int PresentsPosted => Volatile.Read(ref _presentsPosted);

    /// <summary>The binding number the last transition published. For tests.</summary>
    internal long Binding => Volatile.Read(ref _snapshot).Binding;

    // Bounded wait for in-flight presents to drain at teardown. On the happy path the
    // compositor finishes each UpdateWithKeyedMutexAsync in well under a frame, so this is
    // never approached; it only bounds the wedged-compositor case (Splashtop display
    // transition) before we defer producer disposal to the background reaper. Kept short so
    // a Dispose()/window-close returns the UI thread promptly.
    private static readonly TimeSpan PresentDrainTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Initializes a new <see cref="CompositionInteropVideoView"/>.</summary>
    public CompositionInteropVideoView()
    {
        ClipToBounds = true;

        // Once for the view's life, not per surface: each attachment sets a surface up again.
        SizeChanged += (_, _) => UpdateSurfaceLayout();
    }

    /// <summary>
    /// The sink this view presents: its own, which <see cref="EnsureSink"/> creates and the view
    /// disposes, or one a caller assigned, which the caller owns and the view never disposes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The view keeps its sink across a detach and presents it again when it rejoins the visual
    /// tree, whoever owns it (ADR-0085, decision 2). Assigning another sink disposes the view's own
    /// or lets go of a caller's, as <see cref="DisposeAsync"/> does, after which this reads
    /// <see langword="null"/>. Assigning <see langword="null"/> while the view is in the tree takes
    /// its compositor side down and stops the stall watchdog, as a detach does, until a sink is
    /// assigned or made again.
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
    public CompositionInteropVideoSink? Sink
    {
        get => _sink;
        set
        {
            if (ReferenceEquals(_sink, value))
                return;

            var transition = _lifecycle.Assign(value is not null);
            ObjectDisposedException.ThrowIf(transition.Refused, this);

            // Before anything changes, and released once the outgoing sink is disposed or let go
            // (ADR-0085, decisions 2 and 7).
            using var claim = ClaimOutgoingSink();

            // End the outgoing sink's diagnostics attachment. It must not keep reading the
            // view's live counters, which go on moving for its replacement — a caller still
            // holding it would otherwise read the successor's presents and ring-full drops
            // against its own baseline. An ended attachment reports the documented unwired
            // behavior (VideoSinkDiagnosticsSnapshot.Empty): no window is the honest answer
            // for a sink this view no longer presents, rather than someone else's.
            //
            // Ending under _diagnosticsGate, which BuildSinkDiagnostics also takes, is what
            // makes this safe against a poll already in flight: a diagnostics call that read
            // the delegate before the swap either completes wholly before the end, or sees
            // Ended and returns Empty. Clearing DiagnosticsSource alone would not — the
            // delegate can already be on another thread's stack. Diagnostics polling runs off
            // the UI thread while this setter is UI-thread affine, so the two do overlap.
            // The render tick never takes this gate.
            //
            // Only on replacement. A detach or DisposeAsync leaves the attachment live, so a host
            // polling PlaybackDiagnosticsSnapshot after the view detaches still reads the
            // session's counts instead of zeros, until a successor sink is wired.
            lock (_diagnosticsGate)
            {
                if (_attachment is not null)
                    _attachment.Ended = true;
                _attachment = null;
            }

            if (_sink is not null)
                _sink.DiagnosticsSource = null;

            Complete(Apply(transition, value));
        }
    }

    // How the view's own claim names it, in the error a build gets while the view replaces its sink.
    private const string ReplacingClaimant = "a CompositionInteropVideoView replacing its sink";

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
                $"The video sink {nameof(CompositionInteropVideoSink)} is held by "
                    + $"{outgoing.Claimant ?? "a player or pass"}. Replacing it would leave that player "
                    + "feeding a sink no view presents: await DisposeAsync on the player or pass built "
                    + "against it before assigning Sink."
            );
    }

    /// <summary>
    /// Total frames presented to the compositor (zero-copy or upload). Diagnostic
    /// surface for hosts — e.g. a multi-pane demo showing per-pane throughput.
    /// </summary>
    public int FramesPresented => Volatile.Read(ref _framesPresented);

    /// <summary>
    /// Total frames whose compositor hand-off actually <b>committed</b> — the
    /// <c>UpdateWithKeyedMutexAsync</c> task completed, meaning the compositor acquired the
    /// shared texture and released the keyed mutex (ADR-0064 §Observability). Distinct from
    /// <see cref="FramesPresented"/>, which counts at enqueue. A persistent gap
    /// (<see cref="FramesPresented"/> climbing while this stays flat) means frames are
    /// queued to the compositor but not reaching the screen — the failure class the
    /// enqueue-only counter could not see.
    /// </summary>
    public int FramesCommitted => Volatile.Read(ref _framesCommitted);

    /// <summary>
    /// Total frames the presenter dropped: every ring buffer still had a present in flight, or,
    /// for a D3D12VA frame, both of the converter's draws were still on the GPU. Diagnostic
    /// surface; a steady climb means the compositor or the GPU can't keep up with the feed rate.
    /// </summary>
    /// <remarks>
    /// This is only the presenter's share of the loss, counted for the view's whole lifetime.
    /// Frames superseded at the sink's latest-wins intake — the newest frame replacing one
    /// the view never presented — are counted by
    /// <see cref="CompositionInteropVideoSink.FramesSuperseded"/>. The diagnostics snapshot
    /// reports the sum of the two, rebased onto the attached sink's window; see
    /// <c>BuildSinkDiagnostics</c>.
    /// </remarks>
    public int FramesDropped => Volatile.Read(ref _framesDropped);

    /// <summary>
    /// Asks the GPU driver to upscale the video with its learned model, NVIDIA's RTX Video Super
    /// Resolution (#560). Off by default. While on, a D3D11VA NV12 frame shown larger than its own
    /// size is scaled through the video processor to the size it is shown at, with the driver's
    /// extension on; otherwise the view converts as it does with this off.
    /// </summary>
    /// <remarks>
    /// The upscale costs several milliseconds of GPU time a frame, more while the GPU clocks down
    /// between frames. It applies only on an NVIDIA RTX GPU, in a local session, with the driver's
    /// own super-resolution setting on. The driver upscales one video at a time across the system:
    /// a second view in this process stays on the shader path, and a view in another process that
    /// got there first leaves this one with plain scaling while it reports
    /// <see cref="SuperResolutionStatus.Requested"/>.
    /// <see cref="DriverSuperResolutionStatus"/> says whether the view asked, and if not, why.
    /// Changing it rebuilds the converter on the next frame.
    /// </remarks>
    public bool DriverSuperResolution
    {
        get => _driverSuperResolution;
        set
        {
            if (_driverSuperResolution == value)
                return;
            _driverSuperResolution = value;
            _superResolutionToggled = true;
            if (!value)
                Volatile.Write(ref _superResolutionStatus, (int)SuperResolutionStatus.Off);
        }
    }

    /// <summary>
    /// Whether the view asks the driver to upscale the frames it presents, and if not, the first
    /// condition that keeps it from asking. Updated on each presented frame; readable from any
    /// thread.
    /// </summary>
    public SuperResolutionStatus DriverSuperResolutionStatus =>
        (SuperResolutionStatus)Volatile.Read(ref _superResolutionStatus);

    /// <summary>
    /// Raised on the rising edge of a detected present stall (the UI-thread <c>VideoProcessorBlt</c>
    /// wedged in the GPU driver — investigation 2026-06-12 §9). The presenter cannot self-recover from
    /// a device-level wedge; a host (e.g. an application health monitor) subscribes to drive recovery by
    /// rebuilding the whole decode pipeline. Raised on a background timer thread, not the UI thread.
    /// </summary>
    public event EventHandler<PresenterStallInfo>? PresenterStalled;

    /// <summary>
    /// Raised once per stall, on the sample that confirms the presenter is presenting again. The
    /// clear-path counterpart to <see cref="PresenterStalled"/>: a host that took itself out of
    /// service on the stall can come back without an operator or a process restart. Confirmation is
    /// evidence-based (sustained forward progress on the counter that froze), never a bare timer or
    /// a counter reset. Raised on the same background timer thread as <see cref="PresenterStalled"/>,
    /// not the UI thread.
    /// </summary>
    public event EventHandler<PresenterRecoveryInfo>? PresenterRecovered;

    /// <summary>
    /// Wires the logger, makes the view's own sink unless it holds one, and, once the control is
    /// attached, brings up the compositor surface + GPU interop and starts the stall watchdog. Call
    /// on the UI thread, before playback starts.
    /// </summary>
    /// <remarks>
    /// <see cref="EnsureSink"/> runs this without a logger when it has not run yet, and
    /// <see cref="IVideoSurface.AttachSink"/> runs it with the factory it is given. Calling it
    /// again moves the view's logging, and its own sink's, to the new factory. A caller who
    /// assigns <see cref="Sink"/> instead of asking for one calls this itself. The surface and
    /// the watchdog run while the view is in the visual tree: a detach stops them and a reattach
    /// starts them again.
    /// </remarks>
    /// <exception cref="ObjectDisposedException"><see cref="DisposeAsync"/> has run.</exception>
    public void Initialize(ILoggerFactory loggerFactory)
    {
        var ensure = _lifecycle.EnsureOwned();
        ObjectDisposedException.ThrowIf(ensure.Refused, this);

        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger.Use(_loggerFactory);
        _sinkLogger.Use(_loggerFactory);
        _tickMeter.Reset();

        Complete(Apply(ensure));

        // The composition surface needs the control attached: the core starts it now if we
        // already are, otherwise the attach does. (When hosted in a FrameFlowPlayerView,
        // AttachSink may run before or after we enter the tree.)
        Complete(Apply(_lifecycle.Initialize()));
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Complete(Apply(_lifecycle.Attach()));
    }

    /// <summary>
    /// Returns the sink this view presents, as the <see cref="IVideoSink"/> to hand to the player:
    /// the view's own, which the first call makes, or one assigned through <see cref="Sink"/>. It
    /// is the same sink for the view's whole life, across trips out of the visual tree, unless
    /// <see cref="Sink"/> is assigned (ADR-0085, decision 2). The view disposes its own sink in
    /// <see cref="DisposeAsync"/>, and only there.
    /// </summary>
    /// <remarks>
    /// Readies the view to present that sink's frames: if <see cref="Initialize"/> has not run,
    /// this runs it, without a logger, so call it on the UI thread. To log, pass a factory to
    /// <see cref="Initialize"/> before or after this, or hand the view to a builder with
    /// <c>FrameFlowAvaloniaBuilderExtensions.WithVideoSurface</c>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// <see cref="DisposeAsync"/> has run: a sink made now would never be presented or disposed.
    /// </exception>
    public CompositionInteropVideoSink EnsureSink()
    {
        var ensure = _lifecycle.EnsureOwned();
        ObjectDisposedException.ThrowIf(ensure.Refused, this);
        Complete(Apply(ensure));

        // The compositor surface and the GPU interop wait for Initialize, and the presenter
        // holds every frame until they exist. A caller copying the FrameFlowVideoView pattern,
        // EnsureSink straight into WithVideoSink, never called it and got a black window with
        // nothing in the log (#613).
        if (!_lifecycle.Initialized)
            Initialize(_loggerFactory);

        return _sink!;
    }

    /// <summary>
    /// Moves the view to the state the lifecycle core chose and does the work it named, in the
    /// core's order (ADR-0085): unbind, stop presenting, dispose or let go of the sink, make or
    /// take up a sink, bind, start presenting. UI thread.
    /// </summary>
    /// <param name="transition">What the core decided.</param>
    /// <param name="assigned">The sink a caller assigned, which the borrow step takes up.</param>
    /// <returns>
    /// The teardown's remaining work, which completes once this attachment's producers are
    /// disposed or handed to the reaper, and what the other steps threw. Every step runs even when
    /// one before it threw: the core has moved on and will not ask for that work again, so a frame
    /// that fails to release must not leave the surface up, the sink undisposed or the next sink
    /// unbound.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The core refused the event: the view is disposed.</exception>
    private (Task Teardown, Exception? Fault) Apply(
        SurfaceTransition transition,
        CompositionInteropVideoSink? assigned = null
    )
    {
        ObjectDisposedException.ThrowIf(transition.Refused, this);

        // Before any work, under the gate the producer thread and a present's commit compare it
        // under: a callback raised under a binding this ends posts nothing from here on.
        lock (_presentGate)
            _lifecycle = transition.Next;

        var work = transition.Work;
        var teardown = Task.CompletedTask;
        List<Exception>? faults = null;

        if (work.HasFlag(SurfaceWork.Unbind))
            Run(UnbindSink, ref faults);

        if (work.HasFlag(SurfaceWork.StopPresenting))
        {
            try
            {
                teardown = StopPresenting();
            }
            catch (Exception ex)
            {
                teardown = Task.FromException(ex);
            }

            // From here the sink keeps a frame only for a player that holds its claim, and lets go
            // of the one it holds now when no player does.
            Run(() => _sink!.SetPresented(false), ref faults);
        }

        if (work.HasFlag(SurfaceWork.DisposeTarget))
            Run(DisposeSink, ref faults);
        if (work.HasFlag(SurfaceWork.ForgetTarget))
            Run(ForgetSink, ref faults);
        if (work.HasFlag(SurfaceWork.Create))
            Run(() => TakeUp(new CompositionInteropVideoSink(_sinkLogger)), ref faults);
        if (work.HasFlag(SurfaceWork.Borrow))
            Run(() => TakeUp(assigned!), ref faults);
        if (work.HasFlag(SurfaceWork.Bind))
            Run(BindSink, ref faults);
        if (work.HasFlag(SurfaceWork.StartPresenting))
            Run(StartPresenting, ref faults);

        Volatile.Write(ref _snapshot, new Snapshot(transition.Next.Presents, transition.Next.Binding, _sink));

        var fault = faults switch
        {
            null => null,
            [var one] => one,
            _ => new AggregateException(faults),
        };
        return (teardown, fault);
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

    /// <summary>
    /// Finishes a transition a caller asked for: a teardown it started runs on and is observed,
    /// and what a step threw is thrown here, once every step has run.
    /// </summary>
    private void Complete((Task Teardown, Exception? Fault) applied)
    {
        ObserveTeardown(applied.Teardown);
        if (applied.Fault is { } fault)
            ExceptionDispatchInfo.Throw(fault);
    }

    /// <summary>
    /// The teardown, faulted once it completes with what the other steps threw. A detach and
    /// <see cref="DisposeAsync"/> report every failure through it.
    /// </summary>
    private static Task Combined((Task Teardown, Exception? Fault) applied) =>
        applied.Fault is null ? applied.Teardown : FaultAfterAsync(applied.Teardown, applied.Fault);

    /// <summary>
    /// Completes once <paramref name="teardown"/> does, then faults with <paramref name="fault"/>,
    /// or with both faults when the teardown failed as well.
    /// </summary>
    private static async Task FaultAfterAsync(Task teardown, Exception fault)
    {
        try
        {
            await teardown.ConfigureAwait(false);
        }
        catch (Exception teardownFault)
        {
            throw new AggregateException(fault, teardownFault);
        }

        ExceptionDispatchInfo.Throw(fault);
    }

    /// <summary>
    /// Brings one attachment up: the sink keeps what it is given for the view again, the
    /// compositor surface and the GPU interop are set up, the stall watchdog starts, and a frame
    /// the sink kept is put up first.
    /// </summary>
    private void StartPresenting()
    {
        _tickMeter.Reset();
        _sink!.SetPresented(true);

        // Stopped by the next StopPresenting, so its timer cannot outlive the attachment and
        // keep a view out of the tree alive.
        if (_stallWatchdog is null)
        {
            _stallWatchdog = new PresenterStallWatchdog(SampleStall, _logger);
            _stallWatchdog.Stalled += OnStallDetected;
            _stallWatchdog.Recovered += OnStallRecovered;
        }

        SetupComposition();

        // A frame the sink kept while the view did not present is the newest the player sent,
        // and no later arrival is sure to come for it: the player may have paused or ended since.
        // The interop's readiness sweeps again.
        SchedulePresentIfPending();
    }

    // ── The sink steps (ADR-0085, decision 2). UI thread, in Apply's order. ──
    // The sink the view holds is presented exactly while the view presents: StartPresenting and
    // StopPresenting set it, and a sink taken up or let go of in between is set to match.

    private void TakeUp(CompositionInteropVideoSink sink)
    {
        _sink = sink;

        // The view does the presenting, so it supplies diagnostics for whichever sink it drives,
        // a caller-assigned one included.
        WireDiagnostics(sink);
        sink.SetPresented(_lifecycle.Presents);
    }

    private void DisposeSink()
    {
        var sink = _sink!;
        _sink = null;

        var t = sink.DisposeAsync();
        if (t.IsCompleted)
            t.GetAwaiter().GetResult();
        else
            t.AsTask().GetAwaiter().GetResult();
    }

    private void ForgetSink()
    {
        var sink = _sink!;
        _sink = null;

        // No view presents it now, so it keeps a frame only while a claim on it stands. The Sink
        // setter releases its own claim as it returns, which lets go of the frame.
        sink.SetPresented(false);
    }

    // Bind the diagnostics callback to THIS sink instance rather than to the mutable _sink
    // field, so a sink the caller replaced but still holds keeps reporting its own intake
    // losses instead of its successor's (or zero, once teardown has nulled _sink).
    //
    // One sink's attachment to this view: the counter values latched when it was wired, and
    // whether the attachment has since ended. Mutated only under _diagnosticsGate.
    private sealed class SinkAttachment(int presented, int dropped, int committed, long superseded)
    {
        public readonly int Presented = presented;
        public readonly int Dropped = dropped;
        public readonly int Committed = committed;
        public readonly long Superseded = superseded;
        public bool Ended;
    }

    // Serializes ending an attachment (Sink setter) against reading one (BuildSinkDiagnostics),
    // so a diagnostics poll already in flight when a sink is replaced cannot straddle the swap.
    // Held only for a handful of counter reads; the render tick never takes it.
    private readonly object _diagnosticsGate = new();
    private SinkAttachment? _attachment;

    // The view's counters are monotonic for its whole lifetime — PresenterStallWatchdog reads
    // _framesPresented/_framesCommitted as a forward-only delta, so they must never reset. To
    // still report one coherent window per attachment, latch every counter here and subtract
    // the latch in the snapshot. The sink's own supersede count is latched too: a caller may
    // hand back a sink this view (or another) already ran, and its slot count carries over.
    // On the single-sink path (EnsureSink, every path in this repo) all four baselines are
    // zero and the arithmetic is the identity.
    private void WireDiagnostics(CompositionInteropVideoSink sink)
    {
        var attachment = new SinkAttachment(
            Volatile.Read(ref _framesPresented),
            Volatile.Read(ref _framesDropped),
            Volatile.Read(ref _framesCommitted),
            sink.FramesSuperseded
        );

        // A new sink replaces the one whose attachment is still live. The Sink setter ends that
        // attachment itself before it replaces a sink, and the view's own sink lives as long as
        // the view (ADR-0085, decision 2), so this is a guard: a sink still polled by a player
        // must not read its successor's presents as its own.
        lock (_diagnosticsGate)
        {
            if (_attachment is not null)
                _attachment.Ended = true;
            _attachment = attachment;
        }

        sink.DiagnosticsSource = () => BuildSinkDiagnostics(sink, attachment);
    }

    // The view owns the presented count and the ring-full drop count, so it supplies the
    // sink's diagnostics. Mirrors AvaloniaVideoSink.GetDiagnostics on the CPU path; without
    // it the zero-copy presenter reported all-zero video counts.
    //
    // FramesDropped is the SUM of both places a frame dies on this path:
    //   1. superseded at the sink's latest-wins slot (feed rate > render-tick rate), and
    //   2. dropped here in PresentRing because the whole ring was still in flight, or because a
    //      D3D12VA frame found both of the converter's draws still on the GPU.
    // Reporting only (2) under-reported the loss: at 1080p60 the render tick takes one frame
    // per ~16 ms tick while the decoder feeds 60/s, so nearly all of the loss is (1) and the
    // snapshot read sink-drop=0 while a third of the frames never reached the screen. That
    // false-negative sent the #125 investigation upstream. The CPU AvaloniaVideoSink already
    // reports its slot's supersede count, so before this the two presenters disagreed about
    // identical loss on the same source.
    //
    // Every count spans ONE window: this sink's attachment to this view. Rebased onto the
    // wire-time latch at the start, bounded by the Ended check at the end. The
    // last-presented/committed stamps are latest-value rather than counts, so they are
    // reported as-is.
    private VideoSinkDiagnosticsSnapshot BuildSinkDiagnostics(
        CompositionInteropVideoSink sink,
        SinkAttachment attachment
    )
    {
        lock (_diagnosticsGate)
        {
            if (attachment.Ended)
                return VideoSinkDiagnosticsSnapshot.Empty;

            var ptsTicks = Volatile.Read(ref _lastPresentedPtsTicks);
            var utcTicks = Volatile.Read(ref _lastPresentedAtUtcTicks);
            var committedTicks = Volatile.Read(ref _lastCommittedAtUtcTicks);

            var presented = Volatile.Read(ref _framesPresented) - attachment.Presented;
            var ringFull = Volatile.Read(ref _framesDropped) - attachment.Dropped;
            var committed = Volatile.Read(ref _framesCommitted) - attachment.Committed;
            var superseded = sink.FramesSuperseded - attachment.Superseded;

            return new VideoSinkDiagnosticsSnapshot(
                FramesPresented: presented,
                FramesDropped: ringFull + superseded,
                LastPresentedPresentationTime: ptsTicks >= 0 ? TimeSpan.FromTicks(ptsTicks) : null,
                LastPresentedAtUtc: utcTicks >= 0 ? new DateTime(utcTicks, DateTimeKind.Utc) : null,
                FramesCommitted: committed,
                LastCommittedAtUtc: committedTicks >= 0
                    ? new DateTime(committedTicks, DateTimeKind.Utc)
                    : null
            );
        }
    }

    // ── IVideoSurface (lets FrameFlowPlayerView host the zero-copy presenter) ──
    Control IVideoSurface.Control => this;
    IVideoSink IVideoSurface.AttachSink(ILoggerFactory loggerFactory)
    {
        Initialize(loggerFactory);
        return EnsureSink();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ObserveTeardown(Combined(Apply(_lifecycle.Detach())));
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Binds the held sink's arrival callback to this view, under the binding number the
    /// transition has just set: a callback the sink raises later carries it, and posts nothing
    /// once the number has moved (ADR-0085, decision 5).
    /// </summary>
    private void BindSink()
    {
        var sink = _sink!;
        var binding = _lifecycle.Binding;
        sink.FrameArrived = () => OnFrameArrived(sink, binding);

        // A frame may already be waiting: fed before the sink was bound, or installed in the
        // instant before the hook went on. No later arrival is guaranteed to come and collect it:
        // on a paused or ended stream that frame is the one meant to be on screen. Sweep once now;
        // the coalescing claim makes a redundant sweep a no-op, and a view not presenting yet
        // leaves the frame in the sink.
        if (sink.HasPendingFrame)
            OnFrameArrived(sink, binding);
    }

    /// <summary>
    /// Takes the arrival callback off the held sink. A callback already running has seen, or will
    /// see, the binding number move under <see cref="_presentGate"/>, and posts nothing.
    /// </summary>
    private void UnbindSink() => _sink!.FrameArrived = null;

    /// <summary>
    /// Schedules a present for a frame the sink just installed. Runs on the presenting (graph)
    /// thread, and deliberately does no rendering: the compositor work is UI-thread affine, so
    /// all this does is wake the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces a <c>DispatcherTimer</c>. Avalonia's dispatcher timer is a message-queue
    /// timer quantized to the ~15.625 ms platform tick, so a 16 ms request was delivered at
    /// ~26 ms and capped the presenter near 38 fps against a 60 fps source however cheap the
    /// present was — measured at 0.3 ms (issue #128). A post carries no such quantum: it runs
    /// when the dispatcher next drains.
    /// </para>
    /// <para>
    /// <paramref name="binding"/> is the number the callback was bound under. A callback that
    /// outlives its binding, or one raised before the view first presents, posts nothing, and the
    /// frame stays with the sink, which keeps or drops it (ADR-0085, decisions 2, 3 and 5).
    /// </para>
    /// </remarks>
    private void OnFrameArrived(CompositionInteropVideoSink sink, long binding)
    {
        if (!TryClaimPost(binding))
            return;

        if (TryPostPresent())
            return;

        // The post never landed, so nothing will release the claim and every later arrival
        // would skip posting behind it — a permanent freeze from a transient failure. Release
        // it, then try once more in case a frame is still waiting: on a paused or ended
        // stream no later arrival will come to retry for us, and that last frame is the one
        // left on screen.
        lock (_presentGate)
            _presentPosted = false;

        if (sink.HasPendingFrame && TryClaimPost(binding) && !TryPostPresent())
        {
            lock (_presentGate)
                _presentPosted = false;
        }
    }

    /// <summary>
    /// Claims the one queued present for work started under <paramref name="binding"/>: the
    /// number has not moved, the view presents, and no present is queued already.
    /// </summary>
    private bool TryClaimPost(long binding)
    {
        lock (_presentGate)
        {
            if (binding != _lifecycle.Binding || !_lifecycle.Presents || _presentPosted)
                return false;
            _presentPosted = true;
            return true;
        }
    }

    /// <summary>
    /// Posts a present if the held sink is holding a frame. UI thread. Safe to call spuriously:
    /// the coalescing claim collapses a redundant call, and a present that finds nothing simply
    /// returns.
    /// </summary>
    private void SchedulePresentIfPending()
    {
        if (_sink is { HasPendingFrame: true } sink)
            OnFrameArrived(sink, _lifecycle.Binding);
    }

    private bool TryPostPresent()
    {
        try
        {
            Dispatcher.UIThread.Post(PresentPending, DispatcherPriority.Render);
        }
        catch
        {
            return false;
        }

        Interlocked.Increment(ref _presentsPosted);
        return true;
    }

    /// <summary>
    /// Takes the latest frame and presents it. UI thread only — every compositor and D3D11
    /// call below is thread-affine.
    /// </summary>
    /// <remarks>
    /// A post presents for whichever binding is current when it runs. The transitions run on this
    /// thread too, so nothing moves between the check below and the present, and a post claimed
    /// under an earlier binding is the one a later binding's arrivals coalesced into: dropping it
    /// would leave their frame waiting for an arrival that may never come.
    /// </remarks>
    private void PresentPending()
    {
        // Released before the work, not after, so a frame arriving mid-present can claim the
        // next post and be queued behind this one rather than waiting for it to finish.
        lock (_presentGate)
            _presentPosted = false;

        // Leave the frame pending rather than taking it: Present would drop it. Before the view
        // presents, and until the compositor side of this attachment is ready, the sink keeps the
        // newest frame, or none, by its own rule; the attach and the interop's readiness sweep it.
        if (!_lifecycle.Presents || _interop is null || _surface is null)
            return;

        var entered = Stopwatch.GetTimestamp();
        IVideoFrame? frame = null;
        try
        {
            frame = _sink?.TakePendingFrame();
            if (frame is not null)
                Present(frame);
        }
        finally
        {
            // Cadence vs cost, for issue #128. Two Stopwatch reads and some adds per present.
            //
            // In a finally so a throwing present is still recorded. Skipping it would leave
            // the failed present's duration unmeasured, and the next gap — which runs from
            // the last recorded exit — would absorb it and report it as scheduling delay.
            // That is the render path failing slowly disguised as the exact thing this meter
            // exists to detect.
            RecordTick(entered, frame is not null);
        }
    }

    private void RecordTick(long entered, bool hadFrame)
    {
        var report = _tickMeter.Record(entered, Stopwatch.GetTimestamp(), hadFrame);
        if (report is not { } r)
            return;

        // Emitted off the UI thread entirely. An enabled sink can format, do I/O and flush
        // synchronously, and dispatcher work is non-preemptive — so running it on the UI
        // thread at any priority can still delay the next present by the flush duration,
        // once per window, in the middle of measuring how often frames are missed.
        // A thread-pool hop every ~120 presents is far cheaper than that risk.
        var logger = _logger;
        _ = Task.Run(
            () =>
                logger.LogDebug(
                    "Presenter tick: {Rate:F1} ticks/s; scheduler gap mean={GapMean:F1}ms "
                        + "max={GapMax:F1}ms; present work mean={WorkMean:F1}ms max={WorkMax:F1}ms "
                        + "over {WithFrame}/{Ticks} ticks with a frame.",
                    r.TicksPerSecond,
                    r.GapMeanMs,
                    r.GapMaxMs,
                    r.WorkMeanMs,
                    r.WorkMaxMs,
                    r.TicksWithFrame,
                    r.Ticks
                )
        );
    }

    // Liveness snapshot for the stall watchdog — read off the UI thread, so only volatile counters
    // and the published snapshot, whose sink is the one the view presents.
    private PresenterSample SampleStall() => new(
        Stopwatch.GetTimestamp(),
        Volatile.Read(ref _framesPresented),
        Volatile.Read(ref _framesCommitted),
        Volatile.Read(ref _snapshot).Sink?.FramesAccepted ?? 0,
        Volatile.Read(ref _lastBltStartedTicks));

    private void OnStallDetected(PresenterStallInfo info)
    {
        // A sample the watchdog took as the view stopped presenting is not a stall of anything
        // on screen. Its timer runs off the UI thread, so it reads the published snapshot.
        if (!Volatile.Read(ref _snapshot).Presents)
            return;

        // The stalls counter lives on this presenter's own FrameFlow.Presenter meter, so it is
        // recorded here rather than inside the watchdog, which is presenter-agnostic and in
        // FrameFlow.Media. Still the rising edge: the watchdog raises Stalled once per stall.
        PresenterTeardownMetrics.RecordStall();
        PresenterStalled?.Invoke(this, info);
    }

    private void OnStallRecovered(PresenterRecoveryInfo info) => PresenterRecovered?.Invoke(this, info);

    /// <summary>What to do with the cached GPU converter for an incoming frame (ADR-0064).</summary>
    internal enum ConverterAction
    {
        /// <summary>Reuse the cached converter as-is.</summary>
        Reuse = 0,
        /// <summary>Device-loss (TDR / DEVICE_REMOVED) was observed — drop + rebuild it (step 6 guard).</summary>
        RebuildForDeviceLoss = 1,
        /// <summary>
        /// The incoming frame's decode device differs from the one the converter is bound to — a
        /// warm-sink player swap. The converter owns its own device (ADR-0064 Decision 2), so it
        /// <b>rebinds its decode bridge in place</b> rather than being rebuilt; the ring and its
        /// compositor imports stay warm. (Only if that in-place rebind fails does the presenter
        /// fall back to a full rebuild.)
        /// </summary>
        RebindDecodeDevice = 2,
        /// <summary>
        /// The incoming frame's dimensions differ from the cached converter's, and the converter
        /// cannot be resized: it is in the video processor mode, or the sample format changed too.
        /// The ring + staging textures are sized by the frame, so a resolution change <b>cannot</b>
        /// be an in-place rebind (the per-frame copy Box and the BGRA ring would be the wrong size).
        /// Takes priority over a device change, since a mixed-resolution playlist swap changes both
        /// at once.
        /// </summary>
        RebuildForResolutionChange = 3,
        /// <summary>
        /// The incoming frame's sample format differs from the one the converter was built for, as
        /// when an 8-bit item follows a 10-bit one (#559). The staging texture and its plane views
        /// are built for one format, so the converter is rebuilt. Takes priority over a device change.
        /// </summary>
        RebuildForFormatChange = 4,
        /// <summary>
        /// The incoming frame's dimensions differ from the cached converter's, in the same sample
        /// format, and the converter is in the shader mode. It replaces its staging texture, ring and
        /// decode bridge at the new size and keeps its device and shader pipeline. A device change
        /// at the same boundary is covered, since the new bridge opens on the frame's device.
        /// </summary>
        ResizeInPlace = 5,
    }

    /// <summary>
    /// Pure decision for how to handle the cached zero-copy converter for an incoming frame
    /// (ADR-0064): reuse it, rebuild it after device-loss, resize or rebuild it on a resolution
    /// change, or rebind its decode bridge after a same-size warm-sink player swap. Priority is
    /// device-loss, then resolution change (the ring/staging are sized by the frame, so a rebind
    /// cannot serve it), then a same-size device change (rebind in place). A resolution change
    /// resizes in place when <paramref name="cachedResizable"/> and the format is unchanged, and
    /// rebuilds otherwise. A <paramref name="frameDevice"/> of <see cref="nint.Zero"/>
    /// means the frame's device identity is unknown (chain unavailable) and is never treated as a
    /// mismatch — reuse rather than thrash on missing telemetry. A format change rebuilds after a
    /// resolution change; a null format, as the D3D12 converter passes since it reads the format
    /// per frame, is never a change. Extracted as a static so the swap-detection logic is
    /// unit-testable without a GPU.
    /// </summary>
    internal static ConverterAction EvaluateConverterAction(
        bool hasCached, nint cachedDevice, bool cachedDeviceLost, nint frameDevice,
        int cachedWidth, int cachedHeight, int frameWidth, int frameHeight,
        FrameFlow.Media.PixelFormat? cachedFormat = null, FrameFlow.Media.PixelFormat? frameFormat = null,
        bool cachedResizable = false)
    {
        if (!hasCached)
            return ConverterAction.Reuse;
        if (cachedDeviceLost)
            return ConverterAction.RebuildForDeviceLoss;
        bool formatChanged = cachedFormat is { } built && frameFormat is { } arriving && built != arriving;
        // A resolution change cannot be served by a rebind, whatever the device: the converter's ring
        // + staging textures and the per-frame copy Box are sized by the frame. Checked before the
        // device change so a mixed-resolution swap (new device AND new size) resizes or rebuilds
        // rather than rebinding onto a wrong-sized ring.
        if (frameWidth != cachedWidth || frameHeight != cachedHeight)
        {
            return cachedResizable && !formatChanged
                ? ConverterAction.ResizeInPlace
                : ConverterAction.RebuildForResolutionChange;
        }
        if (formatChanged)
            return ConverterAction.RebuildForFormatChange;
        if (frameDevice != nint.Zero && cachedDevice != frameDevice)
            return ConverterAction.RebindDecodeDevice;
        return ConverterAction.Reuse;
    }

    // ── Composition / interop setup ────────────────────────────────
    // Runs once per attachment, from StartPresenting: once the view is initialized, attached (the
    // compositor exists) and holds a sink, whichever happens last.
    private void SetupComposition()
    {
        var selfVisual = ElementComposition.GetElementVisual(this);
        _compositor = selfVisual?.Compositor;
        if (_compositor is null)
        {
            _logger.LogError("No compositor for this control — cannot set up GPU interop.");
            return;
        }

        _surface = _compositor.CreateDrawingSurface();
        _surfaceVisual = _compositor.CreateSurfaceVisual();
        _surfaceVisual.Surface = _surface;
        ElementComposition.SetElementChildVisual(this, _surfaceVisual);
        UpdateSurfaceLayout();

        _ = InitInteropAsync(_compositor, _surface, _d3d12Retired);
    }

    private async Task InitInteropAsync(Compositor compositor, CompositionDrawingSurface surface, Task d3d12Retired)
    {
        try
        {
            var interop = await compositor.TryGetCompositionGpuInterop();

            // A reattach builds no new D3D12 converter until the one the last teardown took has
            // been disposed, which releases the decoder frames it held for its draws (ADR-0085,
            // decision 2). Until then this attachment presents nothing, and the frames wait in the
            // sink. Completed at once unless that converter is still being torn down; never faults.
            await d3d12Retired;

            // The surface is this attachment's. A detach while the interop was on its way tore it
            // down, and a reattach has set up its own and asked for its own interop. The binding
            // number cannot say this: a sink assigned while the view presents moves the number and
            // keeps the surface, whose interop is still wanted.
            if (!ReferenceEquals(_surface, surface))
                return;

            _interop = interop;
            if (_interop is null)
            {
                _logger.LogError("ICompositionGpuInterop unavailable on this render backend.");
                return;
            }

            // Setup is complete, so anything PresentPending declined to take is now showable.
            // Without this a source that delivered during startup and then paused leaves its
            // last frame in the slot with nothing scheduled to collect it.
            SchedulePresentIfPending();

            var supported = _interop.SupportedImageHandleTypes;
            var ok = supported.Contains(
                KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle
            );
            _logger.LogInformation(
                "Compositor GPU interop ready. D3D11 global-shared-handle import supported: {Ok}. Types: [{Types}].",
                ok, string.Join(", ", supported)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to obtain ICompositionGpuInterop.");
        }
    }

    /// <summary>
    /// Sizes + centers the surface visual to a letterboxed rect at the video's display shape
    /// within the control bounds (the black window background shows in the bars), turned
    /// upright about its centre (#542). Called on attach, on resize, and when the video size,
    /// pixel shape or rotation changes.
    /// </summary>
    private void UpdateSurfaceLayout()
    {
        if (_surfaceVisual is null)
            return;

        var b = Bounds;
        var placement = VideoPlacement.Fit(b.Width, b.Height, _videoWidth, _videoHeight, _videoShape, _videoRotation);
        if (placement.IsEmpty)
        {
            _surfaceVisual.Size = new Vector(b.Width, b.Height);
            _surfaceVisual.Offset = new System.Numerics.Vector3(0f, 0f, 0f);
            _surfaceVisual.RotationAngle = 0f;
            return;
        }

        double x = placement.DrawX, y = placement.DrawY;
        double w = placement.DrawWidth, h = placement.DrawHeight;

        // While the ring is the size the frame is drawn at (#560), snap the visual to physical
        // pixels, so the compositor draws the upscaled ring 1:1 rather than resampling it by a
        // fraction of a pixel. A ring of another size, mid-resize, is scaled as before, and so is a
        // quarter turn, which moves the corner the snap aligns.
        double scaling = RenderScaling;
        if (_videoRotation is VideoRotation.None or VideoRotation.Clockwise180
            && _gpuConverter is { Output.SuperResolution: true } scaled
            && SuperResolutionPolicy.TargetSize(w, h, scaling) == (scaled.Output.Width, scaled.Output.Height))
        {
            w = scaled.Output.Width / scaling;
            h = scaled.Output.Height / scaling;
            x = Math.Round(x * scaling) / scaling;
            y = Math.Round(y * scaling) / scaling;
        }

        // The imported texture is the frame as coded. The visual is the frame's rectangle before
        // rotation, centred on the display area, so turning it about its centre fills the area.
        _surfaceVisual.Size = new Vector(w, h);
        _surfaceVisual.Offset = new System.Numerics.Vector3((float)x, (float)y, 0f);
        _surfaceVisual.CenterPoint = new Vector3D(w / 2, h / 2, 0);
        _surfaceVisual.RotationAngle = (float)placement.RotationRadians;
    }

    // ── Per-frame present (UI thread, from the render tick) ────────
    private void Present(IVideoFrame frame)
    {
        try
        {
            if (_interop is null || _surface is null)
                return; // interop not ready yet — drop. PresentPending checked both; a guard.

            if (frame.SampleAspectRatio != _videoShape || frame.Rotation != _videoRotation)
            {
                _videoShape = frame.SampleAspectRatio;
                _videoRotation = frame.Rotation;
                UpdateSurfaceLayout();
            }

            if (frame is GpuVideoFrame gpu && gpu.TryGetD3D11Texture(out var texture, out var slice, out var device))
            {
                // The imported ring is bound to one converter's shared handles.
                if (_d3d12Converter is not null)
                    DropGpuConverter(GpuConverterDropReason.BackendChange);

                // Zero-copy: the hardware D3D11VA surface stays on the GPU. Decide how to handle the
                // cached converter for this frame:
                //   (a) device-loss (TDR) seen on a previous frame  -> drop + rebuild (step 6), or
                //   (b) the frame's decode device differs from the one the converter is bound to — a
                //       player swap onto this warm sink brought a new decode device. The converter
                //       owns its OWN device (ADR-0064 Decision 2), so it rebinds its decode bridge
                //       in place — the ring + compositor imports stay warm, no rebuild (ADR-0064).
                //       Only if that in-place rebind fails (a driver that won't open the shared NV12)
                //       do we fall back to the full rebuild that
                //       predates ADR-0064. device == 0 means "identity
                //       unknown" (chain unavailable) — never treat that as a mismatch. A resolution
                //       change resizes the shader-mode converter in place, keeping its device and
                //       shader pipeline, and rebuilds it otherwise (a rebind can't resize).
                //
                //       Not while driver super resolution is on: there the output is chosen per frame
                //       (#560), and a rebuild picks it at once, where a resize would build the shader's
                //       ring only for the output settle to replace it.
                switch (EvaluateConverterAction(
                    hasCached: _gpuConverter is not null,
                    cachedDevice: _gpuConverter?.SourceDevicePointer ?? nint.Zero,
                    cachedDeviceLost: _gpuConverter?.IsDeviceLost ?? false,
                    frameDevice: device,
                    cachedWidth: _gpuConverter?.Width ?? 0,
                    cachedHeight: _gpuConverter?.Height ?? 0,
                    frameWidth: frame.Width,
                    frameHeight: frame.Height,
                    cachedFormat: _gpuConverter?.InputFormat,
                    frameFormat: frame.Format,
                    cachedResizable: _gpuConverter is { Output.SuperResolution: false } && !_driverSuperResolution))
                {
                    case ConverterAction.RebuildForDeviceLoss:
                        DropGpuConverter(GpuConverterDropReason.DeviceLost);
                        break;
                    case ConverterAction.RebuildForResolutionChange:
                        DropGpuConverter(GpuConverterDropReason.ResolutionChange);
                        break;
                    case ConverterAction.ResizeInPlace:
                        ResizeGpuConverter(frame, texture);
                        break;
                    case ConverterAction.RebuildForFormatChange:
                        DropGpuConverter(GpuConverterDropReason.FormatChange);
                        break;
                    case ConverterAction.RebindDecodeDevice:
                        if (_gpuConverter!.TryRebindDecodeDevice(texture))
                        {
                            // The healthy gapless-playlist signature: a swap rebound the bridge with
                            // no rebuild and no compositor re-import.
                            PresenterTeardownMetrics.RecordDeviceChangeRebind();
                        }
                        else
                        {
                            // The converter could not rebind to the new device (driver rejected the
                            // cross-device staging open; the converter logged the cause). Fall back to
                            // the validated pre-ADR-0064 path: drop + rebuild on
                            // the new device.
                            // DropGpuConverter logs the rebuild and bumps device_change_rebuilds (the
                            // otherwise-0 regression alarm).
                            DropGpuConverter(GpuConverterDropReason.DeviceChangeRebindFailed);
                        }
                        break;
                }
                EnsureConverterOutput(frame, texture, device);
                PresentRing(
                    PresentSource.D3D11,
                    frame.Width,
                    frame.Height,
                    _gpuConverter!.Output.Width,
                    _gpuConverter.Output.Height,
                    _gpuConverter.GetSharedHandle,
                    i => _gpuConverter.ConvertInto(i, texture, slice),
                    frame.Pts,
                    frame.Timeline
                );
            }
            else if (frame is GpuVideoFrame d3d12 && d3d12.TryGetD3D12Texture(out var d3d12Texture, out _, out _, out _))
            {
                ReportUnsupportedFrames();
                PresentD3D12(d3d12, d3d12Texture);
            }
            else if (frame.Format == FrameFlow.Media.PixelFormat.Bgra32 && frame.AsCpu() is { } cpu)
            {
                // Upload fallback: software-decoded BGRA frame (no D3D11VA).
                if (_cpuUploader is { IsDeviceLost: true })
                    DropCpuUploader();
                ReportUnsupportedFrames();
                _cpuUploader ??= new D3D11BgraUploader(frame.Width, frame.Height, _logger);
                PresentRing(
                    PresentSource.Cpu,
                    frame.Width,
                    frame.Height,
                    frame.Width,
                    frame.Height,
                    _cpuUploader.GetSharedHandle,
                    i => _cpuUploader.UploadInto(i, cpu),
                    frame.Pts,
                    frame.Timeline
                );
            }
            else if (!_warnedUnpresentable)
            {
                _warnedUnpresentable = true;
                _logger.LogWarning(
                    "Unpresentable frame: domain={Domain}, format={Format}, type={Type}. "
                        + "Expected a D3D11VA or D3D12VA GpuVideoFrame or a Bgra32 CPU frame.",
                    frame.MemoryDomain, frame.Format, frame.GetType().Name
                );
            }
        }
        catch (Exception ex)
        {
            if (!_warnedPresentFailure)
            {
                _warnedPresentFailure = true;
                _logger.LogWarning(
                    ex,
                    "Present failed after {Frames} frame(s); suppressing further per-frame errors.",
                    Volatile.Read(ref _framesPresented)
                );
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// Builds the D3D11 converter for <paramref name="frame"/> with the output the super-resolution
    /// decision wants (#560), rebuilding it when that output has been wanted for the settle
    /// interval, and records the status. Runs after the device-loss, resolution and rebind decisions,
    /// so a converter they dropped is built here at once.
    /// </summary>
    private void EnsureConverterOutput(IVideoFrame frame, nint texture, nint device)
    {
        if (_superResolutionToggled)
        {
            _superResolutionToggled = false;
            _superResolutionFailed = false;
            if (_gpuConverter is not null)
                DropGpuConverter(GpuConverterDropReason.SuperResolutionToggled);
        }

        var (wanted, decided) = SuperResolutionPolicy.Decide(SuperResolutionInputsFor(frame, texture, device));
        var (settle, apply) = (_outputSettle with { Applied = _gpuConverter?.Output })
            .Advance(wanted, Stopwatch.GetTimestamp(), OutputSettleTicks);
        _outputSettle = settle;

        if (apply && _gpuConverter is not null)
            DropGpuConverter(GpuConverterDropReason.OutputChange);

        if (_gpuConverter is null)
        {
            var output = wanted;
            if (output.SuperResolution && !SuperResolutionLease.Process.TryAcquire(this))
                output = PresenterOutput.Shader(frame.Width, frame.Height);
            _gpuConverter = new D3D11Nv12SharedConverter(texture, frame.Width, frame.Height, output, _logger);
            if (!_gpuConverter.Output.SuperResolution)
                SuperResolutionLease.Process.Release(this);
            UpdateSurfaceLayout();
        }

        // A converter that could not set the video processor up, or lost it to a failed blit, is
        // not retried until the property is toggled or the decode device changes.
        if (_gpuConverter.SuperResolutionFailed)
            _superResolutionFailed = true;

        bool scaling = _gpuConverter.Output.SuperResolution && !_gpuConverter.SuperResolutionFailed;
        var status = scaling
            ? SuperResolutionStatus.Requested
            : decided == SuperResolutionStatus.Requested
                ? DriverSuperResolutionStatus // wanted, not applied yet: report what is on screen
                : decided;
        Volatile.Write(ref _superResolutionStatus, (int)status);
    }

    private SuperResolutionInputs SuperResolutionInputsFor(IVideoFrame frame, nint texture, nint device)
    {
        // Off, only the frame's size matters: the wanted output is the shader at it.
        if (!_driverSuperResolution)
            return new SuperResolutionInputs { FrameWidth = frame.Width, FrameHeight = frame.Height };

        // Read once per decode device: a COM round trip to the adapter. A device whose identity is
        // unknown (0) is read each time and never counts as a change.
        if (device == nint.Zero || device != _vendorDevice)
        {
            _vendorId = D3D11Nv12SharedConverter.AdapterVendorOf(texture);
            if (device != nint.Zero)
            {
                if (_vendorDevice != nint.Zero)
                    _superResolutionFailed = false; // a new decode device gets a fresh attempt
                _vendorDevice = device;
            }
        }

        // The frame's rectangle as drawn, before rotation (#542): the ring holds the frame as coded.
        var placement = VideoPlacement.Fit(
            Bounds.Width, Bounds.Height, frame.Width, frame.Height, frame.SampleAspectRatio, frame.Rotation);
        var (targetWidth, targetHeight) = SuperResolutionPolicy.TargetSize(
            placement.DrawWidth, placement.DrawHeight, RenderScaling);

        return new SuperResolutionInputs(
            Enabled: true,
            D3D11Nv12Frame: frame.Format == FrameFlow.Media.PixelFormat.Nv12,
            FrameWidth: frame.Width,
            FrameHeight: frame.Height,
            TargetWidth: targetWidth,
            TargetHeight: targetHeight,
            AdapterVendorId: _vendorId,
            RemoteSession: RemoteSession.IsActive,
            LeaseAvailable: SuperResolutionLease.Process.IsAvailableTo(this),
            Failed: _superResolutionFailed);
    }

    private void ReportUnsupportedFrames()
    {
        if (_driverSuperResolution)
            Volatile.Write(ref _superResolutionStatus, (int)SuperResolutionStatus.UnsupportedFrames);
    }

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>
    /// Presents a D3D12VA frame (#429): the same converter decisions as the D3D11VA path, except
    /// that a new decode device rebuilds the converter. Its D3D12 half lives on that device, and
    /// decoders on one adapter share a device unless it came from <c>ID3D12DeviceFactory</c>, so a
    /// change is rare enough that an in-place rebind is not worth its code.
    /// </summary>
    private void PresentD3D12(GpuVideoFrame frame, nint texture)
    {
        if (_gpuConverter is not null)
            DropGpuConverter(GpuConverterDropReason.BackendChange);

        switch (EvaluateConverterAction(
            hasCached: _d3d12Converter is not null,
            cachedDevice: _d3d12Converter?.SourceDevicePointer ?? nint.Zero,
            cachedDeviceLost: _d3d12Converter?.IsDeviceLost ?? false,
            frameDevice: D3D12YuvSharedConverter.DeviceIdentity(texture),
            cachedWidth: _d3d12Converter?.Width ?? 0,
            cachedHeight: _d3d12Converter?.Height ?? 0,
            frameWidth: frame.Width,
            frameHeight: frame.Height))
        {
            case ConverterAction.RebuildForDeviceLoss:
                DropGpuConverter(GpuConverterDropReason.DeviceLost);
                break;
            case ConverterAction.RebuildForResolutionChange:
                DropGpuConverter(GpuConverterDropReason.ResolutionChange);
                break;
            case ConverterAction.RebindDecodeDevice:
                DropGpuConverter(GpuConverterDropReason.DecodeDeviceChange);
                break;
        }

        var converter = _d3d12Converter ??= new D3D12YuvSharedConverter(texture, frame.Width, frame.Height, _logger);
        PresentRing(
            PresentSource.D3D12,
            frame.Width,
            frame.Height,
            frame.Width,
            frame.Height,
            converter.GetSharedHandle,
            i =>
            {
                // Both draws still on the GPU: the frame is dropped rather than waited for.
                bool converted = converter.ConvertInto(i, frame);
                if (!converted && !converter.IsDeviceLost)
                    Interlocked.Increment(ref _framesDropped);
                return converted;
            },
            frame.Pts,
            frame.Timeline
        );
    }

    /// <summary>Which producer fills the ring for a frame.</summary>
    private enum PresentSource
    {
        Cpu,
        D3D11,
        D3D12,
    }

    /// <summary>
    /// Snapshots the scattered mutable present fields (<see cref="_activeIsGpu"/>,
    /// <see cref="_nextBuffer"/>, <see cref="_videoWidth"/>, <see cref="_videoHeight"/>) into the
    /// immutable <see cref="PresentState"/> the pure <see cref="PresentPlanner"/> folds. The fields
    /// remain the shell's source of truth — other paths (source-flip / device-loss rebuilds) reset
    /// them — so the snapshot is rebuilt per frame rather than cached.
    /// </summary>
    private PresentState CurrentPresentState() =>
        PresentState.Initial(D3D11Nv12SharedConverter.BufferCount)
            .WithActiveSource(_activeIsGpu)
            .WithNextBuffer(_nextBuffer)
            .WithVideoSize(_videoWidth, _videoHeight);

    /// <summary>
    /// Whether ring slot <paramref name="i"/> has no present in flight — its previous
    /// <c>UpdateWithKeyedMutexAsync</c> task is null or completed, so the next fill's
    /// <c>AcquireSync(0)</c> won't contend with an in-flight compositor present. The freedom input
    /// the planner reads on the non-flip path (a source flip clears the whole ring in the shell).
    /// </summary>
    private bool SlotFree(int i)
    {
        var t = _presentTasks[i];
        return t is null || t.IsCompleted;
    }

    /// <summary>
    /// Shared compositor present: pick a ring buffer whose previous present has completed,
    /// fill it (<paramref name="fill"/> does the GPU Blt or CPU upload, including the
    /// keyed-mutex bracket), import it, and hand it to the surface.
    /// <paramref name="getSharedHandle"/> + <paramref name="fill"/> abstract over the GPU
    /// vs CPU source — both produce the same shared keyed-mutex BGRA textures, so the
    /// present + sequencing logic is identical. <paramref name="fill"/> returns
    /// <see langword="false"/> on GPU device-loss, in which case this present is skipped and
    /// the source is rebuilt on the next frame (step 6).
    /// </summary>
    /// <remarks>
    /// The per-frame DECISION — which free slot, source-flip re-import, size-change layout update,
    /// or drop — is the pure <see cref="PresentPlanner.Advance"/>. This method is the imperative
    /// shell that performs the decision against the live D3D / compositor handles: it owns the
    /// keyed-mutex hand-off, the <see cref="Stopwatch.GetTimestamp"/> Blt stamp, import, and the
    /// present-task bookkeeping.
    /// </remarks>
    private void PresentRing(
        PresentSource source,
        int width,
        int height,
        int ringWidth,
        int ringHeight,
        Func<int, nint> getSharedHandle,
        Func<int, bool> fill,
        TimeSpan pts,
        TimelineId timeline
    )
    {
        // Decide the per-frame outcome purely (PresentPlanner): build the current ring/source/size
        // state from the scattered fields, fold in the incoming frame's descriptor, and read back
        // which free slot to fill (or to drop), whether the source flipped (re-import the ring),
        // and whether the video size changed (update the layout). The decision touches no D3D /
        // compositor / clock; this shell performs the named outcomes below in plan order.
        var plan = PresentPlanner.Advance(
            CurrentPresentState(),
            new FrameDescriptor(source != PresentSource.Cpu, width, height),
            SlotFree);

        // Perform "source flip" first: the imported[] ring is bound to one source's shared handles.
        // If the source switched (GPU<->CPU — only if decode flips mid-stream), re-import from
        // scratch. Dispose the old imported images THROUGH the compositor first (§7.4): they hold
        // the compositor's open copy of the old ring's keyed mutex; clearing the array without
        // disposing leaks those opens and leaves the old ring co-owned. DetachImported also clears
        // the present tasks, which is what frees the whole ring for the plan's index-0 pick.
        if (plan.ReimportRing)
            DetachImported();

        // Apply the planned source/size/cursor back onto the live fields so the rest of the shell
        // (and the next frame's snapshot) sees the threaded-forward state. The plan advances the
        // cursor only when a slot was claimed; on a drop it leaves it put.
        _activeIsGpu = plan.NextState.ActiveIsGpu;
        _nextBuffer = plan.NextState.NextBuffer;

        if (plan.UpdateLayout)
        {
            _videoWidth = width;
            _videoHeight = height;
            UpdateSurfaceLayout();
        }

        if (plan.Drop)
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }
        var idx = plan.SlotIndex;

        // Stamp "a Blt is about to start" for the stall watchdog: if fill() (the VideoProcessorBlt)
        // hangs in the GPU driver, this timestamp goes stale while the sink keeps accepting frames —
        // the watchdog's stall signature (investigation 2026-06-12 §9).
        Volatile.Write(ref _lastBltStartedTicks, Stopwatch.GetTimestamp());

        // Device-loss observed mid-fill: skip this present. The source has flagged itself
        // IsDeviceLost; the next Present() drops + rebuilds it (and its imported ring).
        if (!fill(idx))
            return;

        _imported[idx] ??= _interop!.ImportImage(
            new PlatformHandle(
                getSharedHandle(idx),
                KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle
            ),
            new PlatformGraphicsExternalImageProperties
            {
                // The ring's size, which is the frame's except while the driver upscales (#560).
                Width = ringWidth,
                Height = ringHeight,
                Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                // Our D3D11 textures are top-left origin (row 0 = top), like every D3D
                // surface. Avalonia defaults this flag to false (bottom-left / GL
                // convention), so a backend that honours it — e.g. the Intel HD 620's
                // ANGLE/GL compositor — samples the texture upside down.
                // Backends that happen to assume top-left ignore the flag, so the bug is
                // invisible on some GPUs and a vertical flip on others. Stating the true
                // origin makes orientation correct on every backend.
                TopLeftOrigin = true,
            }
        );

        var presentTask = _surface!.UpdateWithKeyedMutexAsync(_imported[idx]!, 1, 0);
        _presentTasks[idx] = presentTask;

        // Count the COMMIT when the hand-off completes (ADR-0064 §Observability). Only RanToCompletion
        // bumps the committed counter — a faulted/cancelled present did not reach the screen.
        // Fire-and-forget off the UI thread; the drain/availability logic keys off the
        // original task (presentTask) so this continuation does not perturb sequencing.
        // The sink this frame belongs to, and the binding it is presented under, captured now
        // rather than read when the hand-off completes: the Sink setter can swap in a
        // replacement while a present is still in flight, and the successor must not be told it
        // presented a frame it never saw. Same rule the diagnostics attachment follows, where a
        // replaced sink keeps reporting its own window.
        var presentingSink = _sink;
        var binding = _lifecycle.Binding;

        presentTask.ContinueWith(
            static (_, state) =>
            {
                var (self, sink, binding, framePts, frameTimeline) =
                    ((CompositionInteropVideoView, CompositionInteropVideoSink?, long, TimeSpan, TimelineId))state!;
                self.OnPresentCommitted(sink, binding, framePts, frameTimeline);
            },
            (this, presentingSink, binding, pts, timeline),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        Interlocked.Increment(ref _framesPresented);
        // Stamp the presented frame's PTS + wallclock for the sink's diagnostics
        // snapshot (A/V drift). Single writer (this UI-thread present loop);
        // GetDiagnostics reads via Volatile from any thread.
        Volatile.Write(ref _lastPresentedPtsTicks, pts.Ticks);
        Volatile.Write(ref _lastPresentedAtUtcTicks, DateTime.UtcNow.Ticks);
        LogProgress(source);
    }

    /// <summary>
    /// The compositor took a frame's hand-off. Counts the commit, and tells
    /// <paramref name="sink"/> its frame was presented, unless the binding it was presented
    /// under has ended (ADR-0085, decision 5). Runs on whichever thread completed the hand-off.
    /// </summary>
    /// <remarks>
    /// The commit is counted whatever the binding: the compositor did drain the hand-off, and the
    /// counters span the view's life. <c>FramePresented</c> rides the commit, not the enqueue, so
    /// it means the same thing the committed counter does: this frame reached the screen. A
    /// faulted or cancelled hand-off never gets here, so a consumer is never told about a frame
    /// that device loss ate, and a view that has stopped presenting tells nobody. The number is
    /// compared under <see cref="_presentGate"/>, and the handlers run outside it.
    /// </remarks>
    internal void OnPresentCommitted(CompositionInteropVideoSink? sink, long binding, TimeSpan pts, TimelineId timeline)
    {
        Interlocked.Increment(ref _framesCommitted);
        Volatile.Write(ref _lastCommittedAtUtcTicks, DateTime.UtcNow.Ticks);

        bool current;
        lock (_presentGate)
            current = binding == _lifecycle.Binding && _lifecycle.Presents;

        if (current)
            sink?.RaiseFramePresented(pts, timeline);
    }

    private void LogProgress(PresentSource source)
    {
        var presented = Volatile.Read(ref _framesPresented);

        if (source == PresentSource.D3D11 && !_loggedGpuLive)
        {
            _loggedGpuLive = true;
            _logger.LogInformation(
                "ZERO-COPY PATH LIVE: D3D11VA NV12/P010 → pixel-shader BGRA ({N}-buffer shared keyed-mutex "
                    + "ring) → ICompositionGpuInterop.ImportImage → compositor. No CPU round-trip.",
                D3D11Nv12SharedConverter.BufferCount
            );
        }
        else if (source == PresentSource.D3D12 && !_loggedD3D12Live)
        {
            _loggedD3D12Live = true;
            _logger.LogInformation(
                "ZERO-COPY PATH LIVE: D3D12VA NV12/P010 → D3D12 pixel-shader BGRA → D3D11 copy ({N}-buffer "
                    + "shared keyed-mutex ring) → ICompositionGpuInterop.ImportImage → compositor. No CPU round-trip.",
                D3D12YuvSharedConverter.BufferCount
            );
        }
        else if (source == PresentSource.Cpu && !_loggedCpuLive)
        {
            _loggedCpuLive = true;
            _logger.LogInformation(
                "CPU-UPLOAD FALLBACK LIVE: CPU BGRA frame → staging → shared keyed-mutex texture "
                    + "({N}-buffer ring) → ICompositionGpuInterop.ImportImage → compositor (software "
                    + "decode, or a hardware frame the decoder downloaded because the video path "
                    + "does not take GPU frames).",
                D3D11Nv12SharedConverter.BufferCount
            );
        }

        // Report the same total the diagnostics snapshot does. Logging only the ring-full
        // count printed "0 dropped" while the sink was superseding a third of the frames at
        // 1080p60 — that line is quoted in #128 as evidence the presenter was healthy.
        // Split the two so the operational signal says where the loss is.
        if (presented % 120 == 0)
            _logger.LogInformation(
                "Sustained presentation ({Mode}): {Presented} presented, {Dropped} dropped "
                    + "({Superseded} superseded at the sink, {Presenter} ring-full or GPU-busy).",
                source switch
                {
                    PresentSource.D3D11 => "zero-copy",
                    PresentSource.D3D12 => "zero-copy, D3D12",
                    _ => "cpu-upload",
                },
                presented,
                Volatile.Read(ref _framesDropped) + (_sink?.FramesSuperseded ?? 0),
                _sink?.FramesSuperseded ?? 0,
                Volatile.Read(ref _framesDropped)
            );
    }

    /// <summary>
    /// Tears the view down for good: the compositor side, if the view is presenting, and its sink.
    /// The view's own sink is disposed, even while a player still feeds it, which then has every
    /// frame disposed on arrival; a caller's sink is let go, not disposed. The view presents
    /// nothing afterwards, and <see cref="Sink"/> reads <see langword="null"/>.
    /// <see cref="EnsureSink"/>, <see cref="Initialize"/>, <see cref="IVideoSurface.AttachSink"/>
    /// and assigning a sink then throw <see cref="ObjectDisposedException"/>. Call on the UI
    /// thread.
    /// </summary>
    /// <returns>
    /// A task that completes once the producers are disposed, or handed to the background reaper
    /// when the compositor does not drain in time. It faults with what any step threw, such as a
    /// frame that failed to release, after every step has run.
    /// </returns>
    public ValueTask DisposeAsync() => new(Combined(Apply(_lifecycle.Dispose())));

    /// <summary>
    /// Observes the teardown a visual-tree detach (or window close), or clearing
    /// <see cref="Sink"/>, started and did not wait for:
    /// the UI thread is never blocked on the compositor. The teardown is self-contained (it owns
    /// snapshots of every resource it frees), so fire-and-forget is safe here;
    /// <see cref="DisposeAsync"/> returns the same work when a caller wants completion. A fault
    /// continuation observes the discarded task so a teardown error never surfaces as an
    /// unobserved task exception.
    /// </summary>
    private void ObserveTeardown(Task teardown)
    {
        if (!teardown.IsCompletedSuccessfully)
            teardown.ContinueWith(
                static (t, state) =>
                    ((ILogger)state!).LogWarning(t.Exception, "Presenter async teardown faulted."),
                _logger,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
    }

    /// <summary>
    /// Ordered teardown of one attachment's compositor side that never leaves the producer
    /// waiting on the compositor (investigation 2026-06-12, §6). Sequence: (1) stop producing,
    /// which the binding number has done: it moved before this ran, so nothing posts or commits
    /// a present for this attachment from here (ADR-0085, decision 5); (2) detach from the
    /// compositor and dispose the
    /// imported images through its render thread; (3) drain in-flight presents with a bounded
    /// wait; (4) on success dispose the producer off the UI thread, on timeout defer producer
    /// disposal to a background reaper (never block, never leak).
    /// </summary>
    /// <remarks>
    /// Everything that reads the view's fields runs here, on the UI thread, before the wait:
    /// the present tasks, the imported ring, the surface and the producers are taken and the
    /// fields cleared. The wait and what follows it run on <see cref="FinishTeardownAsync"/>,
    /// which sees only what it was handed. A reattach during the wait builds a new ring and new
    /// producers in the cleared fields, and this teardown can neither dispose them nor present
    /// into them (#650).
    /// </remarks>
    private Task StopPresenting()
    {
        // Observability (investigation 2026-06-12): time the ordered teardown so the healthy
        // "drained cleanly" path is visible in logs + the FrameFlow.Presenter meter, not just
        // the deferral. Without this a clean teardown leaves no trace and the fix can only be
        // seen when it hits the bad (wedged) path.
        var teardownStart = PresenterTeardownMetrics.BeginTeardown();

        // Stop the stall watchdog before teardown so it cannot fire on the intended stop.
        _stallWatchdog?.Dispose();
        _stallWatchdog = null;

        // Step 2: detach the surface visual from the control and dispose every imported
        // image via its IAsyncDisposable. ICompositionImportedGpuImage.DisposeAsync posts to
        // the compositor render thread, which releases the compositor's open copy of each
        // shared texture AND its keyed mutex — on the compositor's device. This must happen
        // before we destroy the producer ring, or the producer's Release blocks resolving a
        // mutex another (possibly wedged) device still holds.
        //
        // The surface visual + drawing surface are Avalonia composition objects: their teardown
        // serializes through the compositor's UI-thread commit cycle (non-blocking enqueue, not
        // a GPU release), so we dispose them HERE on the UI thread — they are not part of the
        // cross-device keyed-mutex rendezvous and must not be touched off-thread.
        if (_surfaceVisual is not null)
        {
            try { ElementComposition.SetElementChildVisual(this, null); }
            catch (Exception ex) { _logger.LogDebug(ex, "Detaching surface visual on teardown faulted (ignored)."); }
            _surfaceVisual = null;
        }

        // Snapshot the in-flight present tasks BEFORE disposing the imported ring — the image
        // disposal and the present hand-off are distinct pieces of compositor work, and the
        // snapshot must capture the present tasks while they're still referenced.
        var presentSnapshot = (Task?[])_presentTasks.Clone();
        Array.Clear(_presentTasks);

        var importedDisposal = DisposeImportedAsync();

        try { _surface?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Disposing the drawing surface on teardown faulted (ignored)."); }
        _surface = null;

        // The interop belongs to the compositor this attachment ran on. A reattach asks its own
        // compositor for one, and presents nothing until it has it.
        _interop = null;
        _compositor = null;

        // Take this attachment's producers now, on the UI thread, and clear the fields. Taken
        // after the wait instead, a teardown would pick up whatever a reattach built in the
        // meantime and dispose it from the thread pool while the UI thread presents through it.
        // Only the producers — the keyed-mutex rings whose native Release can block — are
        // disposed off-thread; the surface is already gone (UI thread, above). The ring cursor
        // and source go with them, so the next attachment imports its ring from scratch.
        var converter = _gpuConverter;
        var d3d12Converter = _d3d12Converter;
        var uploader = _cpuUploader;
        _gpuConverter = null;
        _d3d12Converter = null;
        _cpuUploader = null;
        _nextBuffer = 0;
        _activeIsGpu = null;
        _outputSettle = default;
        SuperResolutionLease.Process.Release(this);

        // The next attachment waits for this to present (InitInteropAsync): a D3D12 converter
        // holds decoder frames for its draws until it is disposed (ADR-0085, decision 2).
        var producersDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (d3d12Converter is not null)
            _d3d12Retired = producersDisposed.Task;

        // Step 3: drain in-flight presents (a present holding key 1 must finish and release
        // to 0). Both the imported disposal and the drain need the compositor render thread
        // to make progress, so wait on them together under one bounded timeout.
        var presentDrain = DrainPresentsAsync(presentSnapshot);
        var gating = Task.WhenAll(importedDisposal, presentDrain);
        return FinishTeardownAsync(
            gating, teardownStart, _logger, producersDisposed, converter, d3d12Converter, uploader);
    }

    /// <summary>
    /// Steps 3 and 4 of <see cref="StopPresenting"/>: waits for the compositor to drain, then
    /// disposes the producers it was handed, or hands them to the reaper. Static, so it can reach
    /// nothing of the view's but what one teardown took from it. Completes
    /// <paramref name="producersDisposed"/> once they are disposed, by either path.
    /// </summary>
    private static async Task FinishTeardownAsync(
        Task gating,
        long teardownStart,
        ILogger logger,
        TaskCompletionSource producersDisposed,
        D3D11Nv12SharedConverter? converter,
        D3D12YuvSharedConverter? d3d12Converter,
        D3D11BgraUploader? uploader
    )
    {
        var drained = await AwaitBoundedAsync(gating, PresentDrainTimeout).ConfigureAwait(false);

        if (drained)
        {
            // Compositor released the keyed mutex; destroying the producer ring is now safe.
            // We're on a thread-pool continuation (ConfigureAwait(false)), never the UI
            // thread — so even the borrowed-device Release at the converter's tail can't
            // hang the UI.
            try
            {
                converter?.Dispose();
                d3d12Converter?.Dispose();
                uploader?.Dispose();
            }
            finally
            {
                producersDisposed.TrySetResult();
            }

            var elapsedMs = PresenterTeardownMetrics.RecordCompleted(teardownStart);
            logger.LogInformation(
                "Presenter teardown completed cleanly in {ElapsedMs:F0}ms: compositor drained in-flight "
                    + "presents and the producer rings were disposed off the UI thread (no deadlock).",
                elapsedMs
            );
        }
        else
        {
            // Step 4: compositor wedged (e.g. mid display transition). Do NOT enter the
            // synchronous producer-destroying path on the UI thread, and do NOT leak: hand
            // the producers to a background reaper that disposes them once the gating tasks
            // complete (after the compositor recovers).
            logger.LogWarning(
                "Presenter teardown: compositor did not drain in-flight presents within {Timeout}ms; "
                    + "deferring producer disposal to the background reaper to keep the UI thread free.",
                (int)PresentDrainTimeout.TotalMilliseconds
            );
            PresenterTeardownMetrics.RecordDeferred();
            _ = PresenterTeardownReaper
                .Enqueue(gating, logger, converter, d3d12Converter, uploader)
                .ContinueWith(
                    static (_, state) => ((TaskCompletionSource)state!).TrySetResult(),
                    producersDisposed,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
        }
    }

    /// <summary>
    /// Disposes the current <see cref="_imported"/> ring through the compositor and nulls the
    /// <see cref="_imported"/> slots (but <b>not</b> <see cref="_presentTasks"/> — the caller
    /// snapshots and drains those separately, since the in-flight present is a distinct piece
    /// of compositor work from the image disposal). Each
    /// <see cref="ICompositionImportedGpuImage"/> is an <see cref="IAsyncDisposable"/> whose
    /// <c>DisposeAsync</c> posts to the compositor render thread; the returned task completes
    /// when all of them finish (or never, if the compositor is wedged — bounded by the
    /// caller). Safe to call with an empty ring.
    /// </summary>
    private Task DisposeImportedAsync()
    {
        List<Task>? disposals = null;
        for (var i = 0; i < _imported.Length; i++)
        {
            var img = _imported[i];
            _imported[i] = null;
            if (img is null)
                continue;
            (disposals ??= new List<Task>()).Add(DisposeImportedOneAsync(img));
        }
        return disposals is null ? Task.CompletedTask : Task.WhenAll(disposals);
    }

    private async Task DisposeImportedOneAsync(ICompositionImportedGpuImage img)
    {
        try
        {
            await img.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing an imported compositor image faulted (ignored).");
        }
    }

    /// <summary>
    /// Awaits the in-flight present hand-offs in <paramref name="presentTasks"/> (the
    /// compositor's <c>UpdateWithKeyedMutexAsync</c> tasks, captured by the caller before any
    /// ring clearing). Faults are swallowed — a faulted present has still released its keyed
    /// mutex, which is all the drain cares about.
    /// </summary>
    private static Task DrainPresentsAsync(Task?[] presentTasks)
    {
        List<Task>? inflight = null;
        foreach (var t in presentTasks)
        {
            if (t is { IsCompleted: false })
                (inflight ??= new List<Task>()).Add(t);
        }
        if (inflight is null)
            return Task.CompletedTask;
        return Task.WhenAll(inflight).ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>
    /// Awaits <paramref name="task"/> but returns <see langword="false"/> if it does not
    /// complete within <paramref name="timeout"/> (instead of blocking forever). The
    /// timed-out task is left running for the reaper to observe.
    /// </summary>
    private static async Task<bool> AwaitBoundedAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
            return true;
        var delay = Task.Delay(timeout);
        var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);
        return ReferenceEquals(winner, task);
    }

    /// <summary>
    /// Disposes the imported ring through the compositor on a source flip / device-loss
    /// rebuild and clears the ring + present-task arrays, fire-and-forget. Unlike the
    /// teardown path this is on the live present cadence (UI thread): the disposals are
    /// posted to the compositor render thread and we don't await them, so the UI thread is
    /// not blocked. The present-task slots are cleared so the rebuilt ring re-imports from
    /// scratch on the next present (the in-flight presents drain on the compositor thread on
    /// their own; on the live path there is no wedged-compositor concern — that's the
    /// teardown path's job).
    /// </summary>
    private void DetachImported()
    {
        _ = DisposeImportedAsync(); // nulls _imported slots, posts disposals to compositor.
        Array.Clear(_presentTasks);
    }

    /// <summary>Why the cached GPU converter is being dropped + rebuilt (ADR-0064).</summary>
    internal enum GpuConverterDropReason
    {
        /// <summary>Device-loss (TDR / DEVICE_REMOVED) was observed on it (step 6 guard).</summary>
        DeviceLost,
        /// <summary>A warm-sink swap whose in-place decode-bridge rebind failed on this GPU (the
        /// otherwise-zero <c>device_change_rebuilds</c> regression alarm).</summary>
        DeviceChangeRebindFailed,
        /// <summary>An incoming frame's dimensions differ from the converter's — the fixed-size ring +
        /// staging cannot rebind, so the converter is rebuilt at the new size.</summary>
        ResolutionChange,
        /// <summary>The frames switched between D3D11VA and D3D12VA, which have different converters.</summary>
        BackendChange,
        /// <summary>A D3D11VA frame's sample format differs from the one the converter was built for (#559).</summary>
        FormatChange,
        /// <summary>A D3D12VA frame came from another device; the D3D12 converter rebuilds rather than rebinds.</summary>
        DecodeDeviceChange,
        /// <summary>The output the super-resolution decision wants has settled on another one (#560).</summary>
        OutputChange,
        /// <summary><see cref="DriverSuperResolution"/> changed (#560).</summary>
        SuperResolutionToggled,
    }

    /// <summary>Drops the GPU converter, D3D11 or D3D12, and its imported ring; a fresh converter is built on the
    /// next frame. Triggered by device-loss (step 6), a resolution change (the fixed-size ring can't
    /// rebind), or the warm-sink player-swap fallback (the converter owns its device and normally
    /// rebinds in place across a swap — ADR-0064 — so the swap-fallback rebuild is only taken
    /// when that in-place rebind fails on a driver).</summary>
    private void DropGpuConverter(GpuConverterDropReason reason)
    {
        switch (reason)
        {
            case GpuConverterDropReason.DeviceChangeRebindFailed:
                _logger.LogWarning(
                    "Presenter GPU converter could not rebind to the new decode device (warm-sink player swap, "
                        + "ADR-0064); falling back to dropping it and rebuilding on the new device on the next frame."
                );
                PresenterTeardownMetrics.RecordDeviceChangeRebuild();
                break;
            case GpuConverterDropReason.ResolutionChange:
                _logger.LogInformation(
                    "Presenter GPU converter dimensions changed (mixed-resolution playlist item); dropping it and "
                        + "rebuilding at the new size on the next frame."
                );
                PresenterTeardownMetrics.RecordResolutionChangeRebuild();
                break;
            case GpuConverterDropReason.BackendChange:
                _logger.LogInformation(
                    "Presenter frames switched between D3D11VA and D3D12VA; dropping the GPU converter and building "
                        + "the other one."
                );
                break;
            case GpuConverterDropReason.FormatChange:
                _logger.LogInformation(
                    "Presenter D3D11VA frames changed sample format; rebuilding the converter for it (#559)."
                );
                break;
            case GpuConverterDropReason.DecodeDeviceChange:
                _logger.LogInformation(
                    "Presenter D3D12VA frames come from a new decode device; rebuilding the D3D12 converter on it."
                );
                break;
            case GpuConverterDropReason.OutputChange:
                _logger.LogInformation(
                    "Presenter output settled on {Mode} at {W}x{H}; rebuilding the converter for it (#560).",
                    _outputSettle.Applied is { SuperResolution: true } ? "driver super resolution" : "the shader",
                    _outputSettle.Applied?.Width ?? 0,
                    _outputSettle.Applied?.Height ?? 0
                );
                break;
            case GpuConverterDropReason.SuperResolutionToggled:
                _logger.LogInformation(
                    "Presenter driver super resolution turned {State}; rebuilding the converter (#560).",
                    _driverSuperResolution ? "on" : "off"
                );
                break;
            default: // DeviceLost
                _logger.LogWarning(
                    "Presenter GPU converter device-loss (TDR / DEVICE_REMOVED) detected; dropping it and "
                        + "rebuilding on the next frame (step 6 guard)."
                );
                PresenterTeardownMetrics.RecordDeviceLostRebuild();
                break;
        }
        DetachImported();
        _nextBuffer = 0;
        _activeIsGpu = null;
        var conv = _gpuConverter;
        var d3d12 = _d3d12Converter;
        _gpuConverter = null;
        _d3d12Converter = null;
        SuperResolutionLease.Process.Release(this);
        // Dispose off the UI thread: a lost device's Release can still stall in the driver, and the
        // D3D12 converter waits for its GPU work before releasing anything.
        if (conv is not null)
            Task.Run(conv.Dispose);
        if (d3d12 is not null)
            Task.Run(d3d12.Dispose);
    }

    /// <summary>
    /// Resizes the D3D11 converter in place for a frame of another size, and rebuilds it when it
    /// cannot be resized. Either way its ring is new, so the old ring's imports are detached and the
    /// replaced parts are disposed off the UI thread, as a dropped converter is.
    /// </summary>
    private void ResizeGpuConverter(IVideoFrame frame, nint texture)
    {
        // CA2000 is a false positive: the replaced parts are disposed on the thread pool below.
#pragma warning disable CA2000
        if (_gpuConverter!.TryResize(texture, frame.Width, frame.Height) is not { } retired)
#pragma warning restore CA2000
        {
            DropGpuConverter(ResizeFallbackReason(_gpuConverter.IsDeviceLost));
            return;
        }

        PresenterTeardownMetrics.RecordResolutionChangeResize();
        DetachImported();
        _nextBuffer = 0;
        _activeIsGpu = null;
        Task.Run(retired.Dispose);
    }

    /// <summary>
    /// Why a converter that could not be resized is dropped. A resize that failed on device loss
    /// has marked the converter lost, and that drop is a device-loss rebuild, logged and counted as
    /// one; any other failure is a resolution-change rebuild.
    /// </summary>
    internal static GpuConverterDropReason ResizeFallbackReason(bool converterDeviceLost) =>
        converterDeviceLost ? GpuConverterDropReason.DeviceLost : GpuConverterDropReason.ResolutionChange;

    /// <summary>Drops the CPU uploader and its imported ring after a device-loss; a fresh
    /// uploader is built on the next frame (step 6).</summary>
    private void DropCpuUploader()
    {
        _logger.LogWarning(
            "Presenter CPU uploader device-loss (TDR / DEVICE_REMOVED) detected; dropping it and "
                + "rebuilding on the next frame (step 6 guard)."
        );
        PresenterTeardownMetrics.RecordDeviceLostRebuild();
        DetachImported();
        _nextBuffer = 0;
        _activeIsGpu = null;
        var upl = _cpuUploader;
        _cpuUploader = null;
        if (upl is not null)
            Task.Run(upl.Dispose);
    }
}
