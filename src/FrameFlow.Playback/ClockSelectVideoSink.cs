// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Playback;

/// <summary>
/// The <b>imperative shell</b> of presenter-side select-by-clock pacing
/// (ADR-0057 Stage 2). Decorates the real video <see cref="IVideoSink"/>: it
/// accepts decoded frames into a small PTS-ordered ring
/// (<see cref="ClockSelectBuffer"/>, the pure core) and delivers exactly the
/// frame that is due "now" on the master <see cref="IClockSource"/> to the inner
/// sink, dropping any late frames.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this replaces <see cref="PaceUntil"/> on the single-sink video
/// path.</b> The old pacing operator awaited the clock <i>while holding the
/// frame inside the graph operator</i>, and the graph edges were capacity-1: on
/// the zero-copy path that pinned a D3D11VA decode-texture slice across the wait
/// with <b>no slack</b>, so a single long wait drained the FFmpeg-default
/// hwframe pool and stalled the decoder (the confirmed choppiness +
/// lockstep-drop coupling, perf survey §A1/§A4). Here the clock wait happens in
/// this shell, not in the graph operator: the graph's 1→1 sink-pump hands each
/// frame to <see cref="PresentAsync"/> and is released the moment it returns, so
/// no decode lease is ever held <i>inside the graph</i> across a clock wait.
/// </para>
/// <para>
/// <b>Backpressure, not free-running.</b> The decoder still runs at ~real-time:
/// <see cref="PresentAsync"/> blocks (async) when the ring is full, which
/// backpressures the graph sink-pump → the capacity-1 edges → the decoder, so it
/// stays only a few frames (the ring depth) ahead of the clock. The improvement
/// over PaceUntil is the <b>depth of that slack</b> (a few frames vs. one held
/// frame) and that <b>late frames are dropped</b> (their lease returns at once)
/// instead of one frame being pinned for the entire wait. A misaligned/stalled
/// clock therefore degrades to choppy-but-alive (cap → present + drop) rather
/// than a frozen pool.
/// </para>
/// <para>
/// <b>Pacing stays sink-agnostic.</b> Because the decorator wraps whatever
/// <see cref="IVideoSink"/> the consumer supplied (Avalonia zero-copy,
/// <c>WriteableBitmap</c>, SDL, headless/test), every single-sink path stays
/// correctly paced — a clip no longer "plays" in &lt;100&#160;ms on a fast host.
/// The downstream presenter keeps its own latest-wins render tick; it now only
/// ever receives clock-selected frames, so latest-wins there is harmless.
/// </para>
/// <para>
/// <b>End-of-stream is real-time-gated.</b> Because frames arrive at decode rate,
/// graph completion does not mean the clip finished playing. The session marks
/// input complete (<see cref="SignalInputComplete"/>) and awaits
/// <see cref="WaitForDrainAsync"/> — which fires only once the last buffered
/// frame has been delivered at clock cadence — before raising Ended. This keeps
/// a no-audio <c>RepeatMode.One</c> loop ticking once per clip-duration rather
/// than at decode speed. <see cref="BeginRun"/> resets that state per run.
/// </para>
/// <para>
/// <b>Lifecycle / ownership.</b> Constructed and owned by
/// <see cref="SubstrateSession"/> around the consumer's sink. The decorator does
/// <b>not</b> own the inner sink (ADR-0044: sessions are users, not owners, of
/// sinks) — <see cref="DisposeAsync"/> tears down the delivery loop and disposes
/// only buffered frames, never the inner sink. The session calls
/// <see cref="Flush"/> at a seek/loop discontinuity so pre-seek frames never
/// present against the post-seek timeline.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="PresentAsync"/> may be called from any graph
/// pump thread; the delivery loop runs on its own task. A single lock guards the
/// ring. The inner sink's <c>PresentAsync</c> is invoked only from the delivery
/// loop (one frame at a time, in order), matching the single-writer expectation
/// every <see cref="IVideoSink"/> already has from the graph.
/// </para>
/// </remarks>
internal sealed partial class ClockSelectVideoSink : IVideoSink
{
    /// <summary>
    /// Default ring depth. Small so that at most ~this many GPU decode-texture
    /// slices are pinned at once (the FFmpeg-default D3D11VA pool is modest),
    /// while still giving the decoder enough read-ahead that a single late wakeup
    /// (Windows ~15&#160;ms timer granularity) doesn't instantly starve the pool —
    /// a couple of frames of slack turn "every late wakeup drops a frame" into
    /// "only sustained lateness drops" (perf survey §A4).
    /// </summary>
    public const int DefaultCapacity = 3;

    private readonly IVideoSink _inner;
    private readonly IClockSource _clock;
    private readonly ILogger _logger;
    private readonly int _capacity;
    private readonly TimeSpan _maxWait;

    private readonly object _gate = new();
    private readonly ClockSelectBuffer _buffer;

    // Async backpressure: one permit per free ring slot. PresentAsync acquires a
    // permit before adding; the delivery loop releases one per frame it removes
    // (presented, dropped-late, flushed, or drained on teardown). Bounds the ring
    // to _capacity and holds the decoder to ~real-time.
    private readonly SemaphoreSlim _space;

    // Wakes the delivery loop when a frame arrives into a previously-empty ring,
    // or when input is signalled complete, so it re-evaluates promptly.
    private readonly AsyncManualResetEvent _arrival = new(initiallySet: false);

    // Interrupts the loop's in-flight clock wait so it re-picks "earliest" when
    // the buffer changes out from under a wait — specifically a Flush that drains
    // the very frame the loop is currently waiting on. Cancel-and-replaced under
    // _gate; the loop links it into WaitUntilAsync and treats its cancellation as
    // "re-evaluate", not an error.
    private CancellationTokenSource _recheck = new();

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _deliveryLoop;
    private int _disposed;

    private int _presented;
    private int _droppedLate;
    private int _droppedBeforeTarget;

    // Pre-target frames discarded since the floor was set, so the count can be logged once
    // when the floor is spent rather than once per frame. Guarded by _gate.
    private int _droppedThisSeek;

    // Completes with the PTS of the first frame admitted at or past this run's seek floor,
    // or with null when the run ends without one arriving. Replaced per run by BeginRun,
    // and already completed for a run that has no floor, so a caller can await it
    // unconditionally. Guarded by _gate.
    private TaskCompletionSource<TimeSpan?> _seekTargetReached = Completed();

    // Closed between the destination frame being admitted and the session reseating the
    // clocks onto it, so no frame is delivered against the clock the seek is about to
    // correct. Open at every other moment.
    private readonly AsyncManualResetEvent _settled = new(initiallySet: true);

    // Whether this run's caller will reseat, and so whether its destination frame should
    // close the gate at all. Guarded by _gate.
    private bool _holdForSettle;

    // The authoritative "delivery is held" flag, guarded by _gate and read in the same
    // critical section that selects a frame. _settled is only how a waiter is woken: an
    // event read outside the lock cannot order against an admission inside it, which is the
    // race a check on the event alone leaves open.
    private bool _settleHeld;

    // Identifies the run that armed the hold, so a settle that finishes late — after its
    // cap, or after a scheduler delay — cannot open the gate a newer run has since closed.
    // Guarded by _gate.
    private long _runId;

    /// <summary>
    /// How long delivery may stay held waiting for a reseat that never comes. A backstop:
    /// every path that arms the hold has a matching release, and the reseat itself runs in
    /// microseconds once the frame lands. It exists so that a path added later without a
    /// release costs a hiccup rather than a frozen picture.
    /// </summary>
    private static readonly TimeSpan SettleHoldBackstop = TimeSpan.FromMilliseconds(250);

    private static TaskCompletionSource<TimeSpan?> Completed()
    {
        var tcs = new TaskCompletionSource<TimeSpan?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        tcs.TrySetResult(null);
        return tcs;
    }

    // ── End-of-stream drain coordination (per graph run). ──
    // The graph forwards all decoded frames to PresentAsync at DECODE rate, then
    // completes. But the clip has only finished PLAYING once this pacer has
    // delivered the last buffered frame at clock cadence. So the session gates
    // its OnEndOfStream on WaitForDrainAsync: the graph task marks input complete
    // (SignalInputComplete) and waits for the buffer to empty before firing EOS.
    // Without this, a video-only clip (no audio pump to gate the graph) would
    // signal Ended at decode speed and a RepeatMode.One loop would fire every
    // few ms instead of every clip-duration. State is reset per run by BeginRun.
    private bool _inputComplete;
    private TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // End instant (Pts + Duration) of the latest frame handed to the inner sink,
    // i.e. when the final presented frame FINISHES its on-screen display. The
    // drain (→ EOS → Ended) is gated on the clock reaching this, not on the last
    // frame merely being SELECTED (Pts ≤ now): selecting it only begins its
    // display. Draining at selection time cut the final frame's display short and
    // fired Ended ~one frame-duration early, and on a video-only
    // wall-clock master that early/again-racy EOS is what left a single-item loop
    // wedged with no terminal Ended. Guarded by _gate; reset per
    // run by BeginRun.
    private TimeSpan _lastFrameEndPts;

    /// <summary>
    /// Wraps <paramref name="inner"/> with select-by-clock delivery against
    /// <paramref name="clock"/>.
    /// </summary>
    /// <param name="inner">The real video sink to deliver selected frames to. Not owned.</param>
    /// <param name="clock">The master pacing clock (audio sink or wallclock).</param>
    /// <param name="logger">Optional logger; per-selection diagnostics surface at Debug/Warning.</param>
    /// <param name="capacity">Ring depth (defaults to <see cref="DefaultCapacity"/>).</param>
    /// <param name="maxWait">
    /// Upper bound on how long a buffered frame waits for the clock to reach its
    /// PTS before it is presented anyway (defense-in-depth, mirrors the old
    /// PaceUntil cap). A misaligned/stalled master clock degrades to
    /// choppy-but-alive instead of buffering forever. Defaults to 5&#160;s.
    /// </param>
    public ClockSelectVideoSink(
        IVideoSink inner,
        IClockSource clock,
        ILogger? logger = null,
        int capacity = DefaultCapacity,
        TimeSpan? maxWait = null
    )
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger.Instance;
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be >= 1.");
        if (maxWait is { } mw && mw <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxWait), mw, "maxWait must be positive when supplied.");
        _capacity = capacity;
        _maxWait = maxWait ?? TimeSpan.FromSeconds(5);
        _buffer = new ClockSelectBuffer(capacity);
        _space = new SemaphoreSlim(capacity, capacity);

        _deliveryLoop = Task.Run(() => RunDeliveryLoopAsync(_shutdownCts.Token));
    }

    /// <inheritdoc/>
    public IFramePool FramePool => _inner.FramePool;

    /// <inheritdoc/>
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
        _inner.OnFormatChangedAsync(format, ct);

    /// <inheritdoc/>
    public VideoSinkDiagnosticsSnapshot GetDiagnostics() => _inner.GetDiagnostics();

    /// <summary>Total frames delivered to the inner sink (selected as due).</summary>
    public int Presented => Volatile.Read(ref _presented);

    /// <summary>Total frames dropped because a fresher due frame superseded them.</summary>
    public int DroppedLate => Volatile.Read(ref _droppedLate);

    /// <summary>
    /// Frames discarded for carrying a PTS below a seek target — the reference frames
    /// between the keyframe the demuxer landed on and the position actually seeked to.
    /// Expected to be non-zero after any seek into a GOP; it is the count that would
    /// otherwise have fast-forwarded (#157).
    /// </summary>
    public int DroppedBeforeTarget => Volatile.Read(ref _droppedBeforeTarget);

    /// <inheritdoc/>
    /// <remarks>
    /// Enqueue into the ring, blocking (async) only while the ring is full so the
    /// decoder is held to ~real-time. No clock wait happens here, so the graph's
    /// sink pump releases its <c>VideoFrameRef</c> wrapper as soon as this
    /// returns — no decode lease is held inside the graph across a clock wait.
    /// </remarks>
    public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (Volatile.Read(ref _disposed) != 0)
        {
            frame.Dispose();
            return;
        }

        // Acquire a ring slot (backpressure). Linked to shutdown so a blocked
        // enqueue releases promptly on teardown.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        try
        {
            await _space.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            frame.Dispose();
            throw new OperationCanceledException(ct.IsCancellationRequested ? ct : linked.Token);
        }

        // Re-check disposal now the permit is held. The check before the wait is not
        // enough: DisposeAsync sets _disposed, cancels, and lets the delivery loop drain
        // the buffer under _gate, so a caller that acquired its permit before all that and
        // reaches the lock afterwards would hand a frame to a buffer nothing will drain
        // again — and then release a permit into an already-disposed semaphore. _disposed
        // is set before the loop's teardown runs, so reading it under _gate here orders
        // this against that drain either way round.
        bool admitted;
        bool floorSpent;
        bool disposed;
        int droppedThisSeek;
        TaskCompletionSource<TimeSpan?>? reachedAt = null;
        lock (_gate)
        {
            disposed = Volatile.Read(ref _disposed) != 0;
            reachedAt = _seekTargetReached;
            var hadFloor = !disposed && _buffer.HasFloor;
            admitted = !disposed && _buffer.Admit(frame);
            floorSpent = hadFloor && admitted;
            // Closed here, under the same lock that made the frame visible to delivery.
            // Doing it after the lock leaves a window in which the loop can take _gate,
            // find the destination frame and select it against the clock the reseat is
            // about to correct.
            if (floorSpent && _holdForSettle)
            {
                _settleHeld = true;
                _settled.Reset();
            }
            if (!disposed && !admitted)
                _droppedThisSeek++;
            droppedThisSeek = _droppedThisSeek;
            if (floorSpent)
                _droppedThisSeek = 0;
        }

        if (disposed)
        {
            // Deliberately no _space.Release(): DisposeAsync disposes the semaphore once
            // the loop has stopped, and releasing into it throws. Permits do not matter to
            // an object being torn down; the frame does.
            frame.Dispose();
            return;
        }

        if (!admitted)
        {
            // A pre-target reference frame: decoded because the frames after it need it,
            // never displayed because the clock is already past it. Releasing the slot here
            // is what keeps the decoder moving on toward the target — the fast-forward was
            // these frames being shown, not these frames existing.
            frame.Dispose();
            _space.Release();
            Interlocked.Increment(ref _droppedBeforeTarget);
            return;
        }

        if (floorSpent)
        {
            if (droppedThisSeek > 0)
                LogDroppedBeforeTarget(_logger, droppedThisSeek, frame.Pts.TotalSeconds);

            // The destination frame exists. Hold delivery before publishing it: the session
            // reseats the clocks onto this frame, and until it has, the delivery loop would
            // be selecting against the very clock the reseat exists to correct — it would
            // present this frame and the ones behind it at decode rate, which is the run-up
            // being removed (#161). The gate was closed under the lock above, before the
            // frame became visible; this only publishes.
            reachedAt?.TrySetResult(frame.Pts);
        }

        // Wake the delivery loop: a frame is available (the ring may have been empty).
        _arrival.Set();
    }

    /// <summary>
    /// Drops every buffered frame (disposing it) and releases its ring slot. Call
    /// at a seek/loop discontinuity so frames decoded against the pre-seek
    /// timeline never present after the clock rebases. Safe to call concurrently
    /// with the delivery loop and <see cref="PresentAsync"/>.
    /// </summary>
    /// <remarks>
    /// Drains only. The post-seek floor is armed by <see cref="BeginRun"/>, because this
    /// runs at the start of the discontinuity recipe, before the reposition that can still
    /// fail — a floor set here would outlive an abandoned seek.
    /// </remarks>
    public void Flush()
    {
        List<IVideoFrame> toDispose = new();
        lock (_gate)
        {
            _buffer.DrainInto(toDispose);
            // After a flush there is nothing pending; the loop should park until
            // the next post-seek frame arrives.
            _arrival.Reset();
            // Break any in-flight clock wait — the loop may be waiting on a frame
            // we just drained; force it to re-pick "earliest" (now empty).
            TriggerRecheckLocked();
        }

        foreach (var f in toDispose)
            f.Dispose();
        if (toDispose.Count > 0)
            _space.Release(toDispose.Count);
    }

    // Cancel-and-replace the recheck CTS so a loop blocked in WaitUntilAsync wakes
    // and re-evaluates. Must hold _gate.
    private void TriggerRecheckLocked()
    {
        var old = _recheck;
        _recheck = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
    }

    /// <summary>
    /// Resets per-run end-of-stream drain state at the start of a graph run.
    /// Call before (re)starting the graph that feeds this pacer (initial play, or
    /// a seek/loop resume) so a prior run's "drained" signal can't satisfy the
    /// new run's <see cref="WaitForDrainAsync"/>.
    /// </summary>
    /// <param name="seekFloor">
    /// The position a preceding seek committed to, or <see cref="TimeSpan.Zero"/> for an
    /// ordinary launch. Frames below it are refused until one reaches it: the demuxer
    /// restarts at the keyframe before the target while the clock is seated on the target
    /// exactly, so without this the whole GOP arrives already due and presents at decode
    /// rate (#157).
    /// </param>
    /// <remarks>
    /// The floor is armed here rather than at the flush because this is the point the
    /// discontinuity is committed — a run is about to start producing frames against the
    /// rebased clock. Arming at the flush would leave it set on a seek that was cancelled
    /// or failed between the two, where no run at that target ever begins.
    /// </remarks>
    public void BeginRun(TimeSpan seekFloor = default, bool holdForSettle = false)
    {
        lock (_gate)
        {
            _runId++;
            _settleHeld = false;
            _settled.Set();
            _buffer.SetFloor(seekFloor);
            _droppedThisSeek = 0;
            // Only a run whose caller is going to reseat the clocks may hold delivery for
            // it. A run that arms a floor without settling — first play after a seek, the
            // resume after a paused seek — would otherwise close the gate when its
            // destination frame lands, long after the caller has moved on, and be recovered
            // only by the backstop.
            _holdForSettle = holdForSettle && _buffer.HasFloor;
            // Armed only when there is a floor to reach. A run with no floor leaves this
            // already completed so a caller can await it without knowing which it is.
            _seekTargetReached = _buffer.HasFloor
                ? new TaskCompletionSource<TimeSpan?>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )
                : Completed();
            _inputComplete = false;
            _lastFrameEndPts = TimeSpan.Zero;
            if (_drained.Task.IsCompleted)
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Marks the current run's input as complete: the graph has forwarded every
    /// decoded frame. The pacer keeps delivering buffered frames at clock
    /// cadence; once the buffer empties, <see cref="WaitForDrainAsync"/>
    /// completes. If the buffer is already empty, it completes immediately.
    /// </summary>
    public void SignalInputComplete()
    {
        TaskCompletionSource<TimeSpan?> reachedAt;
        lock (_gate)
        {
            reachedAt = _seekTargetReached;
            _inputComplete = true;
            // Drain only once the last presented frame has FINISHED displaying
            // (clock ≥ Pts+Duration), not the instant it was selected. If the
            // clock hasn't reached that yet, the delivery loop holds until it does.
            if (_buffer.IsEmpty && _clock.Latest >= _lastFrameEndPts)
                _drained.TrySetResult();
        }
        // No more frames are coming, so a target not reached by now never will be. Releases
        // a session waiting on it rather than leaving it to time out (#161).
        reachedAt.TrySetResult(null);

        // Wake the loop so it re-evaluates the drained condition promptly.
        _arrival.Set();
    }

    /// <summary>
    /// Completes when every frame buffered at <see cref="SignalInputComplete"/>
    /// has been delivered to the inner sink at clock cadence (i.e. the clip has
    /// finished playing), or when <paramref name="ct"/> cancels. The session
    /// awaits this before firing its end-of-stream callback so Ended is
    /// real-time-accurate rather than decode-speed.
    /// </summary>
    public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);

    /// <summary>
    /// Lets delivery resume after a post-seek reseat. Idempotent, and safe to call on a run
    /// that never held.
    /// </summary>
    /// <remarks>
    /// Scoped to the run that armed it. A settle can finish late — after its cap, or after
    /// a scheduler delay — by which time a newer run may have armed a hold of its own, and
    /// an unscoped release would open that one's gate and deliver its destination frame
    /// against the clock its own settle is still about to correct.
    /// </remarks>
    public void ReleaseSeekSettle(long runId)
    {
        lock (_gate)
        {
            if (runId != _runId)
                return;
            _settleHeld = false;
            _settled.Set();
        }
    }

    /// <summary>Identifies the current run, for pairing a settle with the hold it releases.</summary>
    public long CurrentRunId
    {
        get
        {
            lock (_gate)
                return _runId;
        }
    }

    /// <summary>
    /// Completes with the PTS of the first frame this run admitted at or past its seek
    /// floor, or <see langword="null"/> when the run has no floor, ends without reaching
    /// one, or <paramref name="cap"/> elapses first.
    /// </summary>
    /// <remarks>
    /// The session awaits this after a seek so it can reseat the clocks onto the frame that
    /// actually arrived. Between the reposition and that frame the decoder is walking from
    /// the keyframe to the target, during which nothing can be displayed — time spent there
    /// is not playback time, and letting the clocks count it leaves the destination frame
    /// already late (#161).
    ///
    /// Returning null rather than throwing on the cap is deliberate: every reason this does
    /// not complete — a target past the end of the stream, a stalled decoder — is a reason
    /// to carry on with the clocks as they are, which is the behaviour that shipped.
    /// </remarks>
    public async Task<TimeSpan?> WaitForSeekTargetAsync(
        long runId,
        TimeSpan cap,
        CancellationToken ct
    )
    {
        Task<TimeSpan?> reached;
        lock (_gate)
        {
            if (runId != _runId)
                return null;
            reached = _seekTargetReached.Task;
        }

        if (reached.IsCompleted)
            return await reached.ConfigureAwait(false);

        try
        {
            return await reached.WaitAsync(cap, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parks until the post-seek reseat has happened, or until the backstop gives up on it.
    /// </summary>
    /// <param name="runId">
    /// The run whose hold this waiter observed. The backstop is scoped to it for the same
    /// reason the explicit release is: a wait entered under one run can expire after a
    /// newer one has armed a hold of its own, and clearing that one would deliver its
    /// destination frame before its reseat.
    /// </param>
    private async Task WaitForSettleAsync(long runId, CancellationToken ct)
    {
        try
        {
            await _settled.WaitAsync(ct).WaitAsync(SettleHoldBackstop, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (runId != _runId)
                    return;
                _settleHeld = false;
                _settled.Set();
            }
        }
    }

    private async Task RunDeliveryLoopAsync(CancellationToken ct)
    {
        var dropped = new List<IVideoFrame>(_capacity);
        var waitSw = new Stopwatch();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Park until at least one frame is buffered.
                TimeSpan? earliest;
                bool inputComplete;
                TimeSpan lastFrameEnd;
                bool held;
                long heldRun;
                lock (_gate)
                {
                    // Read here rather than before the lock. Admission closes the gate
                    // inside this same critical section, so checking it out here is what
                    // makes "the frame is visible" and "delivery is held" one observation
                    // instead of two.
                    held = _settleHeld;
                    heldRun = _runId;
                    earliest = _buffer.EarliestPts;
                    inputComplete = _inputComplete;
                    lastFrameEnd = _lastFrameEndPts;
                    // Buffer empty + input complete ⇒ this run is delivered once the
                    // LAST frame has finished displaying (clock ≥ Pts+Duration), not
                    // the instant it was selected. Gating here makes Ended fire at
                    // true end-of-content rather than ~one frame early.
                    if (earliest is null && inputComplete && _clock.Latest >= lastFrameEnd)
                        _drained.TrySetResult();
                }
                if (held)
                {
                    // Held only across a post-seek reseat, and bounded so a missed release
                    // cannot freeze the picture.
                    await WaitForSettleAsync(heldRun, ct).ConfigureAwait(false);
                    continue;
                }

                if (earliest is null)
                {
                    // Decide under the lock whether the end-of-content hold is needed:
                    // input complete, not already drained, last frame still mid-display.
                    bool holdForFrameEnd;
                    TimeSpan holdTarget;
                    lock (_gate)
                    {
                        holdForFrameEnd = _inputComplete
                            && !_drained.Task.IsCompleted
                            && _clock.Latest < _lastFrameEndPts;
                        holdTarget = _lastFrameEndPts;
                    }
                    if (holdForFrameEnd)
                    {
                        // The clip is decoded out but the final frame is still within its
                        // display interval. Hold until the clock reaches its end, then
                        // drain — no more frames arrive this run, so parking on _arrival
                        // would wedge EOS forever (the single-item-loop freeze). Bounded by _maxWait
                        // and linked to the recheck token exactly like the normal wait
                        // below, so a stalled/stopped master (e.g. audio EOS before the
                        // video tail) caps out instead of hanging, and a concurrent
                        // Flush/seek breaks the hold rather than stranding the next frame.
                        CancellationToken holdRecheckToken;
                        lock (_gate)
                            holdRecheckToken = _recheck.Token;
                        bool holdRechecked = false;
                        using (var holdCts = CancellationTokenSource.CreateLinkedTokenSource(ct, holdRecheckToken))
                        {
                            holdCts.CancelAfter(_maxWait);
                            try
                            {
                                await _clock.WaitUntilAsync(holdTarget, holdCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                // Flush/seek (recheck) ⇒ re-evaluate; never EOS on a
                                // discontinuity. Cap fired (master not advancing) ⇒ end
                                // the run anyway via the drain below.
                                holdRechecked = holdRecheckToken.IsCancellationRequested;
                            }
                        }
                        if (holdRechecked)
                            continue;
                        lock (_gate)
                        {
                            // Genuine end-of-run: buffer empty + input complete, reached
                            // either by the clock hitting frame-end or by the cap (a
                            // master that stopped short). A recheck never reaches here.
                            if (_buffer.IsEmpty && _inputComplete)
                                _drained.TrySetResult();
                        }
                        continue;
                    }
                    await _arrival.WaitAsync(ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        // Reset only if still empty — a frame may have landed
                        // between Set and here; we want the next iteration to see
                        // it, not to re-park.
                        if (_buffer.IsEmpty)
                            _arrival.Reset();
                    }
                    continue;
                }

                // Wait until the earliest buffered frame is due on the master
                // clock, bounded by the cap. WaitUntilAsync is the same
                // starvation-immune pull PaceUntil used (ADR-0057 Stage 1). The
                // wait is linked to a "recheck" token so a concurrent Flush (which
                // may drain the very frame being waited on) breaks it and forces a
                // fresh "earliest" pick.
                bool capFired = false;
                bool rechecked = false;
                CancellationToken recheckToken;
                lock (_gate)
                    recheckToken = _recheck.Token;
                waitSw.Restart();
                using (var capCts = CancellationTokenSource.CreateLinkedTokenSource(ct, recheckToken))
                {
                    capCts.CancelAfter(_maxWait);
                    try
                    {
                        await _clock.WaitUntilAsync(earliest.Value, capCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        if (recheckToken.IsCancellationRequested)
                            // Flush (or other buffer change) fired — re-evaluate
                            // from the top; do NOT treat this as a cap/force-present.
                            rechecked = true;
                        else
                            // The cap fired: the clock did not reach this frame's
                            // PTS within the bound (misaligned/stalled master).
                            // Present it anyway rather than buffer forever.
                            capFired = true;
                    }
                }
                waitSw.Stop();

                if (ct.IsCancellationRequested)
                    break;
                if (rechecked)
                    continue;

                // Select-by-clock under the lock (pure core). On a cap fire,
                // advance "now" to the earliest PTS so it (the freshest due at
                // that instant) is presented and anything older is dropped.
                IVideoFrame? present;
                dropped.Clear();
                lock (_gate)
                {
                    var now = capFired ? Max(_clock.Latest, earliest.Value) : _clock.Latest;
                    present = _buffer.Select(now, dropped);
                    // Track when the frame we're about to present finishes its
                    // on-screen display (Pts + Duration). The drain gate above waits
                    // for the clock to reach this for the LAST frame, so Ended fires
                    // at true end-of-content (the final frame gets its full display).
                    if (present is not null)
                    {
                        var end = present.Pts + present.Duration;
                        if (end > _lastFrameEndPts)
                            _lastFrameEndPts = end;
                    }
                }

                // Release a ring slot for every frame we removed (late drops +
                // the presented one), and dispose the drops.
                int removed = dropped.Count + (present is not null ? 1 : 0);
                foreach (var late in dropped)
                {
                    late.Dispose();
                    Interlocked.Increment(ref _droppedLate);
                }
                if (dropped.Count > 0)
                    LogDroppedLate(_logger, dropped.Count);
                if (removed > 0)
                    _space.Release(removed);

                if (present is null)
                    continue; // nothing due (e.g. flush raced the wait) — re-evaluate.

                if (capFired)
                {
                    LogPaceCapExceeded(
                        _logger,
                        waitSw.Elapsed.TotalMilliseconds,
                        present.Pts.TotalSeconds,
                        _clock.Latest.TotalSeconds
                    );
                }

                try
                {
                    await _inner.PresentAsync(present, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _presented);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    present.Dispose();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            // Dispose anything left buffered on teardown and release its slots so
            // a blocked PresentAsync (if any) unblocks.
            List<IVideoFrame> remaining = new();
            lock (_gate)
            {
                _buffer.DrainInto(remaining);
            }
            foreach (var f in remaining)
                f.Dispose();
            if (remaining.Count > 0)
                _space.Release(remaining.Count);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent (ADR-0044). Stops the delivery loop and disposes buffered
    /// frames; the inner sink is <b>not</b> disposed — the session/consumer owns
    /// its lifecycle.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdownCts.Cancel();
        lock (_gate)
        {
            _settleHeld = false;
            _settled.Set();
        }
        lock (_gate)
            _seekTargetReached.TrySetResult(null);
        // Wake a parked loop so it observes cancellation and runs its finally.
        _arrival.Set();
        try
        {
            await _deliveryLoop.ConfigureAwait(false);
        }
        catch
        {
            // loop swallows its own cancellation; nothing else should surface.
        }
        _shutdownCts.Dispose();
        _space.Dispose();
        lock (_gate)
            _recheck.Dispose();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "ClockSelect dropped {Count} late frame(s) superseded by a fresher due frame."
    )]
    private static partial void LogDroppedLate(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "ClockSelect discarded {Count} pre-target frame(s) after a seek; first frame presented at {PtsSec:F3}s."
    )]
    private static partial void LogDroppedBeforeTarget(ILogger logger, int count, double ptsSec);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ClockSelect WAIT CAP exceeded after {WaitMs:F1}ms (pts={PtsSec:F3}s, clock={ClockSec:F3}s) — clock not advancing to PTS; presenting frame to avoid a freeze. Suspect a misaligned/stalled master clock."
    )]
    private static partial void LogPaceCapExceeded(
        ILogger logger,
        double waitMs,
        double ptsSec,
        double clockSec
    );
}
