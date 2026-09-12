// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.OpenAL;
using FrameFlow.Graph;

namespace FrameFlow.Audio.OpenAL;

/// <summary>
/// Audio sink that plays decoded PCM audio through an OpenAL device.
/// Provides the master playback clock for A/V synchronization.
/// </summary>
/// <remarks>
/// <para>
/// Audio data arrives as interleaved S16 stereo at 48 kHz from the
/// <c>AudioDecoder</c>. Small decoded blocks (~21ms each for AAC) are
/// coalesced into larger staging buffers (~100ms) before being uploaded
/// to OpenAL. This reduces buffer-transition overhead, which is the
/// primary source of crackling on high-latency audio stacks (WSL2,
/// PulseAudio).
/// </para>
/// <para>
/// <see cref="GetPlaybackTime"/> returns the cumulative playback position
/// based on processed buffer sample counts plus the current source's
/// sample offset. This drives the <c>AudioMasterSyncStrategy</c>.
/// </para>
/// </remarks>
public sealed partial class OpenAlAudioSink
    : IAudioSink,
        IVolumeControl,
        IClockSource,
        ISeekableClock
{
    /// <summary>
    /// Upper bound on a single sleep slice inside the pull-based
    /// <see cref="IClockSource.WaitUntilAsync"/> loop. Active pacing always sleeps
    /// the (sub-frame) remaining time directly; this cap only governs how promptly
    /// the loop re-checks a frozen counter (pause / pre-roll gap).
    /// </summary>
    private static readonly TimeSpan MaxClockWaitSlice = TimeSpan.FromMilliseconds(50);

    private static readonly Meter OpenAlMeter = new("FrameFlow.Audio.OpenAL", "1.0.0");
    private static readonly Counter<long> BlocksWrittenCounter = OpenAlMeter.CreateCounter<long>(
        "frameflow.openal.blocks_written",
        description: "Total audio blocks written to OpenAL."
    );
    private static readonly Counter<long> UnderrunCounter = OpenAlMeter.CreateCounter<long>(
        "frameflow.openal.underruns",
        description: "Total buffer underruns (source starved and stopped)."
    );
    private static readonly Counter<long> BackpressureCounter = OpenAlMeter.CreateCounter<long>(
        "frameflow.openal.backpressure_events",
        description: "Total times PresentAsync had to wait for a free buffer."
    );
    private static readonly Histogram<double> WriteLatencyHistogram =
        OpenAlMeter.CreateHistogram<double>(
            "frameflow.openal.write_latency_ms",
            description: "Time spent in PresentAsync including backpressure waits."
        );
    private static readonly Histogram<double> QueueDepthHistogram =
        OpenAlMeter.CreateHistogram<double>(
            "frameflow.openal.queue_depth",
            description: "Number of buffers queued on the OpenAL source at write time."
        );

    private const int BufferPoolSize = 16;
    private const int PreBufferCount = 4;

    // Target size for coalesced buffers: ~100ms at 48kHz stereo S16.
    // 48000 samples/sec × 2 channels × 2 bytes/sample × 0.1sec = 19200 bytes ≈ 4800 shorts.
    private const int CoalesceTargetSamples = 4800;

    private readonly ILogger<OpenAlAudioSink> _logger;

    // The shared AL API (sourced from the process-wide context lease) and this
    // sink's own source within that single context. Before ADR-0058 each sink
    // owned its own device + context and made it current; that clobbered the
    // process-global current context whenever a second sink activated. Now the
    // device/context lifetime lives in SharedOpenAlContext and this sink owns
    // only its source + buffer pool inside the one shared context.
    private IOpenAlApi? _al;
    private IOpenAlContextLease? _contextLease;

    // How this sink gets its device. Production passes SharedOpenAlContext.Acquire;
    // a test passes a fake so the sink's ordering against the device, and the PCM the
    // device was actually handed, are both observable on a headless runner (#138).
    private readonly Func<IOpenAlContextLease?> _leaseFactory;
    private uint _source;
    private readonly Queue<uint> _freeBuffers = new();

    // Every buffer name generated at activation, free or in flight. _freeBuffers
    // holds only the ones not currently queued on the source, so it is not the set
    // to delete at teardown: a buffer the device still had was in neither place and
    // leaked (#138). Written once per cold activation, read once at disposal.
    private readonly List<uint> _allBuffers = new();
    private int _sampleRate;
    private int _channels;
    private volatile bool _disposed;

    // The master clock as a pure value (§5.2): the source-time origin, whether it's
    // seated, the cumulative processed-sample count, and any pending seek seed — the
    // four fields that used to be _baseSourceTime / _baseSourceTimeCaptured /
    // _processedSamplesPerChannel / _pendingSeekBaseline, all collapsed into one
    // immutable AudioClockState advanced under _stateLock. Every clock decision
    // (origin policy across activate/seek/first-buffer, processed-sample accumulation,
    // and the published-position arithmetic) lives in that value; this shell only
    // reads the live AL_SAMPLE_OFFSET + _sampleRate and feeds them in. The clock used
    // to be reachable only through the live OpenAL handle here, so its arithmetic was
    // device-test-only; AudioClockState makes it unit-testable with no device while
    // this field stays the single threaded mutable holder of that value.
    private AudioClockState _clock = AudioClockState.Initial;

    // Smooths the 20 ms AL_SAMPLE_OFFSET step between device updates (#125). Guarded by
    // _stateLock, like _clock — every read goes through GetPlaybackTimeUnderLock.
    private AudioClockAnchor _clockAnchor = AudioClockAnchor.None;

    // Holds the clock to the rate a playing device can consume at, so a counter that
    // advanced because the device dropped audio cannot carry the master clock with it
    // (#127). Dropped at every discontinuity that reseats the clock, alongside
    // _clockAnchor — see InvalidateClockAnchorsUnderLock. Guarded by _stateLock.
    private AudioClockRateAnchor _rateAnchor = AudioClockRateAnchor.None;

    // Latched once the device has reported itself gone, so the error is logged once per
    // activation rather than once per flush. Read off-thread by DeviceDisconnected without
    // the sink lock, so it is an int touched through Interlocked/Volatile like the other
    // lock-free tallies rather than a bool guarded by _stateLock.
    private int _deviceDisconnected;

    // The position the clock stopped at, while it is stopped; null whenever playing.
    // A paused device's counters are not evidence of anything: the clock reads this and
    // ResumeAsync re-anchors onto it, so whatever the queue did meanwhile stays out of
    // the master clock (#127). Guarded by _stateLock.
    private TimeSpan? _pausedPosition;

    // Serialises every touch of the OpenAL source state, the buffer-pool queue,
    // the sample-counter, and the staging buffer. Three threads can race here in
    // production: (1) the audio worker calling PresentAsync, (2) the video worker
    // calling GetPlaybackTime once per decoded frame for AV sync, and (3) the
    // session lifecycle (Pause/Resume/Deactivate/Dispose) on the controller
    // thread. Without the lock, the Queue<uint> and _processedSamplesPerChannel
    // get corrupted and OpenAL receives interleaved Unqueue/QueueBuffers calls,
    // which surfaced in the inference example as audio "looping" and a position
    // counter that runs ahead of audio actually written.
    private readonly Lock _stateLock = new();

    // Staging buffer for coalescing small decoded blocks into larger OpenAL buffers.
    // The byte storage stays in the shell (it backs the BufferData upload); the valid
    // fill level + every queue-control decision over it lives in the pure value below.
    private short[] _stagingBuffer = [];

    // The buffer-queue control logic as a pure value (§5.2): the staging fill level
    // (was _stagingCount), the source-started latch (was _sourceStarted), and the four
    // decisions the flush loop turns on — coalesce gate, underrun check, upload/
    // backpressure plan, and pre-buffer playback-start gate. Advanced under _stateLock.
    // The OpenAL buffer HANDLES (_freeBuffers, _stagingBuffer bytes) stay in the shell
    // because they are device resources; the lock-free diagnostic tallies
    // (_underrunCount / _backpressureCount) stay as Interlocked/Volatile fields because
    // the public UnderrunCount / BackpressureCount properties read them off-thread
    // without _stateLock. What this value owns is the lock-protected decision state, so
    // the queue decisions become unit-testable with no device.
    private BufferQueueState _queue = BufferQueueState.Create(CoalesceTargetSamples, PreBufferCount);

    // Fires when RecycleProcessedBuffers returns a processed buffer to
    // _freeBuffers. The backpressure path in FlushStagingBufferAsync awaits this
    // instead of Thread.Sleep-spinning, so a pooled thread is freed (not blocked)
    // for the duration of a device-side stall. The signal is advisory: every wake
    // re-checks _freeBuffers under _stateLock and the wait is timeout-bounded, so a
    // missed signal can never lose a buffer or deadlock (see AsyncAutoResetEvent).
    private readonly AsyncAutoResetEvent _bufferReturned = new();

    // Upper bound on a single async backpressure wait slice. Active draining wakes
    // the waiter via _bufferReturned the instant a buffer recycles; this cap only
    // bounds how often the loop re-polls the source state (to catch a Pause/Stop
    // that arrived while parked, or a signal that raced just ahead of the wait).
    // Same role the MaxClockWaitSlice cap plays for the pull-based clock loop.
    private static readonly TimeSpan MaxBackpressureWaitSlice = TimeSpan.FromMilliseconds(50);

    // ── Clock source (IClockSource), pull-based ─────────────────────────────
    // The master clock is read on demand from the OpenAL sample counter
    // (GetPlaybackTime), never pushed by a ticker thread. WaitUntilAsync
    // recomputes the remaining wait from the live counter each slice, so a
    // descheduled thread can never strand a pacer on a stale clock (ADR-0057,
    // superseding the prior 5 ms publish ticker that starved under CPU load and
    // burst-released frames downstream). Cancelled on dispose to release any
    // in-flight pacing waits.
    private readonly CancellationTokenSource _clockShutdownCts = new();

    // Diagnostics state
    private long _blocksWritten;
    private long _underrunCount;
    private long _backpressureCount;

    // Supplies the sleep in IClockSource.WaitUntilAsync. Defaulted to the high-resolution
    // provider because the choice decides the frame rate. See the constructor remarks.
    private readonly TimeProvider _timeProvider;
    private readonly Stopwatch _sessionClock = new();

    // ── Volume / Mute (persist across Activate/Deactivate cycles) ───────────
    // Stored under _stateLock; effective gain applied via ApplyEffectiveGain
    // which is called both on Volume/Muted writes (instant) and at the end of
    // ActivateAsync (so values set before activation are honored on first frame
    // and after every loop restart).
    private float _volume = 1.0f;
    private bool _muted;

    /// <inheritdoc/>
    public float Volume
    {
        get
        {
            lock (_stateLock)
                return _volume;
        }
        set
        {
            if (float.IsNaN(value) || value < 0f)
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Volume must be a non-negative, non-NaN float."
                );
            lock (_stateLock)
            {
                _volume = value;
                ApplyEffectiveGain();
            }
        }
    }

    /// <inheritdoc/>
    public bool Muted
    {
        get
        {
            lock (_stateLock)
                return _muted;
        }
        set
        {
            lock (_stateLock)
            {
                _muted = value;
                ApplyEffectiveGain();
            }
        }
    }

    /// <summary>
    /// Pushes the effective gain (<c>_muted ? 0 : _volume</c>) to the
    /// OpenAL source. Safe to call before activation — no-ops if the
    /// device isn't open yet. Caller must hold <see cref="_stateLock"/>.
    /// </summary>
    private void ApplyEffectiveGain()
    {
        if (_al is null || _disposed)
            return; // not activated; will be re-applied on next ActivateAsync
        var effective = _muted ? 0f : _volume;
        _al.SetSourceProperty(_source, SourceFloat.Gain, effective);
    }

    /// <summary>Total audio blocks written since last <see cref="ActivateAsync"/>.</summary>
    public long BlocksWritten => Volatile.Read(ref _blocksWritten);

    /// <summary>Total buffer underruns (source starved) since last <see cref="ActivateAsync"/>.</summary>
    public long UnderrunCount => Volatile.Read(ref _underrunCount);

    /// <summary>Total backpressure events (all buffers full) since last <see cref="ActivateAsync"/>.</summary>
    public long BackpressureCount => Volatile.Read(ref _backpressureCount);

    /// <summary>
    /// Initializes a new <see cref="OpenAlAudioSink"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public OpenAlAudioSink(ILogger<OpenAlAudioSink>? logger = null)
        : this(logger, timeProvider: null) { }

    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">
    /// Supplies the sleep in <see cref="IClockSource.WaitUntilAsync"/>. Null uses
    /// <see cref="HighResolutionTimeProvider.Preferred"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why the default is not <see cref="TimeProvider.System"/>.</b> This sink masters the
    /// clock (ADR-0003), so the granularity of its sleep is the granularity of video
    /// delivery. The system provider routes to the platform timer queue, which on Windows
    /// rounds every sleep up to the ~15.625 ms tick; a 60 fps frame period is 16.67 ms, just
    /// over one quantum, so a sleep for one frame costs two. Measured: 28.9 ms against
    /// 16.4 ms through a high-resolution timer, or 34.6 fps versus 61.0 fps for the same
    /// source (#128, #152).
    /// </para>
    /// <para>
    /// Passing <see cref="TimeProvider.System"/> explicitly opts back out. Off Windows, and
    /// on Windows before 10 1803, the default already <i>is</i> the system provider.
    /// </para>
    /// </remarks>
    public OpenAlAudioSink(ILogger<OpenAlAudioSink>? logger, TimeProvider? timeProvider)
        : this(logger, timeProvider, leaseFactory: null) { }

    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">Supplies the sleep in <see cref="IClockSource.WaitUntilAsync"/>.</param>
    /// <param name="leaseFactory">
    /// Supplies the device lease at each activation. Null acquires the process-wide
    /// shared OpenAL device (ADR-0058), which is what production does. A test passes a
    /// fake device here so the sink's call ordering and the PCM the device was handed
    /// are both assertable with no sound card (#138).
    /// </param>
    internal OpenAlAudioSink(
        ILogger<OpenAlAudioSink>? logger,
        TimeProvider? timeProvider,
        Func<IOpenAlContextLease?>? leaseFactory
    )
    {
        _logger = logger ?? NullLogger<OpenAlAudioSink>.Instance;
        _timeProvider = timeProvider ?? HighResolutionTimeProvider.Preferred;
        _leaseFactory = leaseFactory ?? SharedOpenAlContext.Acquire;
    }

    /// <summary>
    /// Whether the audio endpoint this sink is playing on has gone away underneath it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Remote Desktop session disconnecting removes its Remote Audio endpoint, and an
    /// output device can be disabled or unplugged. OpenAL Soft keeps mixing over the dead
    /// device either way, marking queued buffers processed without playing them — audible
    /// as silence, and visible to this sink only as a sample counter that keeps advancing.
    /// Nothing else distinguishes it from playback, which is how it cost a session before
    /// it was named (#127).
    /// </para>
    /// <para>
    /// Latched, not momentary: it stays true for the rest of the activation once seen, so
    /// a consumer polling it cannot miss the transition between polls. The device is shared
    /// process-wide (ADR-0058), so every sink in the process reports the same endpoint.
    /// </para>
    /// <para>
    /// Reporting only. The clock is held to real time by
    /// <see cref="FrameFlow.Media.AudioClockRateLimit"/> whether or not anyone reads this,
    /// and recovering the endpoint is not attempted — that means reopening the device the
    /// whole process shares, which is a larger change than this observation.
    /// </para>
    /// </remarks>
    public bool DeviceDisconnected => Volatile.Read(ref _deviceDisconnected) != 0;

    /// <inheritdoc/>
    public unsafe ValueTask ActivateAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            if (_al is not null)
            {
                // Re-activation on loop restart: the device/context/source/buffer pool are
                // still valid, but the source may carry a stale buffer from the prior
                // iteration. Force a clean slate before the next prime — see
                // ResetSourceQueueUnderLock for what that leftover does to loop 2.
                ResetSourceQueueUnderLock();

                // The OpenAL source handle persists across re-activation, so its
                // AL_GAIN property should too — but re-asserting is defensive and
                // cheap. Also handles the case where the consumer wrote to Volume
                // between Deactivate and Activate (since the Volume setter early-
                // returns when _al is null between disposal scenarios; that
                // window doesn't actually exist here since _al stays non-null,
                // but the defensive reapply keeps the contract uniform).
                ApplyEffectiveGain();

                // Processed-sample count is zeroed by SeatBaseSourceTimeOnActivate()
                // (the pure SeatOnActivate transition resets it as part of seating the
                // origin), so it is not reset separately here.
                _sampleRate = 0;
                _channels = 0;
                _blocksWritten = 0;
                _underrunCount = 0;
                _backpressureCount = 0;
            _deviceDisconnected = 0;
                // Source-started latch + staging fill level reset together via the pure
                // value (was `_sourceStarted = false; _stagingCount = 0;`).
                _queue = _queue.ResetForActivation();
                SeatBaseSourceTimeOnActivate();
                _sessionClock.Restart();
                LogStarted(_logger, _contextLease?.DeviceName ?? "(no device)", BufferPoolSize, PreBufferCount, CoalesceTargetSamples);
                return ValueTask.CompletedTask;
            }

            // Acquire a reference on the process-wide shared device/context
            // instead of opening our own. The context is made current exactly
            // once (on the first sink in the process) and never changed, so no
            // sink can clobber another's al* target (ADR-0058). A null lease
            // means no audio device is available; stay inert, exactly as the
            // prior per-sink OpenDevice-failure path did.
            _contextLease = _leaseFactory();
            if (_contextLease is null)
            {
                LogDeviceOpenFailed(_logger);
                return ValueTask.CompletedTask;
            }

            _al = _contextLease.Al;

            _source = _al.GenSource();
            // Apply any pre-activation Volume/Muted writes; defaults to 1.0.
            ApplyEffectiveGain();

            for (int i = 0; i < BufferPoolSize; i++)
            {
                var buffer = _al.GenBuffer();
                _freeBuffers.Enqueue(buffer);
                _allBuffers.Add(buffer);
            }

            // Processed-sample count is zeroed by SeatBaseSourceTimeOnActivate() (the
            // pure SeatOnActivate transition), so it is not reset separately here.
            _sampleRate = 0;
            _channels = 0;
            _blocksWritten = 0;
            _underrunCount = 0;
            _backpressureCount = 0;
            _deviceDisconnected = 0;
            // Source-started latch + staging fill level reset together via the pure
            // value (was `_sourceStarted = false; _stagingCount = 0;`).
            _queue = _queue.ResetForActivation();
            SeatBaseSourceTimeOnActivate();
            _sessionClock.Restart();

            LogStarted(_logger, _contextLease?.DeviceName ?? "(no device)", BufferPoolSize, PreBufferCount, CoalesceTargetSamples);
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Consumes the samples synchronously into the staging buffer (and
    /// flushes to OpenAL when it crosses
    /// <see cref="CoalesceTargetSamples"/>), then disposes the buffer per
    /// the <see cref="FrameFlow.Media.IAudioSink"/> contract. The dispose just
    /// decrements the refcount — when the original packet downstream
    /// drops its reference, the pooled <c>IMemoryOwner&lt;short&gt;</c>
    /// returns to <see cref="System.Buffers.MemoryPool{T}.Shared"/>.
    /// </remarks>
    public async ValueTask PresentAsync(
        IAudioBuffer frame,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame is not PcmAudioBuffer pcm)
        {
            frame.Dispose();
            throw new NotSupportedException(
                $"OpenAlAudioSink supports only PcmAudioBuffer; received "
                    + $"{frame.GetType().FullName}."
            );
        }

        try
        {
            bool flushNeeded;
            lock (_stateLock)
            {
                if (_disposed || _al is null)
                    return;

                _sampleRate = pcm.SampleRate;
                _channels = pcm.Channels;

                // Capture the source-time baseline from the FIRST buffer
                // arriving after each activation. Lets GetPlaybackTime
                // publish source-stream PTS (baseSourceTime + samples/
                // sampleRate) so PacedUntil's frame.Pts comparisons
                // stay valid across seeks — without this, post-seek
                // clock = 0 while video frames carry seek-target PTS,
                // freezing video for `seek-target` real seconds.
                if (!_clock.OriginSeated)
                {
                    _clock = _clock.CaptureFirstBufferPts(pcm.PresentationTime);
                    InvalidateClockAnchorsUnderLock();
                    LogBaseSourceTimeCaptured(_logger, _clock.BaseSourceTime.TotalSeconds);
                }

                // Append decoded samples to the staging buffer. The shell copies the
                // bytes at the current fill offset; the pure value advances the count.
                var samples = pcm.Samples.Span;
                var stagingOffset = _queue.StagingCount;
                EnsureStagingCapacity(stagingOffset + samples.Length);
                samples.CopyTo(_stagingBuffer.AsSpan(stagingOffset));
                _queue = _queue.AppendStaging(samples.Length);

                Interlocked.Increment(ref _blocksWritten);
                BlocksWrittenCounter.Add(1);

                // Decide whether to flush, but do NOT flush under the lock: the
                // flush may need to wait (asynchronously) for OpenAL to recycle a
                // buffer, and the wait must not be performed while holding the
                // sink lock (it would block the video worker's GetPlaybackTime and
                // lifecycle calls — the very thing the lock-release dance existed
                // to avoid, now expressed as a real async wait outside the lock).
                flushNeeded = _queue.ShouldFlush;
            }

            // Flush the staging buffer to OpenAL when it's large enough. The async
            // flush re-takes _stateLock for each synchronous critical section and
            // awaits the buffer-return signal (never the lock) when backpressured,
            // freeing the pooled thread for the duration of any device-side stall.
            if (flushNeeded)
                await FlushStagingBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pcm.Dispose();
        }
    }

    /// <summary>
    /// Outcome of a single synchronous <see cref="TryFlushStagingBufferOnce"/>
    /// attempt, steering the async backpressure loop in
    /// <see cref="FlushStagingBufferAsync"/>.
    /// </summary>
    private enum FlushStep
    {
        /// <summary>Staging uploaded (or nothing to do / sink torn down). Stop looping.</summary>
        Done,

        /// <summary>No free buffer right now; the source is draining. Await a buffer return, then retry.</summary>
        NeedBuffer,

        /// <summary>Source is paused or stopped, so no buffer will return. Abandon the flush promptly.</summary>
        Abort,
    }

    /// <summary>
    /// Uploads the staging buffer to OpenAL, awaiting a recycled buffer (instead of
    /// spin-sleeping) when every pooled buffer is in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces the former <c>Thread.Sleep(1)</c> busy-wait. Each synchronous
    /// critical section (recycle, underrun check, the upload itself) runs under
    /// <see cref="_stateLock"/> inside <see cref="TryFlushStagingBufferOnce"/>;
    /// when backpressured this method releases the lock and
    /// <c>await</c>s <see cref="_bufferReturned"/>, so the pooled thread is freed
    /// for the duration of a device-side stall rather than blocked on it. The wait
    /// is bounded (<see cref="MaxBackpressureWaitSlice"/>) and linked to
    /// <see cref="_clockShutdownCts"/>, so dispose, pause/stop, cancellation, or a
    /// missed signal all unblock it promptly — a buffer can never be lost and the
    /// loop can never deadlock if the device never drains.
    /// </para>
    /// </remarks>
    private async ValueTask FlushStagingBufferAsync(CancellationToken cancellationToken)
    {
        var writeSw = Stopwatch.StartNew();

        // Link caller cancellation with the sink's shutdown token so DisposeAsync
        // breaks any in-flight backpressure wait (the clock loop links the same way).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _clockShutdownCts.Token
        );
        var token = linked.Token;

        Stopwatch? bpSw = null;
        bool firstPass = true;

        try
        {
            while (true)
            {
                FlushStep step = TryFlushStagingBufferOnce(firstPass, writeSw);
                firstPass = false;

                if (step is FlushStep.Done or FlushStep.Abort)
                    return;

                // step == NeedBuffer: every pooled buffer is queued and the source
                // is still draining. Account for the backpressure once, then await a
                // buffer return outside the lock instead of spinning.
                if (bpSw is null)
                {
                    Interlocked.Increment(ref _backpressureCount);
                    BackpressureCounter.Add(1);
                    bpSw = Stopwatch.StartNew();
                }

                if (_disposed)
                    return;

                try
                {
                    // Returns when a buffer recycles (RecycleProcessedBuffers ->
                    // _bufferReturned.Set) or when the slice elapses; either way we
                    // loop and re-check the real queue under the lock. The timeout
                    // also re-polls source state to catch a Pause/Stop that landed
                    // while we were parked.
                    await _bufferReturned
                        .WaitAsync(MaxBackpressureWaitSlice, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Dispose or caller cancellation — abandon the flush; the
                    // staging samples are dropped with the sink teardown.
                    return;
                }
            }
        }
        finally
        {
            if (bpSw is not null)
            {
                bpSw.Stop();
                LogBackpressure(_logger, bpSw.Elapsed.TotalMilliseconds, _backpressureCount);
            }
        }
    }

    /// <summary>
    /// One synchronous attempt to upload the staging buffer, taken under
    /// <see cref="_stateLock"/>. Recycles processed buffers, checks for underrun
    /// (first pass only), and either uploads (if a free buffer exists) or reports
    /// that the caller should await a buffer return.
    /// </summary>
    /// <param name="firstPass">
    /// Whether this is the first attempt of the current flush — the endpoint-liveness
    /// check runs once per flush rather than once per attempt. The underrun check is not
    /// gated on it; see <see cref="BufferQueueState.ObserveUnderrun"/> for why.
    /// </param>
    /// <param name="writeSw">
    /// Stopwatch started when the flush began, recorded into
    /// <see cref="WriteLatencyHistogram"/> on a successful upload so the metric
    /// still includes any backpressure-wait time.
    /// </param>
    private unsafe FlushStep TryFlushStagingBufferOnce(bool firstPass, Stopwatch writeSw)
    {
        lock (_stateLock)
        {
            // Sink inert / nothing staged → Nothing. (sinkActive=false here covers the
            // _al-null / disposed cases; PlanUpload also folds the staging-empty test,
            // but we test the inert case first to skip RecycleProcessedBuffers on a
            // torn-down sink, exactly as the old `_al is null` guard did.)
            if (_al is null || _disposed || _queue.StagingCount == 0)
                return FlushStep.Done;

            // Underrun check, before anything reads BuffersProcessed and on every pass,
            // not just the first. The device SourceState read stays gated behind
            // SourceStarted, so a priming sink never pays for it; the pure value renders
            // the Stopped → underrun verdict + latch.
            //
            // Every pass, because a source can drain while this flush is parked in the
            // backpressure wait. A retry that did not look would queue a buffer onto a
            // source nobody had noticed was stopped — audio that never plays, credited to
            // the clock by the next flush's recycle and then dropped by the re-prime.
            // Before the recycle, because a processed count taken from a source whose stop
            // has not been established yet cannot be read as a record of what played.
            if (_queue.SourceStarted)
            {
                var underrun = _queue.ObserveUnderrun(ReadSourceStateUnderLock());
                _queue = underrun.Next;
                if (underrun.Underran)
                {
                    Interlocked.Increment(ref _underrunCount);
                    UnderrunCounter.Add(1);
                    _al.GetSourceProperty(
                        _source,
                        GetSourceInteger.BuffersQueued,
                        out int queuedAtUnderrun
                    );
                    LogUnderrun(_logger, _underrunCount, _freeBuffers.Count, queuedAtUnderrun);

                    // Credit what the device genuinely played, while that is still what
                    // the processed count means. The check above runs on every pass, so
                    // nothing has been queued since the source stopped: every processed
                    // buffer here really was played. Forced because the latch just cleared
                    // and the priming guard would otherwise skip it.
                    RecycleProcessedBuffers(force: true);

                    // Then put the starved source back in the state ActivateAsync hands
                    // the pre-buffer gate: queue empty, cursor rewound. Without this the
                    // re-prime queues onto a stopped source whose play cursor sits at the
                    // end of the old queue, which is the degenerate state #133 and the
                    // loop-restart comment in ActivateAsync both describe.
                    ResetSourceQueueUnderLock();
                }
            }

            RecycleProcessedBuffers();

            // Endpoint liveness, once per flush. It is the other way the device stops
            // being a place audio goes, and it is deliberately not folded into the
            // underrun check above: the two do not co-occur, because a dead endpoint keeps
            // reporting buffers processed, so the source never starves and no underrun is
            // ever observed (#127).
            if (firstPass)
                ObserveEndpointLivenessUnderLock();

            // Upload / backpressure plan. The source-state read only happens when the
            // pool is empty (the lazy ReadSourceStateUnderLock keeps the upload hot path
            // free of the extra device call, matching the old branch order).
            int freeCount = _freeBuffers.Count;
            var decision = _queue.PlanUpload(
                sinkActive: true,
                freeBufferCount: freeCount,
                sourceState: freeCount == 0 ? ReadSourceStateUnderLock() : AlSourceState.PlayingOrInitial
            );

            if (decision is UploadDecision.Abort)
                return FlushStep.Abort;
            if (decision is UploadDecision.NeedBuffer)
                return FlushStep.NeedBuffer;
            // decision == Upload (Nothing is impossible here: staging non-empty + active).

            var buffer = _freeBuffers.Dequeue();

            var format = _channels switch
            {
                1 => BufferFormat.Mono16,
                2 => BufferFormat.Stereo16,
                _ => BufferFormat.Stereo16,
            };

            fixed (short* dataPtr = _stagingBuffer)
            {
                _al.BufferData(buffer, format, dataPtr, _queue.StagingCount * sizeof(short), _sampleRate);
            }

            _queue = _queue.ClearStaging();

            var bufId = buffer;
            _al.SourceQueueBuffers(_source, 1, &bufId);

            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            QueueDepthHistogram.Record(queued);

            // Start playback after pre-buffering — the pure pre-buffer gate latches
            // SourceStarted and tells us whether to actually call SourcePlay.
            var start = _queue.ObserveQueueDepth(queued);
            _queue = start.Next;
            if (start.ShouldStartPlayback)
            {
                _al.SourcePlay(_source);
                if (_blocksWritten > PreBufferCount * 5) // only log restart, not initial start
                    LogSourceRestarted(_logger, queued);
            }

            writeSw.Stop();
            WriteLatencyHistogram.Record(writeSw.Elapsed.TotalMilliseconds);

            if (_blocksWritten % 500 == 0)
            {
                var playbackPos = GetPlaybackTimeUnderLock();
                int blockSamplesPerChannel = _channels > 0 ? CoalesceTargetSamples / _channels : 0;
                double blockDurationMs =
                    _sampleRate > 0 ? (double)blockSamplesPerChannel / _sampleRate * 1000 : 0;
                LogPeriodicStatus(
                    _logger,
                    _blocksWritten,
                    playbackPos.TotalSeconds,
                    _underrunCount,
                    _backpressureCount,
                    queued,
                    _freeBuffers.Count,
                    blockDurationMs
                );
            }

            return FlushStep.Done;
        }
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_al is not null && !_disposed)
            {
                _al.SourcePause(_source);
                _sessionClock.Stop();
                // Read the device once more, while it still means something, and latch it.
                // Every later read returns the latch: a paused source that drains its queue
                // reports the whole queue processed, and crediting that put the clock a
                // buffer-pool ahead of the picture for the rest of the session (#127).
                var pausedAt = GetPlaybackTimeUnderLock();
                _pausedPosition = pausedAt;
                LogPaused(_logger, pausedAt.TotalSeconds);
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_al is null || _disposed)
                return ValueTask.CompletedTask;

            // Drop the anchors before interpolation and the rate limit come back on. The
            // last read of the paused session anchored at the pause instant, and the clock
            // has been frozen since — so without this the first read after resume measures
            // its gap from that old timestamp and immediately leads the device by the full
            // cap, instead of starting where the device actually is. Both branches below
            // resume, so this sits above them.
            InvalidateClockAnchorsUnderLock();

            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if ((SourceState)state == SourceState.Paused)
            {
                // Source still has buffers queued — resume playback normally.
                _al.SourcePlay(_source);
                _sessionClock.Start();
                LogResumed(_logger, RebaseOnResumeUnderLock().TotalSeconds);
            }
            else
            {
                // Source drained (underrun) while we were paused — it is AL_STOPPED.
                // Rebase first, while the latch still says the source was playing: the
                // rebase returns that drained queue to the pool, and the priming guard in
                // RecycleProcessedBuffers would skip it if the latch were already clear.
                // Then clear the latch so FlushStagingBuffer re-arms the pre-buffer gate
                // and will call SourcePlay again once enough data is queued.
                _sessionClock.Start();
                LogResumed(_logger, RebaseOnResumeUnderLock().TotalSeconds);
                _queue = _queue.MarkSourceStopped();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Seats the clock's origin so it continues from the latched pause position, then
    /// clears the latch and returns that position. A no-op read when there was no latch.
    /// </summary>
    /// <remarks>
    /// Recycles first so the origin accounts for every buffer the device let go of during
    /// the pause, legitimately or not — those buffers are returned to the pool either way,
    /// and the rebase is what keeps their samples out of the published position. The
    /// assertion this exists to make true is the one in #127: a resume must report the
    /// position the pause reported, not that position plus the queue.
    /// </remarks>
    private TimeSpan RebaseOnResumeUnderLock()
    {
        if (_pausedPosition is not { } resumeAt)
            return GetPlaybackTimeUnderLock();

        RecycleProcessedBuffers(force: true);
        _al!.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int sampleOffset);
        _clock = _clock.RebaseOnResume(resumeAt, sampleOffset, _sampleRate);
        _pausedPosition = null;
        InvalidateClockAnchorsUnderLock();
        return resumeAt;
    }

    /// <inheritdoc/>
    public unsafe ValueTask DeactivateAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_al is null || _disposed)
                return ValueTask.CompletedTask;

            // Stop the source — this transitions AL_PAUSED or AL_PLAYING → AL_STOPPED.
            // After SourceStop the driver marks all queued buffers as processed so
            // SourceUnqueueBuffers can return them to the pool.
            _al.SourceStop(_source);
            _queue = _queue.MarkSourceStopped();

            // Drain the source's queue back to _freeBuffers. The next iteration's
            // ActivateAsync re-asserts SourceStop + drains residual + SourceRewind
            // as a belt-and-suspenders measure; this is the suspenders.
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int stillQueued);
            if (stillQueued > 0)
            {
                var stragglers = new uint[stillQueued];
                fixed (uint* ptr = stragglers)
                    _al.SourceUnqueueBuffers(_source, stillQueued, ptr);
                foreach (var buf in stragglers)
                    _freeBuffers.Enqueue(buf);
            }

            // Drop any residual staging samples — this is post-EOF noise that has
            // nowhere good to go. Queueing it onto the now-stopped source (the
            // pre-fix behaviour) wedged the source for the next iteration: the
            // leftover buffer would sit at the queue head when re-activation
            // started feeding new data, and OpenAL Soft marked subsequent
            // queues "processed" without playing them — silent loops 2+.
            _queue = _queue.ClearStaging();

            RecycleProcessedBuffers(force: true);

            _sessionClock.Stop();
            LogStopped(
                _logger,
                _blocksWritten,
                _underrunCount,
                _backpressureCount,
                _sessionClock.Elapsed.TotalSeconds
            );

            _sampleRate = 0;
            _channels = 0;
            // Staging fill level back to zero (defensive re-clear; already dropped above
            // before RecycleProcessedBuffers). The source-started latch was cleared at
            // SourceStop; staging is the only queue-state left to zero here.
            _queue = _queue.ClearStaging();
            // Return the clock to its initial state: origin zero, unseated, processed
            // count zero, and — critically — drop any seek seed that was never consumed
            // by an activation so it can't leak into an unrelated later (re)activation.
            // OnDeactivate() folds all four of the old resets (_processedSamplesPerChannel
            // / _baseSourceTime / _baseSourceTimeCaptured / _pendingSeekBaseline) into one.
            _clock = _clock.OnDeactivate();
            _pausedPosition = null;
            InvalidateClockAnchorsUnderLock();

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Drops both clock anchors. Every discontinuity that reseats the origin has to,
    /// because each anchor measures an advance from a reading taken against the old
    /// timeline: the interpolator would extrapolate from a stale timestamp, and the rate
    /// limit would read the reseat itself as a device outrunning real time — which is the
    /// failure that got the first version of that guard deleted (#127). Must hold
    /// <see cref="_stateLock"/>.
    /// </summary>
    private void InvalidateClockAnchorsUnderLock()
    {
        _clockAnchor = AudioClockAnchor.None;
        _rateAnchor = AudioClockRateAnchor.None;
    }

    // Establishes the source-time origin during activation. Must be called under
    // _stateLock so the published clock (base + samples/rate) reads exactly the chosen
    // origin at the activation instant. Delegates the origin policy — honour a pending
    // seek seed (origin = seek target) over the default first-buffer discovery
    // (origin = zero, unseated) — to the pure value, which also zeroes the processed
    // count as part of the transition (so callers no longer pre-zero it). The log line
    // stays in the shell since the value has no logger.
    private void SeatBaseSourceTimeOnActivate()
    {
        var pendingSeed = _clock.PendingSeekBaseline;
        _pausedPosition = null;
        _clock = _clock.SeatOnActivate();
        InvalidateClockAnchorsUnderLock();
        if (pendingSeed is { } seekBaseline)
            LogBaseSeatedToSeekTarget(_logger, seekBaseline.TotalSeconds);
    }

    /// <inheritdoc cref="ISeekableClock.SeekBaseline"/>
    /// <remarks>
    /// Seeds the source-time origin to the seek target. The seek path
    /// (<c>SubstrateSession.SeekAsync</c>) deactivates then reactivates this sink,
    /// so the seed is stored and applied by the next <see cref="ActivateAsync"/>;
    /// the immediate seat below also covers a seek that does not recycle the sink.
    /// Bypasses the first-post-seek-buffer PTS capture in <see cref="PresentAsync"/>,
    /// whose value could be a stale or keyframe-rounded buffer that anchors the
    /// clock off the seek target and hangs the video pacer.
    /// </remarks>
    public void SeekBaseline(TimeSpan position)
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;
            // The pure reseat sets the origin to the seek target, marks it seated,
            // retains the seed for the next ActivateAsync (the deactivate/reactivate
            // seek path), and zeroes the processed count — the four assignments this
            // used to do inline, now one total transition.
            _clock = _clock.SeekBaseline(position);
            // A seek moves the clock discontinuously; smoothing across it would drag the old
            // position into the new timeline.
            InvalidateClockAnchorsUnderLock();
        }
    }

    /// <summary>
    /// Returns the current playback position based on processed buffers
    /// plus the current source sample offset. Synonym for
    /// <see cref="IClockSource.Latest"/> kept on the type's public surface for direct
    /// callers (diagnostics tooling, examples) that don't already hold an
    /// <see cref="IClockSource"/> reference.
    /// </summary>
    /// <remarks>
    /// This is called once per decoded video frame from the playback
    /// controller's video worker, while the audio worker is concurrently
    /// inside <see cref="PresentAsync"/>. The lock keeps the
    /// <see cref="_freeBuffers"/> queue and the sample counter consistent
    /// across both callers.
    /// </remarks>
    public TimeSpan GetPlaybackTime()
    {
        lock (_stateLock)
        {
            return GetPlaybackTimeUnderLock();
        }
    }

    // ── IClockSource ────────────────────────────────────────────────────────

    /// <inheritdoc cref="IClockSource.Latest"/>
    /// <remarks>
    /// Pull-based: computed on demand from the live OpenAL sample counter via
    /// <see cref="GetPlaybackTime"/>, so the value is never stale — it does not
    /// depend on a ticker thread being scheduled (ADR-0057).
    /// </remarks>
    TimeSpan IClockSource.Latest => GetPlaybackTime();

    /// <inheritdoc cref="IClockSource.WaitUntilAsync"/>
    /// <remarks>
    /// Pull-based pacing: each slice recomputes the remaining wait from the live
    /// sample counter, so a descheduled thread can never leave the caller pacing
    /// against a stale clock (the failure mode the old 5 ms publish ticker had under
    /// CPU contention — ADR-0057). The fast path returns synchronously when the
    /// target is already due, keeping the per-frame hot path allocation-free.
    /// </remarks>
    ValueTask IClockSource.WaitUntilAsync(TimeSpan target, CancellationToken cancellationToken)
    {
        // Fast path: already due — no await, no allocation.
        if (GetPlaybackTime() >= target)
            return ValueTask.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        return new ValueTask(WaitUntilCoreAsync(target, cancellationToken));
    }

    private async Task WaitUntilCoreAsync(TimeSpan target, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _clockShutdownCts.Token
        );
        var token = linked.Token;
        while (true)
        {
            var remaining = target - GetPlaybackTime();
            if (remaining <= TimeSpan.Zero)
                return;
            // Active pacing sleeps the sub-frame `remaining` directly; the cap only
            // bounds re-check latency when the counter is frozen (pause / pre-roll).
            var slice = remaining < MaxClockWaitSlice ? remaining : MaxClockWaitSlice;
            await Task.Delay(slice, _timeProvider, token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ADR-0034: the snapshot is read inside <see cref="_stateLock"/> so the
    /// presentation time and the cumulative sample counter are coherent with
    /// each other. This is the API shape that makes the consumer side of the
    /// audio-PTS race impossible — there is no raw field access for callers
    /// to reach for.
    /// <para>
    /// Field reads happen inside the lock; the record allocation happens
    /// outside. This is the canonical "lock protects the read, not the
    /// allocation" discipline — the heap allocation can take an arbitrary
    /// amount of time under GC pressure, and we don't want to hold the
    /// master-clock lock through it. With one writer (audio callback) and
    /// two readers (video worker calling <see cref="GetPlaybackTime"/>,
    /// UI/diagnostics callers) the difference is invisible in steady state,
    /// but the discipline matters as a template for the rest of the
    /// diagnostics surfaces.
    /// </para>
    /// </remarks>
    public AudioSinkDiagnosticsSnapshot GetDiagnostics()
    {
        TimeSpan presentationTime;
        long processedSamplesPerChannel;
        int sampleRate;
        int channels;
        long blocksWritten;
        long underrunCount;
        long backpressureCount;
        bool isActive;

        lock (_stateLock)
        {
            // GetPlaybackTimeUnderLock recycles first, so _clock's processed count is
            // up to date when read here — same ordering as the old inline field.
            presentationTime = GetPlaybackTimeUnderLock();
            processedSamplesPerChannel = _clock.ProcessedSamplesPerChannel;
            sampleRate = _sampleRate;
            channels = _channels;
            blocksWritten = _blocksWritten;
            underrunCount = _underrunCount;
            backpressureCount = _backpressureCount;
            isActive = _queue.SourceStarted && !_disposed;
        }

        // Allocate the record outside the lock — GC pauses are bounded by
        // record allocation cost (one short-lived gen-0 object) but the
        // master-clock lock should never gate on the allocator.
        return new AudioSinkDiagnosticsSnapshot(
            PresentationTime: presentationTime,
            ProcessedSamplesPerChannel: processedSamplesPerChannel,
            SampleRate: sampleRate,
            Channels: channels,
            BlocksWritten: blocksWritten,
            UnderrunCount: underrunCount,
            BackpressureEvents: backpressureCount,
            IsActive: isActive
        );
    }

    /// <summary>
    /// Internal helper for callers that already hold <see cref="_stateLock"/>
    /// (e.g. <see cref="PauseAsync"/> / <see cref="ResumeAsync"/> when they
    /// log the post-transition position).
    /// </summary>
    private TimeSpan GetPlaybackTimeUnderLock()
    {
        if (_al is null || _disposed || _sampleRate <= 0)
            return _clock.BaseSourceTime;

        // Paused: the latch is the answer, and the device is not consulted at all. Reading
        // it would recycle whatever the queue did while stopped straight into the clock,
        // which is how a pause came to end 1.09 s further on than it started (#127).
        if (_pausedPosition is { } paused)
            return paused;

        // Recycle first so _clock.ProcessedSamplesPerChannel reflects every buffer
        // OpenAL has finished, then read the live in-flight cursor and let the pure
        // value do the arithmetic. The shell owns the two device numbers (the
        // AL_SAMPLE_OFFSET read and _sampleRate); AudioClockState.Position reproduces
        // the exact `base + (processed + offset)/rate` the inline math used to do.
        RecycleProcessedBuffers();

        _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int sampleOffset);

        var raw = _clock.Position(sampleOffset, _sampleRate);

        // A playing device consumes one second per second. A reading that outruns elapsed
        // playing time is audio it dropped rather than played, so publish what real time
        // allows instead of following it (#127). Applied to the device's own claim, before
        // interpolation fills the gap between claims.
        (_rateAnchor, raw) = AudioClockRateLimit.Apply(
            _rateAnchor,
            raw,
            _sessionClock.Elapsed,
            AudioClockRateLimit.DefaultSlack
        );

        // The device value only moves once per mixing period — 20.00 ms on the measured
        // device — so consumers pacing against it move in 20 ms steps and a 60 fps source
        // releases at 50 (#125). Fill the gap between updates with elapsed wall time,
        // re-anchored on every real change.
        var (anchor, position) = AudioClockInterpolation.Read(
            _clockAnchor,
            raw,
            Stopwatch.GetTimestamp(),
            Stopwatch.Frequency,
            AudioClockInterpolation.DefaultMaxExtrapolation,
            // Interpolate only while audio is genuinely advancing. Both signals already exist
            // and are already maintained at the real transitions, so this needs no flag of its
            // own to keep in sync: _sessionClock stops on pause and deactivate and restarts on
            // either resume branch, and _queue.SourceStarted is latched by the pre-buffer gate
            // at the actual SourcePlay and cleared by the underrun observer. A parallel bool
            // duplicated that lifecycle and was already missing transitions.
            interpolate: _sessionClock.IsRunning && _queue.SourceStarted
        );
        _clockAnchor = anchor;
        return position;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent per the ADR-0044 sink-disposal contract.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        // Release any in-flight pacing waits (the pull-based WaitUntilAsync loop
        // links its sleeps to this token), then tear down the OpenAL resources.
        _clockShutdownCts.Cancel();
        _clockShutdownCts.Dispose();

        DisposeOpenAlResources();
        return ValueTask.CompletedTask;
    }

    private unsafe void DisposeOpenAlResources()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_al is not null)
            {
                _al.SourceStop(_source);

                // Delete the source before its buffers. Deleting a source releases
                // whatever is still attached to it, and that is the only way to get
                // the queue off a source that never started: alSourceStop on an
                // AL_INITIAL source is a legal NOP, an AL_INITIAL source reports no
                // buffers processed, and unqueueing from it therefore fails
                // (OpenAL 1.1 §4.3.6, §4.3.2, §4.3.5). Draining first only works for
                // a source that actually played, which is not a safe assumption at
                // teardown — a sink disposed before it reached PreBufferCount has a
                // queue it never started (#138).
                _al.DeleteSource(_source);

                // Delete every name generated, not just the free ones. A buffer the
                // device still held was not in _freeBuffers, and the unqueued array
                // the old teardown built was discarded without deleting or pooling
                // it, so each in-flight buffer leaked a native name per disposal.
                // Nothing in the sink's reporting showed it (#138).
                foreach (var buffer in _allBuffers)
                    _al.DeleteBuffer(buffer);

                _allBuffers.Clear();
                _freeBuffers.Clear();
            }

            // Release this sink's reference on the shared device/context. The
            // last sink to release tears the device + context down (ADR-0058);
            // this sink no longer owns the device, context, or AL API lifetime.
            // Source + buffers were already deleted above while the context was
            // still current and valid.
            _contextLease?.Dispose();
            _contextLease = null;
            _al = null;

            LogDisposed(_logger, _blocksWritten, _underrunCount, _backpressureCount);
        }
    }

    private void EnsureStagingCapacity(int required)
    {
        if (_stagingBuffer.Length >= required)
            return;
        var newSize = Math.Max(required, CoalesceTargetSamples * 2);
        var newBuffer = new short[newSize];
        // Preserve the bytes already staged. _queue.StagingCount is the pre-append fill
        // level here (EnsureStagingCapacity runs before AppendStaging in PresentAsync).
        var staged = _queue.StagingCount;
        if (staged > 0)
            _stagingBuffer.AsSpan(0, staged).CopyTo(newBuffer);
        _stagingBuffer = newBuffer;
    }

    /// <summary>
    /// Reads the live OpenAL source state and maps it to the OpenAL-free
    /// <see cref="AlSourceState"/> the pure <see cref="BufferQueueState"/> decisions take.
    /// Caller must hold <see cref="_stateLock"/> and have a non-null <c>_al</c>. Anything
    /// that is not <c>Paused</c> or <c>Stopped</c> (Playing, or the never-started Initial
    /// state) maps to <see cref="AlSourceState.PlayingOrInitial"/> — the same two-way split
    /// the old inline <c>(SourceState)state is … Paused or Stopped</c> tests made.
    /// </summary>
    /// <summary>
    /// Reads <c>ALC_CONNECTED</c> and latches the first time it says the endpoint is gone.
    /// Must hold <see cref="_stateLock"/>.
    /// </summary>
    /// <remarks>
    /// Logs once per activation, not once per flush: the condition does not clear on its
    /// own, and a per-flush error would bury the moment it happened under thousands of
    /// copies of itself.
    /// </remarks>
    private void ObserveEndpointLivenessUnderLock()
    {
        if (_contextLease is null || Volatile.Read(ref _deviceDisconnected) != 0)
            return;

        if (_contextLease.IsConnected)
            return;

        Volatile.Write(ref _deviceDisconnected, 1);
        LogDeviceDisconnected(_logger, _contextLease.DeviceName);
    }

    private AlSourceState ReadSourceStateUnderLock()
    {
        _al!.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        return (SourceState)state switch
        {
            SourceState.Paused => AlSourceState.Paused,
            SourceState.Stopped => AlSourceState.Stopped,
            _ => AlSourceState.PlayingOrInitial,
        };
    }

    /// <summary>
    /// Stops the source, returns its whole queue to <see cref="_freeBuffers"/>, and rewinds
    /// the play cursor — the clean slate the pre-buffer gate primes from. Must hold
    /// <see cref="_stateLock"/>, and <see cref="_al"/> must be non-null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two paths need it. Re-activation on loop restart: <see cref="DeactivateAsync"/>
    /// recycled the playback queue, but may have re-queued one buffer onto the stopped
    /// source through its trailing flush if staging held residual samples. That leftover
    /// sits at the queue head when the next iteration starts feeding, <c>SourcePlay</c>
    /// runs over it, and OpenAL Soft marks the buffers queued behind it processed without
    /// playing them — silent loop-2+ playback.
    /// </para>
    /// <para>
    /// An underrun leaves the same degenerate state by a different route: the source
    /// stopped on its own with its play cursor at the end of the queue (#133). Both want
    /// the queue empty and the cursor at zero before the next prime begins.
    /// </para>
    /// <para>
    /// The residual buffers are returned to the pool without crediting the clock. They are
    /// exactly the buffers the device did <i>not</i> play — the ones it did are credited by
    /// <see cref="RecycleProcessedBuffers"/> before either caller reaches here.
    /// </para>
    /// </remarks>
    private unsafe void ResetSourceQueueUnderLock()
    {
        _al!.SourceStop(_source);

        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int residual);
        if (residual > 0)
        {
            var bufs = new uint[residual];
            fixed (uint* ptr = bufs)
                _al.SourceUnqueueBuffers(_source, residual, ptr);
            foreach (var buf in bufs)
                _freeBuffers.Enqueue(buf);
        }

        // SourceRewind on AL_STOPPED is a no-op for state, but it resets the internal
        // sample-offset cursor — defensive against drivers that retain stale offset state
        // across Stop/Play cycles.
        _al.SourceRewind(_source);
    }

    /// <summary>
    /// Returns every buffer the device has finished with to <see cref="_freeBuffers"/> and
    /// credits its samples to the clock. A no-op while the queue is priming, unless
    /// <paramref name="force"/> says the caller is draining the source on purpose.
    /// </summary>
    /// <param name="force">
    /// Recycle even while <see cref="BufferQueueState.Priming"/> holds. Set by the two
    /// lifecycle paths that stop the source themselves and want its queue back —
    /// <see cref="RebaseOnResumeUnderLock"/> and <see cref="DeactivateAsync"/> — where the
    /// processed count really is a drained queue and not the artefact described below.
    /// </param>
    /// <remarks>
    /// The priming guard is the fix for #133. OpenAL Soft reports the entire queue of a
    /// stopped source as processed, including buffers queued after it stopped, so recycling
    /// against that count while re-priming unqueues each new buffer before the next one
    /// arrives. The depth never reaches <see cref="PreBufferCount"/>, the pre-buffer gate
    /// never fires <c>SourcePlay</c>, and the sink stays silent for the rest of its life
    /// after a single underrun.
    /// </remarks>
    private unsafe void RecycleProcessedBuffers(bool force = false)
    {
        if (_al is null)
            return;

        // Not playing: the device's processed count is not a record of what it played (#133).
        if (!force && _queue.Priming)
            return;

        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        if (processed <= 0)
            return;

        var bufs = new uint[processed];
        fixed (uint* ptr = bufs)
            _al.SourceUnqueueBuffers(_source, processed, ptr);

        foreach (var buf in bufs)
        {
            _al.GetBufferProperty(buf, GetBufferInteger.Size, out int sizeBytes);
            _al.GetBufferProperty(buf, GetBufferInteger.Channels, out int ch);
            _al.GetBufferProperty(buf, GetBufferInteger.Bits, out int bits);

            if (ch > 0 && bits > 0)
            {
                int bytesPerSample = (bits / 8) * ch;
                int totalSamples = sizeBytes / bytesPerSample;
                // Accumulate per-channel processed samples into the pure clock value
                // (was `_processedSamplesPerChannel += totalSamples`). totalSamples is
                // already per-channel: sizeBytes / ((bits/8) * ch).
                _clock = _clock.WithProcessed(totalSamples);
            }

            _freeBuffers.Enqueue(buf);
        }

        // We reached here only with processed >= 1, so at least one buffer was
        // just returned to _freeBuffers. Wake any awaiter parked in the async
        // backpressure loop (FlushStagingBufferAsync). Set() takes its own leaf
        // lock; the awaiter re-checks _freeBuffers under _stateLock after waking.
        _bufferReturned.Set();
    }

    // ── Source-generated log methods ─────────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "OpenAL audio sink started on {Device}. BufferPoolSize={BufferPoolSize}, PreBufferCount={PreBufferCount}, CoalesceTarget={CoalesceTarget} samples"
    )]
    private static partial void LogStarted(
        ILogger logger,
        string device,
        int bufferPoolSize,
        int preBufferCount,
        int coalesceTarget
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Audio endpoint {Device} reported itself disconnected (ALC_CONNECTED=0). The device is no longer playing; buffers it accepts from here are discarded, not heard. A Remote Desktop session ending, or the output device being disabled or unplugged, does this."
    )]
    private static partial void LogDeviceDisconnected(ILogger logger, string device);

    [LoggerMessage(Level = LogLevel.Error, Message = "OpenAL: failed to open audio device")]
    private static partial void LogDeviceOpenFailed(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Audio buffer UNDERRUN #{UnderrunCount}: source starved. FreeBuffers={FreeBuffers}, QueuedBuffers={QueuedBuffers}"
    )]
    private static partial void LogUnderrun(
        ILogger logger,
        long underrunCount,
        int freeBuffers,
        int queuedBuffers
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Audio backpressure: waited {WaitMs:F1}ms for free buffer. TotalBackpressure={TotalBackpressure}"
    )]
    private static partial void LogBackpressure(
        ILogger logger,
        double waitMs,
        long totalBackpressure
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Audio source restarted after underrun. QueuedBuffers={QueuedBuffers}"
    )]
    private static partial void LogSourceRestarted(ILogger logger, int queuedBuffers);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Audio status: blocks={BlocksWritten}, pos={PositionSec:F2}s, underruns={Underruns}, backpressure={Backpressure}, queued={Queued}, free={Free}, bufferMs={BlockDurationMs:F1}ms"
    )]
    private static partial void LogPeriodicStatus(
        ILogger logger,
        long blocksWritten,
        double positionSec,
        long underruns,
        long backpressure,
        int queued,
        int free,
        double blockDurationMs
    );

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audio paused at {PositionSec:F2}s")]
    private static partial void LogPaused(ILogger logger, double positionSec);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audio resumed at {PositionSec:F2}s")]
    private static partial void LogResumed(ILogger logger, double positionSec);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "BaseSourceTime DISCOVERED at {BaseSec:F3}s from the first buffer's PTS — master clock origin for initial play. (Seeks no longer rely on this: they seat the origin to the seek target via ISeekableClock.SeekBaseline.)"
    )]
    private static partial void LogBaseSourceTimeCaptured(ILogger logger, double baseSec);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "BaseSourceTime SEATED to seek target {BaseSec:F3}s — master clock origin reseated to the seek position, so post-seek frame PTS and the clock agree (no first-buffer-PTS discovery)."
    )]
    private static partial void LogBaseSeatedToSeekTarget(ILogger logger, double baseSec);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audio stopped. Blocks={BlocksWritten}, underruns={Underruns}, backpressure={Backpressure}, duration={DurationSec:F2}s"
    )]
    private static partial void LogStopped(
        ILogger logger,
        long blocksWritten,
        long underruns,
        long backpressure,
        double durationSec
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "OpenAL audio sink disposed. Lifetime blocks={BlocksWritten}, underruns={Underruns}, backpressure={Backpressure}"
    )]
    private static partial void LogDisposed(
        ILogger logger,
        long blocksWritten,
        long underruns,
        long backpressure
    );
}
