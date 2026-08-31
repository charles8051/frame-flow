// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FrameFlow.Avalonia.Windows.Diagnostics;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// An Avalonia <see cref="Control"/> that presents hardware-decoded
/// <see cref="GpuVideoFrame"/>s <b>zero-copy</b>: it color-converts the D3D11VA NV12
/// surface to a shared keyed-mutex BGRA texture on the GPU and imports it straight into
/// the compositor via <see cref="ICompositionGpuInterop"/> (ADR-0016 amendment). No
/// GPU→CPU readback, no <c>WriteableBitmap</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pairs with <see cref="CompositionInteropVideoSink"/>: the sink buffers incoming
/// frames; this control pulls the latest on a render tick and presents it. Use
/// <see cref="EnsureSink"/> to get the <see cref="IVideoSink"/> to hand to the player.
/// </para>
/// <para>
/// Windows / D3D11 only. Spike-grade scope: single video size, latest-frame-wins,
/// VideoProcessor Blt on the UI thread. Remaining follow-ups: off-thread Blt, P010/HDR,
/// mid-stream resolution change. Device-lost handling and ordered imported-image teardown
/// are implemented (investigation 2026-06-12).
/// </para>
/// <para>
/// <b>Teardown ordering (investigation 2026-06-12).</b> The producer's shared keyed-mutex
/// ring is co-owned with the compositor, which acquires it on its render thread with an
/// effectively infinite timeout. Destroying the producer ring while the compositor still
/// holds a copy — during a display/composition transition (e.g. a remote-desktop connect)
/// where the compositor is wedged — hangs the destroying <c>Release</c> forever. So
/// <see cref="Cleanup"/> tears down in a strict order: stop producing, detach from the
/// compositor and dispose the imported images <i>through the compositor render thread</i>,
/// drain in-flight presents with a bounded wait, and only then dispose the producer; if the
/// drain times out (compositor wedged) the producer disposal is deferred to a background
/// reaper rather than blocking the UI thread or leaking.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoView : Control, IVideoSurface, IAsyncDisposable
{
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private ILogger _logger = NullLogger.Instance;

    private CompositionInteropVideoSink? _sink;
    private CpuFramePool? _ownedPool;
    private bool _sinkOwned;

    private Compositor? _compositor;
    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _surfaceVisual;

    private D3D11Nv12SharedConverter? _gpuConverter; // GPU zero-copy source
    private D3D11BgraUploader? _cpuUploader; // CPU upload fallback source
    private bool? _activeIsGpu; // which source the imported[] ring is currently bound to

    private readonly ICompositionImportedGpuImage?[] _imported =
        new ICompositionImportedGpuImage?[D3D11Nv12SharedConverter.BufferCount];
    private readonly Task?[] _presentTasks = new Task?[D3D11Nv12SharedConverter.BufferCount];
    private int _nextBuffer;
    private int _videoWidth;
    private int _videoHeight;

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
    private bool _loggedCpuLive;
    private bool _warnedUnpresentable;
    private bool _warnedPresentFailure;
    private bool _disposed;
    private volatile bool _tornDown;
    private bool _initialized;
    private bool _compositionReady;
    private bool _attached;

    /// <summary>
    /// One sink's attachment to this view. Producer callbacks carry the binding they were
    /// raised under, so a callback that outlives a detach or a sink swap does nothing rather
    /// than driving a view that has moved on.
    /// </summary>
    private sealed class SinkBinding(CompositionInteropVideoSink sink)
    {
        public readonly CompositionInteropVideoSink Sink = sink;

        /// <summary>Set under <c>_presentGate</c> when the attachment ends.</summary>
        public bool Detached;
    }

    // Guards the binding and the queued-present claim. Taken briefly by the producer thread
    // (to claim a post) and by the UI thread (to release it); never held across a present.
    private readonly object _presentGate = new();
    private SinkBinding? _binding;

    // True while a present is queued on the dispatcher. At most one: a frame arriving while
    // one is pending needs no second post, because the queued present takes whatever the
    // slot holds when it runs — which is the newer frame. Without this, a producer running
    // ahead of a busy UI thread queues one delegate per frame.
    private bool _presentPosted;

    // Present cadence/cost instrumentation (#128). UI thread only — PresentPending is the
    // sole caller — so it needs no synchronization. Reports about every 2 s at 60 Hz, and is
    // reset on Initialize so a window cannot span a detach.
    private readonly PresenterTickMeter _tickMeter = new(Stopwatch.Frequency);

    // Present-stall watchdog (investigation 2026-06-12 §9). _lastBltStartedTicks is stamped before
    // each VideoProcessorBlt, so a hung Blt leaves it stale; the watchdog samples it off the UI thread.
    private long _lastBltStartedTicks;
    private PresenterStallWatchdog? _stallWatchdog;


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
    }

    /// <summary>
    /// The sink this view pulls frames from. Assigning a sink marks it caller-owned
    /// (the view won't dispose it); <see cref="EnsureSink"/> creates a view-owned sink.
    /// </summary>
    public CompositionInteropVideoSink? Sink
    {
        get => _sink;
        set
        {
            if (ReferenceEquals(_sink, value))
                return;

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
            // Only on replacement. Teardown leaves the attachment live so a host polling
            // PlaybackDiagnosticsSnapshot after the view detaches still reads the session's
            // final counts instead of zeros.
            lock (_diagnosticsGate)
            {
                if (_attachment is not null)
                    _attachment.Ended = true;
                _attachment = null;
            }

            if (_sink is not null)
                _sink.DiagnosticsSource = null;

            DisposeOwnedSink();
            _sink = value;
            if (_sink is not null)
                BindSink(_sink);
            // The view does the presenting, so it supplies diagnostics for whichever
            // sink it drives — including a caller-assigned one.
            if (_sink is not null)
                WireDiagnostics(_sink);
            _sinkOwned = false;
        }
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
    /// Total frames dropped because every ring buffer still had a present in flight.
    /// Diagnostic surface; a steady climb means the compositor can't keep up with the
    /// feed rate.
    /// </summary>
    /// <remarks>
    /// This is only the ring-full share of the loss, counted for the view's whole lifetime.
    /// Frames superseded at the sink's latest-wins intake — the newest frame replacing one
    /// this render tick never took — are counted by
    /// <see cref="CompositionInteropVideoSink.FramesSuperseded"/>. The diagnostics snapshot
    /// reports the sum of the two, rebased onto the attached sink's window; see
    /// <c>BuildSinkDiagnostics</c>.
    /// </remarks>
    public int FramesDropped => Volatile.Read(ref _framesDropped);

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
    /// Wires the logger, materializes the owned sink, brings up the compositor surface +
    /// GPU interop, and starts the render tick. Call once the control is attached to the
    /// visual tree (e.g. from the window's <c>OnLoaded</c>) and before playback starts.
    /// </summary>
    public void Initialize(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<CompositionInteropVideoView>();
        _initialized = true;

        EnsureSink();
        _tickMeter.Reset();

        if (_stallWatchdog is null)
        {
            _stallWatchdog = new PresenterStallWatchdog(SampleStall, _logger);
            _stallWatchdog.Stalled += OnStallDetected;
            _stallWatchdog.Recovered += OnStallRecovered;
        }

        // The composition surface needs the control attached; set up now if we already
        // are, otherwise OnAttachedToVisualTree does it. (When hosted in a
        // FrameFlowPlayerView, AttachSink may run before or after we enter the tree.)
        TrySetupComposition();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        TrySetupComposition();
    }

    /// <summary>
    /// Ensures a view-owned sink exists (with its own <see cref="CpuFramePool"/>) and
    /// returns it as the <see cref="IVideoSink"/> to hand to the player. The view disposes
    /// the owned sink on detach / <see cref="DisposeAsync"/>.
    /// </summary>
    public CompositionInteropVideoSink EnsureSink()
    {
        if (_sink is not null)
            return _sink;

        var pool = new CpuFramePool(_loggerFactory.CreateLogger<CpuFramePool>());
        _ownedPool = pool;
        _sink = new CompositionInteropVideoSink(
            pool,
            _loggerFactory.CreateLogger<CompositionInteropVideoSink>()
        );
        WireDiagnostics(_sink);
        BindSink(_sink);
        _sinkOwned = true;
        return _sink;
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

        lock (_diagnosticsGate)
            _attachment = attachment;

        sink.DiagnosticsSource = () => BuildSinkDiagnostics(sink, attachment);
    }

    // The view owns the presented count and the ring-full drop count, so it supplies the
    // sink's diagnostics. Mirrors AvaloniaVideoSink.GetDiagnostics on the CPU path; without
    // it the zero-copy presenter reported all-zero video counts.
    //
    // FramesDropped is the SUM of both places a frame dies on this path:
    //   1. superseded at the sink's latest-wins slot (feed rate > render-tick rate), and
    //   2. dropped here in PresentRing because the whole ring was still in flight.
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
    bool IVideoSurface.PrefersHardwareFrames => true;
    IVideoSink IVideoSurface.AttachSink(ILoggerFactory loggerFactory)
    {
        Initialize(loggerFactory);
        return EnsureSink();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Cleanup();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Binds <paramref name="sink"/>'s arrival callback to this view, ending any previous
    /// attachment first.
    /// </summary>
    private void BindSink(CompositionInteropVideoSink sink)
    {
        UnbindSink();

        var binding = new SinkBinding(sink);

        // Callback first, then publish. The callback carries its own binding token and does
        // not read _binding, so installing it early is safe — and it closes the window where
        // a frame installs against a null hook and schedules nothing. Only UnbindSink reads
        // _binding, and it cannot run concurrently: both are called from the UI thread.
        sink.FrameArrived = () => OnFrameArrived(binding);

        lock (_presentGate)
            _binding = binding;

        // A frame may already be waiting — fed before the sink was assigned, or installed in
        // the instant before the hook went on. No later arrival is guaranteed to come and
        // collect it: on a paused or ended stream that frame is the one meant to be on
        // screen. Sweep once now; the coalescing claim makes a redundant sweep a no-op.
        if (sink.HasPendingFrame)
            OnFrameArrived(binding);
    }

    /// <summary>
    /// Ends the current attachment. A callback already running sees <c>Detached</c> under the
    /// same gate and does nothing; one that has not started never posts.
    /// </summary>
    private void UnbindSink()
    {
        SinkBinding? binding;
        lock (_presentGate)
        {
            binding = _binding;
            if (binding is null)
                return;
            binding.Detached = true;
            _binding = null;
        }

        binding.Sink.FrameArrived = null;
    }

    /// <summary>
    /// Schedules a present for a frame the sink just installed. Runs on the presenting (graph)
    /// thread, and deliberately does no rendering: the compositor work is UI-thread affine, so
    /// all this does is wake the UI thread.
    /// </summary>
    /// <remarks>
    /// This replaces a <c>DispatcherTimer</c>. Avalonia's dispatcher timer is a message-queue
    /// timer quantized to the ~15.625 ms platform tick, so a 16 ms request was delivered at
    /// ~26 ms and capped the presenter near 38 fps against a 60 fps source however cheap the
    /// present was — measured at 0.3 ms (issue #128). A post carries no such quantum: it runs
    /// when the dispatcher next drains.
    /// </remarks>
    private void OnFrameArrived(SinkBinding binding)
    {
        if (_tornDown)
            return;

        lock (_presentGate)
        {
            if (binding.Detached || _presentPosted)
                return;
            _presentPosted = true;
        }

        if (TryPostPresent())
            return;

        // The post never landed, so nothing will release the claim and every later arrival
        // would skip posting behind it — a permanent freeze from a transient failure. Release
        // it, then try once more in case a frame is still waiting: on a paused or ended
        // stream no later arrival will come to retry for us, and that last frame is the one
        // left on screen.
        lock (_presentGate)
            _presentPosted = false;

        if (_sink?.HasPendingFrame == true)
        {
            lock (_presentGate)
            {
                if (_presentPosted)
                    return;
                _presentPosted = true;
            }

            if (!TryPostPresent())
                lock (_presentGate)
                    _presentPosted = false;
        }
    }

    /// <summary>
    /// Posts a present if the bound sink is holding a frame. Safe to call spuriously — the
    /// coalescing claim collapses a redundant call, and a present that finds nothing simply
    /// returns.
    /// </summary>
    private void SchedulePresentIfPending()
    {
        SinkBinding? binding;
        lock (_presentGate)
            binding = _binding;

        if (binding is not null && binding.Sink.HasPendingFrame)
            OnFrameArrived(binding);
    }

    private bool TryPostPresent()
    {
        try
        {
            Dispatcher.UIThread.Post(PresentPending, DispatcherPriority.Render);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Takes the latest frame and presents it. UI thread only — every compositor and D3D11
    /// call below is thread-affine.
    /// </summary>
    private void PresentPending()
    {
        // Released before the work, not after, so a frame arriving mid-present can claim the
        // next post and be queued behind this one rather than waiting for it to finish.
        lock (_presentGate)
            _presentPosted = false;

        // Leave the frame pending rather than taking it: Present would drop it, and the
        // Sink setter can bind before Initialize/TrySetupComposition has run, so early
        // frames would be consumed and discarded. Held instead, the newest one is shown as
        // soon as setup completes — which sweeps the slot itself.
        if (_tornDown || _interop is null || _surface is null)
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

    // Liveness snapshot for the stall watchdog — read off the UI thread, so only volatile counters.
    private PresenterSample SampleStall() => new(
        Stopwatch.GetTimestamp(),
        Volatile.Read(ref _framesPresented),
        Volatile.Read(ref _framesCommitted),
        _sink?.FramesAccepted ?? 0,
        Volatile.Read(ref _lastBltStartedTicks));

    private void OnStallDetected(PresenterStallInfo info) => PresenterStalled?.Invoke(this, info);

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
        /// The incoming frame's dimensions differ from the cached converter's. The ring + staging
        /// textures are sized at construction, so a resolution change <b>cannot</b> be an in-place
        /// rebind (the per-frame copy Box and the BGRA ring would be the wrong size) — the converter
        /// must be dropped and rebuilt at the new size. Takes priority over a device change, since a
        /// mixed-resolution playlist swap changes both at once.
        /// </summary>
        RebuildForResolutionChange = 3,
    }

    /// <summary>
    /// Pure decision for how to handle the cached zero-copy converter for an incoming frame
    /// (ADR-0064): reuse it, rebuild it after device-loss, rebuild it on a resolution change, or
    /// rebind its decode bridge after a same-size warm-sink player swap. Priority is device-loss,
    /// then resolution change (must rebuild — the ring/staging are fixed-size), then a same-size
    /// device change (rebind in place). A <paramref name="frameDevice"/> of <see cref="nint.Zero"/>
    /// means the frame's device identity is unknown (chain unavailable) and is never treated as a
    /// mismatch — reuse rather than thrash on missing telemetry. Extracted as a static so the
    /// swap-detection logic is unit-testable without a GPU.
    /// </summary>
    internal static ConverterAction EvaluateConverterAction(
        bool hasCached, nint cachedDevice, bool cachedDeviceLost, nint frameDevice,
        int cachedWidth, int cachedHeight, int frameWidth, int frameHeight)
    {
        if (!hasCached)
            return ConverterAction.Reuse;
        if (cachedDeviceLost)
            return ConverterAction.RebuildForDeviceLoss;
        // A resolution change forces a rebuild regardless of device: the converter's ring + staging
        // textures and the per-frame copy Box are sized at construction and a rebind cannot resize
        // them. Checked before the device change so a mixed-resolution swap (new device AND new size)
        // rebuilds rather than rebinding onto a wrong-sized ring.
        if (frameWidth != cachedWidth || frameHeight != cachedHeight)
            return ConverterAction.RebuildForResolutionChange;
        if (frameDevice != nint.Zero && cachedDevice != frameDevice)
            return ConverterAction.RebindDecodeDevice;
        return ConverterAction.Reuse;
    }

    // ── Composition / interop setup ────────────────────────────────
    private void TrySetupComposition()
    {
        // Run only once the logger is wired (Initialize) AND we're attached (the
        // compositor exists). Called from both Initialize and OnAttachedToVisualTree;
        // whichever happens second does the actual setup.
        if (!_initialized || _compositionReady || !_attached)
            return;
        SetupComposition();
        _compositionReady = true;
    }

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

        SizeChanged += (_, _) => UpdateSurfaceLayout();

        _ = InitInteropAsync();
    }

    private async Task InitInteropAsync()
    {
        try
        {
            _interop = await _compositor!.TryGetCompositionGpuInterop();
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
    /// Sizes + centers the surface visual to a letterboxed rect that preserves the
    /// video's aspect ratio within the control bounds (the black window background shows
    /// in the bars). Called on attach, on resize, and when the video size is first known.
    /// </summary>
    private void UpdateSurfaceLayout()
    {
        if (_surfaceVisual is null)
            return;

        var b = Bounds;
        if (_videoWidth <= 0 || _videoHeight <= 0 || b.Width <= 0 || b.Height <= 0)
        {
            _surfaceVisual.Size = new Vector(b.Width, b.Height);
            _surfaceVisual.Offset = new System.Numerics.Vector3(0f, 0f, 0f);
            return;
        }

        var scale = Math.Min(b.Width / _videoWidth, b.Height / _videoHeight);
        var w = _videoWidth * scale;
        var h = _videoHeight * scale;
        _surfaceVisual.Size = new Vector(w, h);
        _surfaceVisual.Offset = new System.Numerics.Vector3(
            (float)((b.Width - w) / 2),
            (float)((b.Height - h) / 2),
            0f
        );
    }

    // ── Per-frame present (UI thread, from the render tick) ────────
    private void Present(IVideoFrame frame)
    {
        try
        {
            if (_tornDown || _interop is null || _surface is null)
                return; // teardown started, or interop not ready yet — drop.

            if (frame is GpuVideoFrame gpu && gpu.TryGetD3D11Texture(out var texture, out var slice, out var device))
            {
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
                //       change always rebuilds (the ring/staging are fixed-size — a rebind can't resize).
                switch (EvaluateConverterAction(
                    hasCached: _gpuConverter is not null,
                    cachedDevice: _gpuConverter?.SourceDevicePointer ?? nint.Zero,
                    cachedDeviceLost: _gpuConverter?.IsDeviceLost ?? false,
                    frameDevice: device,
                    cachedWidth: _gpuConverter?.Width ?? 0,
                    cachedHeight: _gpuConverter?.Height ?? 0,
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
                _gpuConverter ??= new D3D11Nv12SharedConverter(texture, frame.Width, frame.Height, _logger);
                PresentRing(
                    isGpu: true,
                    frame.Width,
                    frame.Height,
                    _gpuConverter.GetSharedHandle,
                    i => _gpuConverter.ConvertInto(i, texture, slice),
                    frame.Pts
                );
            }
            else if (frame.Format == FrameFlow.Media.PixelFormat.Bgra32 && frame.AsCpu() is { } cpu)
            {
                // Upload fallback: software-decoded BGRA frame (no D3D11VA).
                if (_cpuUploader is { IsDeviceLost: true })
                    DropCpuUploader();
                _cpuUploader ??= new D3D11BgraUploader(frame.Width, frame.Height, _logger);
                PresentRing(
                    isGpu: false,
                    frame.Width,
                    frame.Height,
                    _cpuUploader.GetSharedHandle,
                    i => _cpuUploader.UploadInto(i, cpu),
                    frame.Pts
                );
            }
            else if (!_warnedUnpresentable)
            {
                _warnedUnpresentable = true;
                _logger.LogWarning(
                    "Unpresentable frame: domain={Domain}, format={Format}, type={Type}. "
                        + "Expected a D3D11VA GpuVideoFrame or a Bgra32 CPU frame.",
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
        bool isGpu,
        int width,
        int height,
        Func<int, nint> getSharedHandle,
        Func<int, bool> fill,
        TimeSpan pts
    )
    {
        if (_tornDown)
            return;

        // Decide the per-frame outcome purely (PresentPlanner): build the current ring/source/size
        // state from the scattered fields, fold in the incoming frame's descriptor, and read back
        // which free slot to fill (or to drop), whether the source flipped (re-import the ring),
        // and whether the video size changed (update the layout). The decision touches no D3D /
        // compositor / clock; this shell performs the named outcomes below in plan order.
        var plan = PresentPlanner.Advance(
            CurrentPresentState(),
            new FrameDescriptor(isGpu, width, height),
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
                Width = width,
                Height = height,
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
        presentTask.ContinueWith(
            static (_, state) =>
            {
                var self = (CompositionInteropVideoView)state!;
                Interlocked.Increment(ref self._framesCommitted);
                Volatile.Write(ref self._lastCommittedAtUtcTicks, DateTime.UtcNow.Ticks);
            },
            this,
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
        LogProgress(isGpu);
    }

    private void LogProgress(bool isGpu)
    {
        var presented = Volatile.Read(ref _framesPresented);

        if (isGpu && !_loggedGpuLive)
        {
            _loggedGpuLive = true;
            _logger.LogInformation(
                "ZERO-COPY PATH LIVE: D3D11VA NV12 → VideoProcessor BGRA ({N}-buffer shared keyed-mutex "
                    + "ring) → ICompositionGpuInterop.ImportImage → compositor. No CPU round-trip.",
                D3D11Nv12SharedConverter.BufferCount
            );
        }
        else if (!isGpu && !_loggedCpuLive)
        {
            _loggedCpuLive = true;
            _logger.LogInformation(
                "CPU-UPLOAD FALLBACK LIVE: software BGRA frame → staging → shared keyed-mutex texture "
                    + "({N}-buffer ring) → ICompositionGpuInterop.ImportImage → compositor (hardware decode "
                    + "did not engage).",
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
                    + "({Superseded} superseded at the sink, {RingFull} ring-full).",
                isGpu ? "zero-copy" : "cpu-upload",
                presented,
                Volatile.Read(ref _framesDropped) + (_sink?.FramesSuperseded ?? 0),
                _sink?.FramesSuperseded ?? 0,
                Volatile.Read(ref _framesDropped)
            );
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CleanupAsync());

    /// <summary>
    /// Synchronous detach entry point (visual-tree detach / window close). Kicks off the
    /// ordered async teardown and returns immediately — the UI thread is never blocked on
    /// the compositor. The async teardown is self-contained (it owns snapshots of every
    /// resource it frees), so fire-and-forget is safe here; <see cref="DisposeAsync"/>
    /// awaits the same path when a caller wants completion. A fault continuation observes the
    /// discarded task so a teardown error never surfaces as an unobserved task exception.
    /// </summary>
    private void Cleanup()
    {
        var teardown = CleanupAsync();
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
    /// Ordered teardown that never leaves the producer waiting on the compositor at dispose
    /// (investigation 2026-06-12, §6). Sequence: (1) stop producing; (2) detach from the
    /// compositor and dispose the imported images through its render thread; (3) drain
    /// in-flight presents with a bounded wait; (4) on success dispose the producer off the
    /// UI thread, on timeout defer producer disposal to a background reaper (never block,
    /// never leak).
    /// </summary>
    private async Task CleanupAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _tornDown = true; // step 1: OnRenderTick/Present/PresentRing post nothing further.

        // Observability (investigation 2026-06-12): time the ordered teardown so the healthy
        // "drained cleanly" path is visible in logs + the FrameFlow.Presenter meter, not just
        // the deferral. Without this a clean teardown leaves no trace and the fix can only be
        // seen when it hits the bad (wedged) path.
        var teardownStart = PresenterTeardownMetrics.BeginTeardown();

        // Stop the producer scheduling presents into a view that is tearing down. A callback
        // already inside OnFrameArrived sees Detached under the same gate.
        UnbindSink();

        // Stop the stall watchdog before teardown so it cannot fire on the intended stop.
        _stallWatchdog?.Dispose();
        _stallWatchdog = null;

        DisposeOwnedSink(); // stop the frame source feeding new presents.

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

        // Step 3: drain in-flight presents (a present holding key 1 must finish and release
        // to 0). Both the imported disposal and the drain need the compositor render thread
        // to make progress, so wait on them together under one bounded timeout.
        var presentDrain = DrainPresentsAsync(presentSnapshot);
        var gating = Task.WhenAll(importedDisposal, presentDrain);
        var drained = await AwaitBoundedAsync(gating, PresentDrainTimeout).ConfigureAwait(false);

        // Capture the producers and detach them from the instance so a racing re-entrant
        // teardown can't double-dispose. (_disposed already guards re-entry, but nulling keeps
        // the fields honest.) Only the producers — the keyed-mutex rings whose native Release
        // can block — are deferred off-thread; the surface is already gone (UI thread, above).
        var converter = _gpuConverter;
        var uploader = _cpuUploader;
        _gpuConverter = null;
        _cpuUploader = null;

        if (drained)
        {
            // Compositor released the keyed mutex; destroying the producer ring is now safe.
            // We're on a thread-pool continuation (ConfigureAwait(false)), never the UI
            // thread — so even the borrowed-device Release at the converter's tail can't
            // hang the UI.
            converter?.Dispose();
            uploader?.Dispose();
            var elapsedMs = PresenterTeardownMetrics.RecordCompleted(teardownStart);
            _logger.LogInformation(
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
            _logger.LogWarning(
                "Presenter teardown: compositor did not drain in-flight presents within {Timeout}ms; "
                    + "deferring producer disposal to the background reaper to keep the UI thread free.",
                (int)PresentDrainTimeout.TotalMilliseconds
            );
            PresenterTeardownMetrics.RecordDeferred();
            PresenterTeardownReaper.Enqueue(gating, converter, uploader, _logger);
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
    private enum GpuConverterDropReason
    {
        /// <summary>Device-loss (TDR / DEVICE_REMOVED) was observed on it (step 6 guard).</summary>
        DeviceLost,
        /// <summary>A warm-sink swap whose in-place decode-bridge rebind failed on this GPU (the
        /// otherwise-zero <c>device_change_rebuilds</c> regression alarm).</summary>
        DeviceChangeRebindFailed,
        /// <summary>An incoming frame's dimensions differ from the converter's — the fixed-size ring +
        /// staging cannot rebind, so the converter is rebuilt at the new size.</summary>
        ResolutionChange,
    }

    /// <summary>Drops the GPU converter and its imported ring; a fresh converter is built on the
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
        _gpuConverter = null;
        // Dispose off the UI thread: a lost device's Release can still stall in the driver.
        if (conv is not null)
            Task.Run(conv.Dispose);
    }

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

    private void DisposeOwnedSink()
    {
        if (!_sinkOwned)
        {
            _sink = null;
            return;
        }

        var sink = _sink;
        var pool = _ownedPool;
        _sink = null;
        _ownedPool = null;
        _sinkOwned = false;

        if (sink is not null)
        {
            var t = sink.DisposeAsync();
            if (t.IsCompleted)
                t.GetAwaiter().GetResult();
            else
                t.AsTask().GetAwaiter().GetResult();
        }
        pool?.Dispose();
    }
}
