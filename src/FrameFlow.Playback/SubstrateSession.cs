// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Decoding;
using FrameFlow.Media.Diagnostics;
using FrameFlow.Playback.Core;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FrameFlow.Graph;

namespace FrameFlow.Playback;

/// <summary>
/// Substrate-backed implementation of the controller-facing session
/// contract. Owns demux + decoders + decoding pipeline + a single
/// long-lived <see cref="Graph.Graph"/> wired with
/// <see cref="PausableGate{T}"/> + <see cref="PaceUntil"/> operators
/// for pause/resume + AV pacing without touching the decoder
/// pipeline mid-decode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pause/resume via gates, not cancel-rebuild.</b> The first cut
/// of this class implemented pause by cancelling the graph CTS and
/// rebuilding on resume — that native-faulted on FFmpeg decoders
/// because the codec context ended up in an unstable state after
/// cancel-mid-decode. The current implementation keeps the graph
/// (and decoders, and demux pump) running across pause; a
/// <see cref="PausableGate{T}"/> sits between the (paced) source
/// chain and the sink, so pause closes the gate and frames pile up
/// in the bounded edge channels upstream of the gate. Resume opens
/// the gate; frames drain naturally. The decoders never see a
/// cancel.
/// </para>
/// <para>
/// <b>AV pacing.</b> A <see cref="PaceUntil"/> operator sits between
/// the video source and the video gate, throttling frames to the
/// master clock (audio sink when present + IClockSource; wallclock
/// otherwise). Without it, frames would stream at decode speed and
/// a 3s clip would "play" in &lt;100ms on a fast host.
/// </para>
/// <para>
/// <b>Seek still rebuilds.</b> <see cref="SeekAsync"/> currently
/// stops the demux pump, drains in-flight packets in the decoder
/// queues, flushes the codec contexts, seeks the demuxer, and starts
/// a fresh demux pump — but the graph itself stays alive throughout.
/// This is the same gate-protected shape as pause/resume but with
/// extra demux + codec work in the middle. Documented in docs/DEFERRED_WORK.md as
/// "stable for the common pause/resume case; seek still has known
/// rough edges around frames-pre-seek leaking through."
/// </para>
/// </remarks>
internal sealed class SubstrateSession : IPlaylistItemRuntime
{
    // Upper bound on a single video frame's pacing wait (PaceUntil defense-in-depth).
    // Orders of magnitude above any legitimate per-frame wait (~one frame interval) or
    // brief clock catch-up, so it only trips on a genuinely misaligned/stalled master
    // clock — turning what was a permanent presenter freeze into choppy-but-alive.
    private static readonly TimeSpan PaceWaitCap = TimeSpan.FromSeconds(5);

    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly IPlaybackClock _clock;

    // The master pacing clock (IClockSource consumed by PaceUntil). Selected
    // per item in InitializeAsync once the stream probe reveals whether THIS
    // item actually has a decodable audio stream the sink will play — not in
    // the constructor, where "an IClockSource audio sink is attached" is the
    // wrong question (a video-only item paced against an unactivated audio
    // sink's frozen sample counter would stall). _ownedClockSource is non-null
    // only when this session owns a WallClockSource and must dispose it.
    private IClockSource _clockSource;
    private WallClockSource? _ownedClockSource;
    private readonly SessionCallbacks _callbacks;
    private readonly HardwareDecodeMode _hwMode;
    private readonly Func<
        GraphChain<IVideoFrame>,
        GraphChain<IVideoFrame>
    >? _videoConfigurator;
    private readonly Func<
        GraphChain<PcmAudioBuffer>,
        GraphChain<PcmAudioBuffer>
    >? _audioConfigurator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    // ── Initialize-time runtime resources ──────────────────────────
    private IDemuxSession? _demux;
    private VideoDecoder? _videoDecoder;
    private AudioDecoder? _audioDecoder;
    private DecodingPipeline? _pipeline;
    private IMediaSource? _source;

    // ADR-0056: the set of pre-seek-stateful participants, reset uniformly on the seek
    // path so the orchestrator can't apply a partial invalidation checklist. Built once
    // in InitializeAsync alongside the components it covers.
    private IReadOnlyList<ISeekResettable> _seekResettables = [];

    // Seek target awaiting the launch that will play from it, or Zero when none is pending.
    // Set once a reposition commits (RepositionAsync step 6) and consumed by the next
    // LaunchSessionTasks, which is not necessarily the same call: a seek taken while paused
    // relaunches on the following PlayAsync.
    //
    // It cannot outlive the seek that recorded it. Every path that moves this session's
    // timeline either supersedes it (a further RepositionAsync) or re-derives it from the
    // origin the run starts at (PlayAsync's first-play branch), and consumption clears it.
    // Across items there is nothing to leak: PlaylistSession builds a fresh SubstrateSession
    // per item and disposes the previous one.
    private TimeSpan _pendingSeekFloor = TimeSpan.Zero;

    // The end of the item this runtime plays, or null when it plays to the end of the source. Set
    // at open, and by a rewind that hands the runtime to another item of the same source.
    private TimeSpan? _itemEnd;

    // Whether this runtime may act on what it shares with the item that is playing: the position
    // clock, the audio sink, and the audio sink's clock. A runtime prepared ahead is opened
    // detached while another item plays, positions and warms up without touching any of them,
    // and is attached when it becomes current (media-clips decision 8).
    private bool _attached = true;

    // Where a detached runtime was positioned, for Attach to seat the shared clocks on.
    private TimeSpan _detachedOrigin;

    // The end the current run stops at, in ticks, or long.MaxValue when it has none. Written only
    // before a run launches, and read by the end filter on the graph's sources as each frame or
    // buffer is decoded (media-clips decision 6).
    private long _runEndTicks = long.MaxValue;

    // Where the current run starts, for each of the graph's sources. The floor filter on each
    // releases what is presented before it as soon as it is decoded. Armed before a run launches.
    private readonly RunFloor _videoFloor = new();
    private readonly RunFloor _audioFloor = new();

    // Whether the launch that consumes _pendingSeekFloor is one this session will follow
    // with a clock reseat. Only the reposition's play branch does; the launches that run on
    // the controller's command loop cannot wait for the destination frame, so their runs
    // must not hold delivery for a reseat that never comes.
    private bool _pendingSeekSettles;

    // Set during InitializeAsync once we know whether the source has a
    // decodable audio stream. Gates every _audioSink lifecycle call so a
    // video-only file doesn't spuriously activate (and later deactivate)
    // an audio sink that has nothing to play — a real audio backend that
    // does device I/O on activate (e.g. opening an OpenAL device handle)
    // could fault for video-only content otherwise.
    private bool _hasAudio;

    // ── Gates inserted into the graph for pause control. ──────────
    // Gates start CLOSED so frames don't drain before PlayAsync. The
    // first PlayAsync opens them; PauseAsync closes them; subsequent
    // PlayAsync re-opens them.
    private readonly PausableGate<IVideoFrame> _videoGate = new(initiallyOpen: false);
    private readonly PausableGate<PcmAudioBuffer> _audioGate = new(initiallyOpen: false);

    // ── Presenter-side select-by-clock pacing (ADR-0057 Stage 2). ──
    // Replaces the in-graph PaceUntil operator. Built once in
    // InitializeAsync (when there's a video sink) around _videoSink, bound to
    // the per-item master clock. The graph's video sink node targets THIS, not
    // the raw sink: its PresentAsync enqueues and returns at once, so the graph
    // never holds a decode lease across a clock wait (the held-lease →
    // hwframe-pool-exhaustion coupling the perf survey confirmed is gone). The
    // decorator wraps but does not own _videoSink. Flushed on seek; disposed in
    // DisposeAsync.
    private ClockSelectVideoSink? _videoPacer;

    // ── Long-lived per-session runtime ────────────────────────────
    // The graph + demux pump are constructed in InitializeAsync and
    // run until DisposeAsync or natural EOS. No per-pause cancel.
    private CancellationTokenSource? _sessionCts;

    // The demux pump, graph and lateness-recovery tasks of the current run, held until they have
    // ended: a stop whose wait is cut short leaves them here for the next stop, the dispose's above
    // all, to wait for (#707).
    private RunTasks<Task> _runTasks = RunTasks<Task>.None;

    /// <summary>
    /// Lateness-recovery policy. Off unless a caller opts in — the ADR gates
    /// shipping this on the selection rule being measured, so the default has to
    /// leave every existing pipeline byte-identical.
    /// </summary>
    public LatenessRecoveryOptions? LatenessRecovery { get; init; }

    /// <summary>
    /// Shared across every item played through the same sink, so an unchanged video format is
    /// announced once for the queue rather than once per item (#287). Null gives this session
    /// its own, which announces correctly and cannot deduplicate across a boundary it does not
    /// span.
    /// </summary>
    internal VideoFormatAnnouncer? FormatAnnouncer { get; init; }

    /// <summary>
    /// Shared across every item played through the same sink, so a run of failed calls to it goes
    /// on from one item to the next, and an item shorter than the run that escalates still counts
    /// toward it (#703). Null gives this session's pacer a run of its own.
    /// </summary>
    internal VideoSinkFailures? SinkFailures { get; init; }

    /// <summary>
    /// The hardware devices the items of one player share, which this session's decoder borrows
    /// from when no device is lent. Null when there is none.
    /// </summary>
    internal HardwareDeviceCache? DeviceCache { get; init; }

    /// <summary>
    /// Opens the runtime detached: prepared ahead while another item plays, it leaves the shared
    /// position clock, audio sink and audio clock alone until <see cref="Attach"/>.
    /// </summary>
    internal bool Detached
    {
        init => _attached = !value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Seats the shared position clock and the master clock's baseline on where the runtime was
    /// positioned, as a positioning while attached would have. The caller has stopped the
    /// position clock and disposed the item that was playing, so nothing else is using them.
    /// </remarks>
    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _clock.Seek(_detachedOrigin);
        (_clockSource as ISeekableClock)?.SeekBaseline(_detachedOrigin);
    }

    private bool _renderersActivated;

    // The graph topology built by BuildGraph. A graph instance is re-runnable:
    // RunAsync re-wires fresh edge channels and re-pumps every node each call,
    // and the decoder source adapters re-create a fresh decode enumerator after
    // EOS (DecoderSourceAdapters: enumerator nulls out on EOS, ??= rebuilds it).
    // SeekAsync deliberately rebuilds a fresh graph (its restart follows a
    // CTS-cancel of in-flight work); RewindToStartAsync reuses this one to skip
    // the per-loop teardown/rebuild. Null until the first StartSessionTasks build.
    private FrameFlow.Graph.Graph? _graph;

    // ── EOF coordination ────────────────────────────────────────────
    private int _eofFired;

    // Numbers the runs of the graph; see RunNumber. Advanced by RepositionAsync once it has
    // stopped the run it interrupts, and before anything relaunches.
    private int _runNumber;
    private bool _disposed;
    // Null reaches the decoder, which resolves it to the process probe (#181).
    private readonly FrameFlow.Media.HardwareDecodeCapabilities? _hwCapabilities;
    // Null: derived from the video path (#294).
    private readonly bool? _yieldHardwareFrames;
    // Borrowed by this item's decoder; owned above the player, so it outlives the item.
    private readonly HardwareDevice? _hardwareDevice;
    // The caller's order; empty defers to the sink's, then the platform's (#532).
    private readonly IReadOnlyList<HardwareDecodeBackendKind> _preferredBackends;
    private readonly IReadOnlyList<string> _excludedCodecs;

    public SubstrateSession(
        IVideoSink? videoSink,
        IAudioSink? audioSink,
        IPlaybackClock clock,
        SessionCallbacks callbacks,
        HardwareDecodeMode hwMode = HardwareDecodeMode.Auto,
        FrameFlow.Media.HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? videoConfigurator = null,
        Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? audioConfigurator = null,
        bool? yieldHardwareFrames = null,
        HardwareDevice? hardwareDevice = null,
        IReadOnlyList<HardwareDecodeBackendKind>? preferredBackends = null,
        IReadOnlyList<string>? excludedCodecs = null
    )
    {
        ArgumentNullException.ThrowIfNull(clock);

        _videoSink = videoSink;
        _audioSink = audioSink;
        _clock = clock;
        _hwMode = hwMode;
        _hwCapabilities = hardwareDecodeCapabilities;
        _yieldHardwareFrames = yieldHardwareFrames;
        _hardwareDevice = hardwareDevice;
        _preferredBackends = preferredBackends ?? [];
        _excludedCodecs = excludedCodecs ?? [];
        _callbacks = callbacks;
        _videoConfigurator = videoConfigurator;
        _audioConfigurator = audioConfigurator;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SubstrateSession>();

        // Provisional master clock: a wallclock the session owns. InitializeAsync
        // upgrades this to the audio sink's IClockSource iff this item actually
        // has a decodable audio stream the sink will play (the ADR-0003
        // audio-master rule, evaluated per item). Allocating a WallClockSource
        // here keeps _clockSource non-null and immediately consumable; if the
        // upgrade happens, this provisional one is disposed in InitializeAsync.
        _ownedClockSource = new WallClockSource();
        _clockSource = _ownedClockSource;
    }

    // ── IPlaybackSession read-only surface ──────────────────────────

    public MediaInfo? MediaInfo => _demux?.MediaInfo;

    public TimeSpan Duration => _demux?.MediaInfo?.Duration ?? TimeSpan.Zero;

    /// <summary>
    /// The number of the graph's current run. It advances each time a seek or a rewind stops
    /// the run in progress, before the next run launches.
    /// </summary>
    /// <remarks>
    /// Read it inside <see cref="SessionCallbacks.OnEndOfStream"/>. The run raising the
    /// end-of-stream cannot have been stopped yet, because stopping a run waits for the task
    /// that raises it, so the number read there is that run's. A seek whose wait for those tasks
    /// is cancelled does not advance it, since the run has not ended (#707). A caller that handles
    /// the end-of-stream later, on another thread, compares it with this property then, and drops
    /// the end-of-stream if a seek or rewind has replaced the run in between.
    /// </remarks>
    public int RunNumber => Volatile.Read(ref _runNumber);

    /// <summary>
    /// What a session does around its run's tasks: their start and end, a stop's wait for them,
    /// and what a reposition and the dispose then do to the state they use. Internal: tests read
    /// the order the steps are taken in, and act at one to drive a race (#707).
    /// </summary>
    internal enum Step
    {
        /// <summary>A run's demux pump has started.</summary>
        PumpStarted,

        /// <summary>A run's demux pump is ending: it reads no more packets.</summary>
        PumpEnded,

        /// <summary>A run's graph has started.</summary>
        GraphStarted,

        /// <summary>A run's graph is ending: it has made its last call on a sink.</summary>
        GraphEnded,

        /// <summary>A run's lateness-recovery walk has started.</summary>
        RecoveryStarted,

        /// <summary>A run's lateness-recovery walk is ending: it sets nothing more on the decoder.</summary>
        RecoveryEnded,

        /// <summary>A stop, a reposition's or the dispose's, begins waiting for the run's tasks.</summary>
        WaitingForRun,

        /// <summary>A reposition is about to seek the demuxer.</summary>
        SeekingDemux,

        /// <summary>A reposition is about to reset the decoders and the pipeline for the seek.</summary>
        ResettingForSeek,

        /// <summary>The dispose is about to free the pump pipeline.</summary>
        DisposingPipeline,

        /// <summary>The dispose is about to free the video decoder.</summary>
        DisposingVideoDecoder,

        /// <summary>The dispose is about to free the audio decoder.</summary>
        DisposingAudioDecoder,

        /// <summary>The dispose is about to free the demux session.</summary>
        DisposingDemux,
    }

    /// <summary>
    /// Called with each <see cref="Step"/> as the session takes it, on the thread that takes it.
    /// Internal: a seam for tests, set before the session opens.
    /// </summary>
    internal Action<Step>? OnStep { get; set; }

    private void Take(Step step) => OnStep?.Invoke(step);

    /// <summary>
    /// Called with the token the end-of-stream flush of the decoders is given, before the flush,
    /// on the pump's thread. Internal: a seam for tests, set before the session opens.
    /// </summary>
    internal Action<CancellationToken>? OnFlushingDecoders { get; set; }

    private Task FinalizeDecodersAsync(CancellationToken cancellationToken)
    {
        OnFlushingDecoders?.Invoke(cancellationToken);
        return _pipeline!.FinalizeDecodersAsync(cancellationToken);
    }

    public PipelineDiagnosticsSnapshot GetPipelineDiagnostics()
    {
        var videoSink = _videoSink?.GetDiagnostics() ?? VideoSinkDiagnosticsSnapshot.Empty;
        var audioSink = _audioSink?.GetDiagnostics() ?? AudioSinkDiagnosticsSnapshot.Empty;
        var streamSnapshot = BuildStreamSnapshot();
        return new PipelineDiagnosticsSnapshot(
            Stream: streamSnapshot,
            VideoSink: videoSink,
            AudioSink: audioSink,
            // Now actually wired (ADR-0057 Stage 2): the select-by-clock pacer
            // counts frames dropped as late at delivery time. Previously always
            // zero (PaceUntil's reserved VideoFramesDroppedForSync counter was
            // never populated).
            VideoFramesDroppedForSync: _videoPacer?.DroppedLate ?? 0
        )
        {
            // Null without a pacer: there is no master clock to measure against, so
            // "no lag" would be a claim this session cannot make.
            VideoPresentationLag = _videoPacer?.PresentationLag,
        };
    }

    /// <summary>
    /// Walks <see cref="LatenessRecoveryPolicy.Path"/> from measured lateness, one settle
    /// window at a time, applying each rung to the decoder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives here rather than in the decoder because ADR-0003 puts timing policy in
    /// the playback layer, and this session already holds both halves — the pacer
    /// that measures lateness and the decoder that acts on it. Neither has to learn
    /// about the other.
    /// </para>
    /// <para>
    /// One window of lateness is compared against the window before it, so the
    /// judgement is "did this rung help", not "is the pipeline late". That is the
    /// whole selection rule, and it is the thing the ADR's third acceptance
    /// condition is about.
    /// </para>
    /// </remarks>
    private async Task RunRecoveryWalkAsync(
        LatenessRecoveryOptions options,
        CancellationToken cancellationToken
    )
    {
        var step = 0;
        TimeSpan? lagAtWindowStart = null;
        var recoveredFor = TimeSpan.Zero;
        var lastTick = Stopwatch.GetTimestamp();

        try
        {
            // Put the decoder where this walk believes it is. A previous run can have
            // left it discarding — Flush resets the readback cadence but not the
            // discard level, because how late a pipeline is does not change at a seek
            // — and a fresh walk starting at step 0 against a decoder still on
            // KeyframesOnly would believe playback was untouched while the picture
            // stayed frozen, and would never correct it: Decide only moves when
            // lateness leaves the band, and a recovered stream never does.
            ApplyStep(step);

            using var timer = new PeriodicTimer(options.SettleWindow);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Null while nothing has been presented in this run. No picture means
                // no lateness to read, and a seek is the common way to get here.
                if (_videoPacer?.PresentationLag is not { } lag)
                {
                    lagAtWindowStart = null;
                    recoveredFor = TimeSpan.Zero;
                    continue;
                }

                // Real elapsed time, not the nominal period. A PeriodicTimer tick can
                // arrive late or be coalesced, and RelaxAfter is a duration now, so
                // crediting a full window per observation would let a twelve-second
                // dwell be satisfied by rather less than twelve seconds.
                var now = Stopwatch.GetTimestamp();
                var sinceLastTick = Stopwatch.GetElapsedTime(lastTick, now);
                lastTick = now;

                recoveredFor =
                    lag <= options.RelaxBelow ? recoveredFor + sinceLastTick : TimeSpan.Zero;

                var decision = LatenessRecoveryPolicy.Decide(
                    step,
                    lag,
                    lagAtWindowStart,
                    recoveredFor,
                    options
                );

                // Whatever happens next, this reading is the baseline the rung about
                // to run gets judged against.
                lagAtWindowStart = lag;

                // Every window, not only the ones that moved. A walk is judged by the
                // readings it decided on, and a transcript of moves alone hides the
                // evidence each move was made from.
                _logger.LogDebug(
                    "Lateness recovery window: step {Step}, lag {LagMs} ms, {Move}.",
                    step,
                    lag.TotalMilliseconds,
                    decision.Move
                );

                if (decision.StepIndex == step)
                    continue;

                step = decision.StepIndex;
                var rung = LatenessRecoveryPolicy.Path[step];
                ApplyStep(step);

                _logger.LogInformation(
                    "Lateness recovery {Move} to step {Step} "
                        + "(readback 1 in {ReadbackEveryN}, discard {Discard}) at lag {LagMs} ms.",
                    decision.Move,
                    step,
                    rung.ReadbackEveryN,
                    rung.Discard,
                    lag.TotalMilliseconds
                );

                // A rung needs its own window before it can be judged. Comparing the
                // next reading against the one taken under the previous rung would
                // credit or blame the wrong one; Decide holds for one window when it
                // sees this, rather than moving again on no evidence.
                lagAtWindowStart = null;
                recoveredFor = TimeSpan.Zero;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // The walk is an optimisation. A fault in it must not take playback with
            // it, so it is reported and the pipeline carries on at whatever rung it
            // had reached.
            _callbacks.OnRecoverableError(
                new PlaybackError(
                    ErrorCategory.System,
                    $"Lateness recovery stopped after a fault: {ex.Message}",
                    ex
                )
            );
        }

        void ApplyStep(int index)
        {
            if (_videoDecoder is not { } decoder)
                return;
            var rung = LatenessRecoveryPolicy.Path[index];
            decoder.ReadbackEveryN = rung.ReadbackEveryN;
            decoder.SetDiscardLevel(rung.Discard);
        }
    }

    private FrameFlow.Decoding.Diagnostics.DecodedMediaStreamDiagnosticsSnapshot BuildStreamSnapshot()
    {
        if (_demux is null)
            return FrameFlow.Decoding.Diagnostics.DecodedMediaStreamDiagnosticsSnapshot.Empty;

        return new FrameFlow.Decoding.Diagnostics.DecodedMediaStreamDiagnosticsSnapshot(
            Demux: _demux.GetDiagnostics(),
            VideoDecoder: _videoDecoder?.GetDiagnostics()
                ?? FrameFlow.Decoding.Diagnostics.VideoDecoderDiagnosticsSnapshot.Empty,
            AudioDecoder: _audioDecoder?.GetDiagnostics()
                ?? FrameFlow.Decoding.Diagnostics.AudioDecoderDiagnosticsSnapshot.Empty,
            VideoChannelDepth: 0,
            AudioChannelDepth: 0
        );
    }

    // ── IPlaybackSession lifecycle ──────────────────────────────────

    public ValueTask InitializeAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    ) => InitializeAsync(source, TimeSpan.Zero, end: null, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask InitializeAsync(
        IMediaSource source,
        TimeSpan start,
        TimeSpan? end,
        CancellationToken cancellationToken
    )
    {
        _itemEnd = end;
        await OpenAsync(source, cancellationToken).ConfigureAwait(false);
        if (start > TimeSpan.Zero)
            await PositionAtStartAsync(start, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Positions a session that has opened but not yet warmed up at <paramref name="start"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the seek recipe, not a copy of it (ADR-0056). Before the first play a reposition
    /// holds the session: it moves the demuxer, resets the decoders, seats both clocks on the
    /// start and records the pacer's floor, and starts nothing (#639). The warm-up that follows
    /// decodes from the keyframe at or before the start, and the floor keeps every frame before
    /// the start off the sink.
    /// </para>
    /// <para>
    /// A start at or past a known end is refused. The item would open with nothing in it and end
    /// at once, and under <c>RepeatMode.One</c> it would then loop with no content. Refused here,
    /// it fails to start, and the playlist's failure guard counts it.
    /// </para>
    /// </remarks>
    private ValueTask PositionAtStartAsync(TimeSpan start, CancellationToken cancellationToken)
    {
        ThrowIfPastTheEnd(start);
        return RepositionAsync(start, GraphPolicy.Rebuild, TaskPolicy.Cancel, cancellationToken);
    }

    /// <summary>
    /// Refuses an item start at or past the source's known duration, wherever the item is
    /// positioned: at open, and in a rewind that hands the open runtime to another clip of the
    /// same source.
    /// </summary>
    private void ThrowIfPastTheEnd(TimeSpan start)
    {
        var duration = Duration;
        if (duration > TimeSpan.Zero && start >= duration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                start,
                $"'{_source?.DisplayName}' cannot start at {start}: it is {duration} long."
            );
        }
    }

    private async ValueTask OpenAsync(IMediaSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        _source = source;

        var demuxFactory = new DemuxSessionFactory(_loggerFactory);

        IDemuxSession? demux = null;
        VideoDecoder? videoDecoder = null;
        AudioDecoder? audioDecoder = null;

        try
        {
            demux = await demuxFactory.OpenAsync(source, cancellationToken).ConfigureAwait(false);

            var concreteDemux =
                demux as DemuxSession
                ?? throw new InvalidOperationException(
                    "DemuxSessionFactory returned non-DemuxSession instance; DecodingPipeline requires DemuxSession."
                );

            // ADR-0059: a decodable stream with no consumer must not be decoded.
            // The single demux pump (DecodingPipeline.RunDemuxPumpAsync) feeds every
            // decoder's bounded packet queue and blocks once any queue fills. If a
            // stream is decoded but nothing drains its queue — e.g. an audio stream
            // with no audio sink and no audio configurator/tap — that queue fills,
            // the pump blocks, and the streams that DO have consumers starve (the
            // field symptom: audio with no sink froze video ~10s in, once ~512
            // buffered audio packets filled the queue). The rule is symmetric across
            // stream types: decode a stream only when a sink or a configurator will
            // consume it; otherwise discard it at the demuxer so its packets never
            // enter the pump, and skip building its decoder.
            // A configurator no longer counts on its own: it transforms a stream on its way to
            // a sink, and the builder rejects one registered without a sink, so the sink is the
            // whole question.
            var videoHasConsumer = _videoSink is not null;
            var audioHasConsumer = _audioSink is not null;

            if (videoHasConsumer)
            {
                // Packet-queue depth is left at the VideoDecoderOptions default (512).
                // Block mode (ADR-0060) already paces the no-audio pump correctly, so a
                // smaller depth would only trim read-ahead (512 video packets ≈ 512
                // frames ≈ ~20 s at 25 fps); it is not needed for correctness and is not
                // applied here to avoid any production behavior change. The
                // VideoDecoderOptions.PacketQueueCapacity knob is available if a future,
                // separately-validated tuning pass wants a tighter no-audio queue.
                //
                // Whether the decoder keeps hardware frames on the GPU is decided on a copy of
                // the video path built before it opens, and a decoder that does sizes its pool
                // for what that path can hold (#294, ADR-0081).
                var hardwareDisabled = _hwMode == HardwareDecodeMode.Disabled;
                var videoProbe = HardwareFrameChoice.NeedsProbe(_yieldHardwareFrames, hardwareDisabled)
                    ? VideoProbe(_videoConfigurator, _videoSink!)
                    : default;
                var pathBudget = videoProbe.Graph?.FrameBudgetFor(videoProbe.Source!);

                // Frames stay on the GPU only in a domain the whole path takes, and which domain
                // they are in depends on the backend the decoder binds (#566). Before it opens,
                // the decision is whether any GPU domain gets through.
                var gpuAccepted = _yieldHardwareFrames is null && videoProbe.Graph is { } probeGraph
                    ? probeGraph.FrameDomainsAcceptedFrom(videoProbe.Source!, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly)
                    : FrameMemoryDomains.None;
                var hardwareFrames = HardwareFrameChoice.Decide(
                    _yieldHardwareFrames,
                    hardwareDisabled,
                    _yieldHardwareFrames is null && gpuAccepted == FrameMemoryDomains.None
                        ? videoProbe.Graph?.FrameDomainMismatchFor(
                            videoProbe.Source!, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly)
                        : null,
                    pathBudget);

                var backendOrder = DecodeBackendOrder.Decide(
                    _preferredBackends,
                    _videoSink!.PreferredBackends,
                    hardwareFrames.Yield,
                    _hardwareDevice?.Backend);
                if (!hardwareDisabled && !backendOrder.IsDefault)
                {
                    _logger.LogInformation("Hardware decode order: {Reason}.", backendOrder.Reason);
                }

                var videoBudget = hardwareFrames.Yield ? pathBudget : null;
                var videoFactory = DecoderFactories.CreateVideo(
                    new HardwareDecodeOptions
                    {
                        Mode = _hwMode,
                        PreferredBackends = backendOrder.Preferred,
                        ExcludedCodecs = _excludedCodecs,
                    },
                    _hwCapabilities,
                    _loggerFactory,
                    videoBudget is null && _hardwareDevice is null && DeviceCache is null
                        ? null
                        : new VideoDecoderOptions
                        {
                            HeldHardwareFrames = videoBudget?.Frames ?? 0,
                            Device = _hardwareDevice,
                            DeviceCache = DeviceCache,
                        }
                );
                videoDecoder = videoFactory(demux) as VideoDecoder;
                if (videoDecoder?.BoundBackend is { } bound)
                {
                    hardwareFrames = HardwareFrameChoice.ForBackend(
                        hardwareFrames,
                        _yieldHardwareFrames,
                        bound,
                        hardwareFrames.Yield
                            ? videoProbe.Graph?.FrameDomainMismatchFor(
                                videoProbe.Source!, HardwareFrameDomains.Of(bound), FrameDomainRule.CpuOnly)
                            : null);
                }

                if (!hardwareDisabled)
                {
                    _logger.LogInformation(
                        "Hardware-decoded video frames {Where}: {Reason}.",
                        hardwareFrames.Yield ? "stay on the GPU" : "are downloaded to system memory",
                        hardwareFrames.Reason);
                }

                if (videoDecoder is not null)
                {
                    videoDecoder.YieldHardwareFrames = hardwareFrames.Yield;

                    // A node that cannot take the frames' memory domain, and a path that holds
                    // without bound over a fixed pool, are refused here, at load, rather than
                    // when its first run starts (#435, ADR-0081).
                    if (videoProbe.Graph?.FrameDomainMismatchFor(videoProbe.Source!, videoDecoder.EmittedDomains) is { } mismatch)
                        throw new InvalidOperationException(mismatch.Message);
                    if (videoBudget is not null)
                        videoDecoder.CheckFrameBudget(videoBudget);

                    // Full-queue send policy (ADR-0060). Drop-newest is only safe
                    // when audio shares the single demux pump (so a slow video
                    // chain can't wedge the pump and starve audio). When audio has
                    // no consumer — discarded per ADR-0059, the muted/null-sink
                    // signage path — the video send must BLOCK so it backpressures
                    // the otherwise-unthrottled pump; without it the pump reads the
                    // whole file at IO speed and the drop path sheds most of the
                    // video, freezing playback after ~one queue's worth of frames.
                    videoDecoder.DropNewestWhenQueueFull = audioHasConsumer;
                }
            }
            else
            {
                foreach (var stream in demux.MediaInfo.VideoStreams)
                    concreteDemux.DiscardStream(stream.StreamIndex);
            }

            if (audioHasConsumer)
            {
                audioDecoder = DecoderFactories.CreateAudio(_loggerFactory)(demux) as AudioDecoder;
            }
            else
            {
                foreach (var stream in demux.MediaInfo.AudioStreams)
                    concreteDemux.DiscardStream(stream.StreamIndex);
            }

            // The source is unplayable only when it has no decodable stream at all.
            // A stream that exists but was discarded for lack of a consumer still
            // makes this a valid (if silently-ending) load — matching the prior
            // behaviour where a no-sink controller loads and runs straight to EOS.
            if (
                demux.MediaInfo.VideoStreams.Count == 0
                && demux.MediaInfo.AudioStreams.Count == 0
            )
                throw new InvalidOperationException(
                    $"Source '{source.DisplayName}' has neither a decodable video nor audio stream."
                );

            _pipeline = new DecodingPipeline(
                concreteDemux,
                videoDecoder,
                audioDecoder,
                _loggerFactory.CreateLogger<DecodingPipeline>()
            );

            _demux = demux;
            _videoDecoder = videoDecoder;
            _audioDecoder = audioDecoder;
            _hasAudio = _audioDecoder is not null && _audioSink is not null;

            // Per-item master-clock selection (ADR-0003, evaluated here rather
            // than in the constructor). The audio sink masters the clock only
            // when THIS item has a decodable audio stream the sink will play AND
            // the sink can publish a sample-counter clock. Otherwise the
            // provisional wallclock (set in the constructor) remains the master.
            // This is what lets one warm audio sink span a playlist of mixed
            // audio/silent items, and it removes the stall a video-only item
            // would hit when paced against an attached-but-unactivated audio
            // sink's frozen counter.
            if (_hasAudio && _audioSink is IClockSource audioClock)
            {
                var provisional = _ownedClockSource;
                _clockSource = audioClock;
                _ownedClockSource = null;
                if (provisional is not null)
                    await provisional.DisposeAsync().ConfigureAwait(false);
            }

            // ADR-0057 Stage 2: build the select-by-clock pacer around the video
            // sink now that the per-item master clock is final. The graph's video
            // sink node targets _videoPacer instead of _videoSink, so pacing
            // moves out of the graph operator (no held decode lease across the
            // clock wait) and into delivery-time frame selection. One pacer per
            // session, reused across warmup/play/seek-resume graph rebuilds.
            if (_videoSink is not null)
            {
                _videoPacer = new ClockSelectVideoSink(
                    _videoSink,
                    _clockSource,
                    _loggerFactory.CreateLogger("FrameFlow.Playback.ClockSelect.Video"),
                    maxWait: PaceWaitCap,
                    formatAnnouncer: FormatAnnouncer,
                    sinkFailures: SinkFailures,
                    // A source that stops delivering starves the ring; the controller reports
                    // Rebuffering until frames flow again (#547).
                    onStarved: _callbacks.OnBufferUnderrun,
                    onFed: _callbacks.OnBufferReady,
                    // A sink that throws costs the frame, and the controller reports it on
                    // ErrorOccurred, at most once every few seconds, and plays on. One that keeps
                    // throwing faults the item, as a fault in the graph or the pump does, and so
                    // does a fault in the pacer's own delivery (#703).
                    onSinkFailed: _callbacks.OnRecoverableError,
                    onFaulted: ReportWorkerFault
                );
                // Seat the cap on the gate state this pacer is born into — a session
                // prepared while paused (a playlist advance mid-pause) must not arm the cap
                // against a stopped clock. OpenGates/CloseGates keep the pair in step from
                // here on (#127).
                if (!_videoGate.IsOpen)
                    _videoPacer.Pause();
            }

            // ADR-0056: register every pre-seek-stateful participant once, here, next to
            // where they're constructed. Order matches the historical seek discipline
            // (decoders before the pipeline's pending-packet discard).
            var resettables = new List<ISeekResettable>(3);
            if (_videoDecoder is not null)
                resettables.Add(_videoDecoder);
            if (_audioDecoder is not null)
                resettables.Add(_audioDecoder);
            if (_pipeline is not null)
                resettables.Add(_pipeline);
            _seekResettables = resettables;

            demux = null;
            videoDecoder = null;
            audioDecoder = null;
        }
        catch
        {
            // Tear down the select-by-clock pacer if it was already built before
            // the failure, so its delivery loop task doesn't leak.
            if (_videoPacer is not null)
            {
                try
                {
                    await _videoPacer.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
                _videoPacer = null;
            }
            if (videoDecoder is not null)
            {
                try
                {
                    await videoDecoder.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
            }
            if (audioDecoder is not null)
            {
                try
                {
                    await audioDecoder.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
            }
            if (demux is not null)
            {
                try
                {
                    await demux.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
            }
            throw;
        }
    }

    public async ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_pipeline is null || _renderersActivated)
            return;

        // Audio-only sources skip warmup entirely — software audio decode
        // has no meaningful cold-start cost, and starting the audio pipeline
        // before PlayAsync activates the sink lets buffers accumulate at
        // the gate and surfaces ordering bugs in the resample path. The
        // warmup window is specifically there to absorb hardware-video-
        // decoder cold-start latency; with no video, there's nothing to
        // warm up. Configurator-only video paths (AvaloniaMulticast,
        // LiveCaptioning) still warm up because the video decoder is in
        // the graph.
        var videoDecoder = _videoDecoder;
        var videoIsActive =
            videoDecoder is not null
            && (_videoSink is not null || _videoConfigurator is not null);
        if (!videoIsActive)
        {
            return;
        }

        // Start the long-lived graph + demux pump now, while gates are
        // still closed (their default initial state — see field
        // initializers above). Decoded frames pile up in the bounded
        // edge channels upstream of the gates; the hardware codec
        // session is set up and its expensive first decode is paid
        // here, behind the controller's InitialBuffering / public
        // Loading state. By the time PlayAsync opens the gates, a
        // frame is already waiting at t=0 and audio can't race ahead.
        //
        // Idempotent: re-entry while a task is still running is a no-op
        // beyond the first-frame await below. A run launched beside it
        // would read the same demuxer and decode on the same decoders.
        _runTasks = _runTasks.ForgetEnded(HasEnded);
        if (_runTasks.IsEmpty)
        {
            StartSessionTasks();
        }

        // Wait for either: the first frame to land, or the pump/graph to
        // complete (typically meaning a fault, or the rare edge case of
        // an empty / sub-first-frame stream). Watching the pump/graph
        // matters because the controller's InitialBuffering OnEntry —
        // which calls us — runs ON the dispatch loop that also has to
        // process PlaybackTrigger.FatalError from session callbacks.
        // If we awaited FirstFrameDecoded alone, a pump fault during
        // warmup would deadlock: the fault posts a FatalError trigger,
        // but the dispatch loop is stuck here waiting for a frame the
        // faulted pump will never deliver.
        var firstFrame = videoDecoder!.FirstFrameDecoded;
        var watch = new List<Task>(3) { firstFrame };
        if (_runTasks.Pump is { } pump)
            watch.Add(pump);
        if (_runTasks.Graph is { } graph)
            watch.Add(graph);

        var winner = await Task.WhenAny(watch)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        // Surface a faulted pump/graph (or a disposed decoder) so the
        // InitialBuffering entry routes through FatalError. A clean
        // pump/graph completion before first frame means the source
        // had no decodable video — we treat that as warmup-complete and
        // let the downstream EOS path take it from here.
        if (winner.IsFaulted)
        {
            await winner.ConfigureAwait(false);
        }
    }

    public async ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        if (_pipeline is null)
            return;

        // A seek cancelled before it committed, by a pause sent during it for instance, left the
        // run stopped, and neither branch below would start another (#714).
        await FinishCutShortRepositionAsync(cancellationToken).ConfigureAwait(false);

        if (!_renderersActivated)
        {
            // First play: activate sinks, start clock, open the gates so
            // frames flow. Session tasks were normally started by
            // WarmUpAsync during the controller's InitialBuffering state,
            // so the graph is already running and the first frame is
            // sitting upstream of the video gate ready to render.
            //
            // <b>Nothing-held restart guard.</b> If a caller skipped warmup
            // entirely (e.g. a test that bypasses the state machine),
            // no task is held and we have to start the graph
            // ourselves. But if warmup did run, the tasks exist — even if
            // the demux pump has already drained the file (which happens
            // fast for small clips), the graph task is still alive,
            // blocked on the closed gates. Calling StartSessionTasks here
            // would build a SECOND graph and a SECOND set of decoder
            // source iterators racing the first ones on the same
            // packet-queue reader, which corrupts FFmpeg codec state and
            // crashes the process inside avcodec_send_packet.
            //
            // <b>Gapless-loop clock continuity.</b> Seat the master pacing
            // clock's origin to this item's start position DETERMINISTICALLY,
            // before activating the audio sink — the same reseat primitive the
            // seek path uses (ISeekableClock.SeekBaseline). Each playlist item
            // plays its own 0 → duration timeline, so the origin is the position
            // clock's current value (TimeSpan.Zero for a fresh item or a
            // loop-wrapped one after the position clock is rebased). Without this,
            // an audio-mastered clock left to REDISCOVER its origin from the first
            // post-activation buffer's PTS drifts behind the already-decoded video
            // frames across every gapless-loop boundary: the sample counter sits
            // at the (device-paced, buffer-PTS-dependent) origin while video PTS
            // climb, PaceUntil waits, and on a weak box hits the 5 s wait-cap
            // (cosmetic micro-hitch). Seating the origin here makes the audio clock
            // and the per-item frame PTS agree from the first frame at every loop
            // boundary. This never overrides a seek target. A seek after the first
            // play reactivates outside this branch entirely, and a seek before it
            // leaves the position clock on the target, which is what itemOrigin reads.
            var itemOrigin = _clock.Position;
            (_clockSource as ISeekableClock)?.SeekBaseline(itemOrigin);

            // Re-derive the pacer's floor from the origin this run actually starts at,
            // rather than trusting whatever a previous discontinuity left pending. This is
            // the one launch path that seats the timeline without going through
            // RepositionAsync, so it is the one place a pending floor could otherwise
            // outlive the seek that recorded it. Load → Seek → Play arrives here with
            // Position already at the target and gets the same value it was carrying;
            // an ordinary first play gets Zero, which is no floor.
            _pendingSeekFloor = itemOrigin;
            _pendingSeekSettles = false;

            if (_hasAudio)
                await _audioSink!.ActivateAsync(cancellationToken).ConfigureAwait(false);

            _clock.Start(itemOrigin);

            // Start runs the wallclock from zero and discards the baseline seated above,
            // because the owned wallclock is the same object as _clockSource. It is seated
            // again once running. Otherwise a seek before the first play paced the target's
            // frames against a clock at zero, and each one waited out PaceWaitCap (#639).
            if (_ownedClockSource is { } wallclock)
            {
                wallclock.Start();
                wallclock.Seek(itemOrigin);
            }

            if (_runTasks.IsEmpty)
            {
                StartSessionTasks();
            }

            OpenGates();

            _renderersActivated = true;
            return;
        }

        // Resume from pause: re-open the gates so the (usually) already-
        // running graph drains buffered frames, and resume audio + clock.
        //
        // <b>Graph-may-be-torn-down case.</b> <see cref="SeekAsync"/>'s
        // wasPaused branch stops the graph + pump and intentionally does
        // NOT restart them — the comment there delegates that to "the
        // next user PlayAsync". So before opening the gates we need to
        // detect that case and rebuild the session tasks; otherwise the
        // user gets a Play that produces silent video and silent audio
        // until the next seek happens to restart the pump. Symptom in
        // pre-fix logs: post-(pause + seek-to-0 + play) the audio sink
        // reports "duration=2.09s, Blocks=0" — i.e. nothing was pushed
        // to it for the entire "playing" interval.
        //
        // <b>The discriminator is _sessionCts, NOT task completion.</b> Only the
        // seek-while-paused teardown sets _sessionCts to null (it cancels the
        // session CT via StopGraphAsync, then nulls it). A plain pause leaves the
        // live session CT in place. Keying the rebuild off task completion
        // instead is WRONG now that ADR-0057 Stage 2 removed the in-graph
        // PaceUntil: with pacing moved to the presenter, the demux pump is no
        // longer throttled by the in-graph clock wait, so on a small clip it
        // reads the whole file and reaches EOF (its task completes) during warmup
        // — long before the user pauses. The decoder still holds every demuxed
        // packet in its bounded queue and keeps producing frames against the
        // (closed) gate's backpressure, so resume only needs to reopen the gate.
        // Rebuilding on the pump task's completion would instead restart the demux
        // pump against an already-exhausted demuxer (immediate EOF) and abandon
        // the original decoder's queued packets, ending playback seconds early —
        // the video-only pause/resume regression. Gate the rebuild on the one
        // condition that actually means "torn down": _sessionCts is null.
        if (_sessionCts is null)
        {
            StartSessionTasks();
        }

        _clock.Resume();
        _ownedClockSource?.Resume();

        OpenGates();

        if (_hasAudio)
            await _audioSink!.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Repositions at the clock's position when a reposition was cancelled before it committed,
    /// so the play that follows launches a run instead of opening the gates on a stopped one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reposition stops the run first, and clears <see cref="_sessionCts"/> only once it has
    /// committed (<see cref="RepositionAsync"/> step 6). One cancelled before that, such as a seek
    /// whose wait for the run a pause cut short, leaves the token cancelled and set. The run is
    /// stopped or still unwinding, and the demuxer, the decoders and the pacer are where it left
    /// them, or partway through the seek. The resume branch of <see cref="PlayAsync"/> restarts
    /// only a run a committed reposition cleared, and its first-play branch only when no task is
    /// held, so neither starts one (#714).
    /// </para>
    /// <para>
    /// A run launched over that state would decode on codec contexts a cancellation interrupted,
    /// which the class remarks warn against. Repositioning where the clock stands, which the
    /// cancelled seek never moved, does what a seek taken while paused does: it waits for the old
    /// run's tasks, flushes the pacer, seeks the demuxer, resets the decoders and seats the clocks,
    /// and the play then launches from there. The cancelled seek's target is not taken.
    /// </para>
    /// </remarks>
    private ValueTask FinishCutShortRepositionAsync(CancellationToken cancellationToken) =>
        _sessionCts is { IsCancellationRequested: true }
            ? RepositionAsync(_clock.Position, GraphPolicy.Rebuild, TaskPolicy.Cancel, cancellationToken)
            : ValueTask.CompletedTask;

    /// <summary>
    /// Upper bound on waiting for a post-seek run's destination frame before giving up and
    /// leaving the clocks where they are.
    /// </summary>
    /// <remarks>
    /// The wait is the decoder walking from the keyframe to the seek target — tens of
    /// milliseconds on ordinary content, and 0.73 s measured on a 1080p60 file whose only
    /// keyframe is at 0.0. Five seconds is far past any of that, so this only trips when the
    /// frame is never coming: a target past the end of the stream, or a stalled decoder.
    /// Same order as the pacer's own wait cap, and for the same reason — degrade to the
    /// previous behaviour rather than hang.
    /// </remarks>
    private static readonly TimeSpan SeekSettleCap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reseats the clocks onto the first frame a post-seek run actually delivers, so the
    /// decode-forward that produced it does not count as playback time (#161).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seek repositions the demuxer to the keyframe at or before the target and seats the
    /// clocks on the target (<see cref="RepositionAsync"/> step 6). The decoder then has to
    /// walk from that keyframe to the target before anything can be shown — 0.73 s on a
    /// file with sparse keyframes. The clocks count that walk as playback, so by the time
    /// the destination frame arrives they are already past it and the pacer, correctly,
    /// releases everything up to where they have got to: the picture holds on the pre-seek
    /// frame and then covers 0.78 s of content in ~80 ms before settling.
    /// </para>
    /// <para>
    /// Reseating onto the frame that arrived removes the run-up. It is a backwards step in
    /// reported position, which is why it happens here and not later: this is inside the
    /// seek, where a discontinuity is expected and one has just occurred anyway.
    /// </para>
    /// <para>
    /// <b>Only from the reposition, never from PlayAsync.</b> This waits on pipeline
    /// progress, and <c>PlayAsync</c> is dispatched on the controller's command loop, so
    /// waiting there would hold every other command — pause, seek, stop — behind the
    /// decoder's walk. <see cref="RepositionAsync"/> runs on the controller's detached seek
    /// runner instead, where blocking costs nothing but the seek's own duration. The
    /// consequence is that a seek taken while paused is not covered: it decodes forward on
    /// the following resume, which is a command-loop call.
    /// </para>
    /// <para>
    /// <b>Wallclock master only.</b> An audio-mastered clock does not advance during the
    /// walk — the sink is deactivated across the reposition and its sample counter only
    /// moves as the device consumes — so there is nothing to correct, and reseating its
    /// origin from a video frame's PTS is the exact move ADR-0057 §B5 exists to prevent.
    /// </para>
    /// </remarks>
    private async ValueTask SettleClocksOnSeekTargetAsync(CancellationToken cancellationToken)
    {
        // _ownedClockSource is non-null only when this session masters with a wallclock.
        if (_videoPacer is null || _ownedClockSource is null)
            return;

        // Captured before the wait, so a settle that finishes late releases the hold it
        // armed rather than one a newer run has since taken.
        var runId = _videoPacer.CurrentRunId;
        try
        {
            var reached = await _videoPacer
                .WaitForSeekTargetAsync(runId, SeekSettleCap, cancellationToken)
                .ConfigureAwait(false);

            // Null means this run had no seek floor (an ordinary play or resume), or the
            // target never arrived. Both are "leave the clocks alone".
            if (reached is not { } pts)
                return;

            _clock.Seek(pts);
            _ownedClockSource.Seek(pts);
        }
        finally
        {
            // The pacer holds delivery from the moment it admits the destination frame
            // until this runs, so that no frame is presented against the clock this method
            // is correcting. In a finally because a cancelled or faulted settle must not
            // leave the picture held.
            _videoPacer.ReleaseSeekSettle(runId);
        }
    }

    /// <summary>
    /// Opens both stream gates and re-arms the pacer's wait cap.
    /// </summary>
    /// <remarks>
    /// The gates and the pacer's cap are one decision, so they move together here rather
    /// than at each of the six call sites. The pacer knows only the clock, and a clock that
    /// is stopped because the gates are shut looks exactly like a stalled one — which is
    /// how a pause came to force-present the ring a frame at a time (#127).
    /// </remarks>
    private void OpenGates()
    {
        if (_videoDecoder is { } decoder)
            decoder.PoolWatchdogSuspended = false;
        _videoPacer?.Resume();
        _videoGate.Open();
        _audioGate.Open();
    }

    /// <summary>
    /// Closes both stream gates and suspends the pacer's wait cap. See <see cref="OpenGates"/>.
    /// </summary>
    private void CloseGates()
    {
        _videoGate.Close();
        _audioGate.Close();
        _videoPacer?.Pause();

        // Holders stop releasing while the gates are shut, so a decoder parked at its pool
        // budget is waiting on purpose (ADR-0081 decision 5).
        if (_videoDecoder is { } decoder)
            decoder.PoolWatchdogSuspended = true;
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        // Pause the audio sink first so the user hears silence
        // immediately. Then close the gates — frames already in the
        // sink-side channel may drain (one frame each), but no new
        // frames pass the gate until resume.
        if (_hasAudio)
            await _audioSink!.PauseAsync(cancellationToken).ConfigureAwait(false);

        CloseGates();

        _clock.Pause();
        _ownedClockSource?.Pause();
    }

    public ValueTask SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default
    ) =>
        // A user seek can land mid-playback: cancel in-flight work (TaskPolicy.Cancel) and
        // rebuild a fresh graph topology on restart (GraphPolicy.Rebuild). The ordered
        // discontinuity recipe lives once in RepositionAsync.
        RepositionAsync(position, GraphPolicy.Rebuild, TaskPolicy.Cancel, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Old path (SeekAsync(0)) vs this rewind.</b> A <c>RepeatMode.One</c> loop boundary is
    /// reached at natural end-of-stream, so by the time the controller fires this the graph
    /// run task and the demux pump task have already completed cleanly — nothing is in flight.
    /// The full <see cref="SeekAsync"/> path is built for a seek that can arrive mid-playback:
    /// it cancels the session CTS to stop a running graph (<see cref="StopGraphAsync"/>), then
    /// rebuilds a brand-new graph topology on restart (<see cref="StartSessionTasks"/> →
    /// <see cref="BuildGraph"/>). On a 24/7 attract loop that pays a graph teardown + full
    /// rebuild every loop, and the cancel-mid-GPU-op is the path implicated in the native
    /// seek-cancel wedge. This rewind instead <b>awaits the already-finished tasks without
    /// cancelling</b> and <b>re-runs the retained graph</b> (<see cref="LaunchSessionTasks"/>),
    /// skipping both the CTS-cancel and the topology rebuild.
    /// </para>
    /// <para>
    /// <b>What it keeps (the correctness-load-bearing steps), identical to the seek path:</b>
    /// demuxer reposition to the first packet (<see cref="IDemuxSession.SeekAsync"/> to 0,
    /// which for ts=0 BACKWARD is a true rewind-to-start), the uniform <c>ResetForSeek</c> pass
    /// over decoders + pipeline (codec flush + drain, packet-queue replacement, and the
    /// pending-packet drop that stops a pre-loop packet anchoring the post-loop clock), and the
    /// master-clock epoch reseat — both the position clock (<see cref="IPlaybackClock.Seek"/>)
    /// and the pacing clock source (<see cref="ISeekableClock.SeekBaseline"/>) to 0, exactly as
    /// a user seek to 0 does. That establishes the loop epoch deterministically with no
    /// gapless-loop clock drift and guarantees no pre-loop frame leaks past the boundary
    /// (gates are closed across the reset, the codec is flushed, the queues are replaced).
    /// </para>
    /// <para>
    /// <b>Safety fallback.</b> If the tasks are somehow still running when this is called (not
    /// the EOS loop case this is designed for), it cancels them via <see cref="StopGraphAsync"/>
    /// first — so the method is correct even off the happy path; it just forfeits the cheap
    /// no-cancel route in that case.
    /// </para>
    /// </remarks>
    public ValueTask RewindToStartAsync(CancellationToken cancellationToken = default) =>
        RewindToAsync(TimeSpan.Zero, _itemEnd, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// The rewind <see cref="RewindToStartAsync"/> describes, to <paramref name="position"/>
    /// rather than to zero: the start of the item this runtime now plays, which for a clip is
    /// partway into the source. The runs that follow stop at <paramref name="end"/>, that item's
    /// end.
    /// </remarks>
    public ValueTask RewindToAsync(TimeSpan position, TimeSpan? end, CancellationToken cancellationToken)
    {
        if (_pipeline is null || _demux is null)
            return ValueTask.CompletedTask;

        _itemEnd = end;

        // A hand-off to another clip of this source positions it here rather than at open, so
        // the open's check applies here too. The rewind then fails, the playlist rebuilds the
        // item, and the rebuilt item fails to start.
        if (position > TimeSpan.Zero)
            ThrowIfPastTheEnd(position);

        // If the graph was never built (e.g. a session that never reached
        // StartSessionTasks), there is nothing to reuse — fall back to the full seek
        // (Rebuild + Cancel), which builds/initialises whatever is missing.
        if (_graph is null)
            return RepositionAsync(
                position,
                GraphPolicy.Rebuild,
                TaskPolicy.Cancel,
                cancellationToken
            );

        // A RepeatMode.One loop boundary is reached at natural EOS, so nothing is in
        // flight: settle the already-finished tasks WITHOUT cancelling
        // (TaskPolicy.AwaitClean) and re-run the retained graph (GraphPolicy.Reuse),
        // skipping the per-loop CTS-cancel and topology rebuild.
        return RepositionAsync(
            position,
            GraphPolicy.Reuse,
            TaskPolicy.AwaitClean,
            cancellationToken
        );
    }

    /// <summary>
    /// Selects how the post-discontinuity graph is brought back up in
    /// <see cref="RepositionAsync"/>.
    /// </summary>
    private enum GraphPolicy
    {
        /// <summary>
        /// Build a fresh graph topology on restart (<see cref="StartSessionTasks"/> →
        /// <see cref="BuildGraph"/>). The seek path: its restart follows a CTS-cancel of
        /// in-flight work, so a brand-new topology is the safe shape.
        /// </summary>
        Rebuild,

        /// <summary>
        /// Re-run the retained <see cref="_graph"/> (<see cref="LaunchSessionTasks"/>)
        /// without rebuilding the topology. The cheap loop-rewind path: it skips the
        /// per-loop teardown/rebuild on a 24/7 attract loop.
        /// </summary>
        Reuse,
    }

    /// <summary>
    /// Selects how the running pump + graph tasks are stopped in
    /// <see cref="RepositionAsync"/> before the discontinuity is applied.
    /// </summary>
    private enum TaskPolicy
    {
        /// <summary>
        /// Always cancel the session CTS and wait (<see cref="StopGraphAsync"/>). The seek
        /// path: a user seek can land while the graph is actively running, so the only safe
        /// way to halt it is to cancel. Safe here because the gates are already closed.
        /// </summary>
        Cancel,

        /// <summary>
        /// Await the (normally already-completed) tasks WITHOUT cancelling
        /// (<see cref="WaitForSessionTasksAsync"/>) — the whole point of the cheap rewind,
        /// since it avoids the cancel-mid-GPU-op wedge. Falls back to
        /// <see cref="StopGraphAsync"/> only if a task is somehow still running (the
        /// non-EOS safety path), or a lateness-recovery walk is held: it runs until it is
        /// cancelled, and once the pump and the graph have ended, cancelling interrupts
        /// nothing else.
        /// </summary>
        AwaitClean,
    }

    /// <summary>
    /// The single ordered discontinuity recipe shared by <see cref="SeekAsync"/> and
    /// <see cref="RewindToStartAsync"/> (architecture-deepening §2.2): close gates → pause
    /// audio → settle tasks → flush the pacer → deactivate audio → reposition the demuxer →
    /// uniform <c>ResetForSeek</c> pass → reseat both clocks → relaunch → reactivate + resume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recipe is the ADR-0048 seek discipline; the ordering and the
    /// <c>ResetForSeek</c> pass (ADR-0056) are correctness-load-bearing — a missing or
    /// reordered step is exactly the four-bug seek-state-leak class ADR-0048 catalogues.
    /// Only two axes differ between a user seek and a loop rewind, and they are the two
    /// parameters:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <paramref name="taskPolicy"/> — how in-flight tasks are stopped
    /// (<see cref="TaskPolicy.Cancel"/> for seek vs <see cref="TaskPolicy.AwaitClean"/> for
    /// the cheap rewind).
    /// </description></item>
    /// <item><description>
    /// <paramref name="graphPolicy"/> — whether the unpaused relaunch rebuilds the topology
    /// (<see cref="GraphPolicy.Rebuild"/>) or re-runs the retained graph
    /// (<see cref="GraphPolicy.Reuse"/>).
    /// </description></item>
    /// </list>
    /// </remarks>
    private async ValueTask RepositionAsync(
        TimeSpan target,
        GraphPolicy graphPolicy,
        TaskPolicy taskPolicy,
        CancellationToken cancellationToken
    )
    {
        if (_pipeline is null || _demux is null)
            return;

        // A session that has not played yet holds still as a paused one does. Its clock is
        // stopped rather than paused, so IsPaused alone reads false, and resuming would open
        // the gates before the first PlayAsync (#639). That PlayAsync launches the graph.
        var wasPaused = _clock.IsPaused || !_renderersActivated;

        // Supersede any floor a previous discontinuity recorded but never launched: this
        // reposition is now the one that decides where playback resumes.
        _pendingSeekFloor = TimeSpan.Zero;
        _pendingSeekSettles = false;

        // Step 1: close gates so no frames forward to sinks during the reposition, and
        // pause audio so we don't hear the in-flight pre-discontinuity audio buffer.
        // (At a clean EOS loop boundary the gates are open and the sinks have drained;
        // closing here is defensive symmetry that also covers the AwaitClean safety path.)
        CloseGates();

        // A session that has not played never activated the audio sink, so it has nothing to
        // pause. A detached one shares the sink with the item that is playing, and must not.
        if (_hasAudio && _renderersActivated)
            await _audioSink!.PauseAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: settle the pump + graph tasks. Safe regardless of policy because the
        // gates are already closed — no in-flight decode the stop could interrupt mid-call.
        //   • Cancel (seek): always cancel the session CTS and wait — a user seek can land
        //     while the graph is actively running.
        //   • AwaitClean (rewind): at EOS the tasks have already completed, so just await
        //     them with NO CTS cancel (the whole point — avoids the per-loop graph teardown
        //     and the cancel-mid-GPU-op wedge). Fall back to the cancel path only if a task
        //     is somehow still running, which a lateness-recovery walk always is: it ends only
        //     when cancelled.
        // Either way the wait covers tasks an earlier reposition left held: one this
        // reposition supersedes was cancelled while it waited for them, and they may still be
        // unwinding. Nothing below touches the demuxer or the decoders until they have ended.
        // If this reposition is cancelled in turn, the wait throws and leaves what has not
        // ended held, for the next stop to wait for (#707).
        if (taskPolicy == TaskPolicy.Cancel)
        {
            await StopGraphAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (_runTasks.ForgetEnded(HasEnded).IsEmpty)
                await WaitForSessionTasksAsync(cancellationToken).ConfigureAwait(false);
            else
                await StopGraphAsync(cancellationToken).ConfigureAwait(false);
        }

        // The interrupted run is over: its tasks have ended, and nothing has relaunched. An
        // end-of-stream it raised read the old number; any run from here on reads the new one.
        Interlocked.Increment(ref _runNumber);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2b (ADR-0057 Stage 2): drop any frames buffered in the select-by-clock pacer.
        // They were decoded against the pre-discontinuity timeline; the clock is about to
        // rebase to the target, so a lingering pre-discontinuity frame would present at the
        // wrong moment. The graph is already stopped, so no new frame races this flush. (At a
        // clean EOS loop boundary the pacer is already drained, so this is a no-op there; it
        // matters on the AwaitClean safety path, where StopGraphAsync stopped a live run.)
        _videoPacer?.Flush();

        // Step 3: deactivate audio so we can reactivate fresh at the target position. As in
        // step 1, only a session that has played activated it.
        if (_hasAudio && _renderersActivated)
            await _audioSink!.DeactivateAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 4: reposition the demuxer. For target 0 with the BACKWARD flag this lands at
        // the file start (a true rewind-to-start) — the same call the seek path makes.
        Take(Step.SeekingDemux);
        await _demux.SeekAsync(target, cancellationToken).ConfigureAwait(false);

        // Step 5: invalidate every pre-discontinuity-stateful participant in one uniform pass
        // (ADR-0056). Each component folds its complete seek reset into ResetForSeek —
        // decoders replace their packet queue + flush the codec; the pipeline drops the
        // pump's retained pre-seek packet (which belongs to the pre-seek timeline and would
        // otherwise become the stale-PTS head of the post-seek stream, anchoring the master
        // clock behind the target and freezing PaceUntil). Registering participants in one
        // list (see InitializeAsync) mechanises the ADR-0048 seek-discipline audit: the four
        // historical seek-state-leak bugs all came from a missing step in a hand-listed
        // checklist here.
        Take(Step.ResettingForSeek);
        foreach (var resettable in _seekResettables)
        {
            resettable.ResetForSeek();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 6: reset the clock to the target position. A detached runtime records the
        // target instead, and Attach seats the shared clocks on it (media-clips decision 8). The
        // clock it owns, a wallclock, it seats now.
        if (!_attached)
        {
            _detachedOrigin = target;
            _ownedClockSource?.SeekBaseline(target);
        }
        else
        {
            _clock.Seek(target);
            // Reseat the master pacing clock's origin to the exact target, uniformly across
            // clock masters: WallClockSource (no audio) and the OpenAL sink (audio) both
            // implement ISeekableClock. This replaces the old `_ownedClockSource?.Seek` — a
            // no-op precisely in the audio-mastered case, where the sink was left to re-discover
            // its origin from the first post-seek buffer's PTS. A stale or keyframe-rounded first
            // buffer there anchored the clock off the target and hung PaceUntil (the frozen-video
            // / choppy-seek root cause). For the audio sink this seeds the origin; its next
            // ActivateAsync (Step 7) applies it.
            //
            // This same reseat deterministically seats the AUDIO-MASTERED gapless-loop origin
            // (perf survey B5): a RepeatMode.One single-clip loop reaches here with target == 0,
            // so a signage surface's looping clip realigns its OpenAL sample-counter
            // origin to the loop epoch at every boundary instead of re-discovering it from the
            // first post-loop buffer's PTS — no per-loop epoch drift. Keep this uniform across
            // user seeks and loop-seeks; do not special-case the user-seek path, or the
            // gapless-loop drift returns (covered by AudioMasteredLoopOriginTests).
            (_clockSource as ISeekableClock)?.SeekBaseline(target);
        }

        // The clock now reads `target` while the demuxer restarts at the keyframe at or
        // before it, so the frames in between arrive already due and would present at
        // decode rate (#157). The next launch arms the pacer to refuse them.
        //
        // Recorded here rather than applied at the step-2b flush because this is the first
        // point the discontinuity has actually committed, and carried rather than applied
        // now because a seek taken while paused does not relaunch — the next PlayAsync
        // does, and it is that run the floor belongs to.
        _pendingSeekFloor = target;

        // Only the branch below that resumes playing goes on to wait for the destination
        // frame and reseat. A seek taken while paused relaunches on the next PlayAsync,
        // which runs on the command loop and cannot wait — so that run gets the floor but
        // not the delivery hold.
        _pendingSeekSettles = !wasPaused && _ownedClockSource is not null;

        RetireSessionToken();

        // The cheap rewind keeps the post-CTS-null cancellation check the seek path omits:
        // at the loop boundary it is the documented "a concurrent user seek can cancel the
        // loop rewind here" point, and bailing now (before reactivate/relaunch) lets the
        // superseding seek own the restart. The seek path intentionally has no check here, so
        // a superseded user seek still completes its own relaunch — preserving the exact
        // WasCanceled-vs-completed outcome each path reports today.
        if (graphPolicy == GraphPolicy.Reuse)
            cancellationToken.ThrowIfCancellationRequested();

        // Step 7: restore sink + graph state for the post-discontinuity world.
        if (wasPaused)
        {
            // Re-init audio in paused state so it's ready for the next resume but produces no
            // audible output now. Don't restart the graph or open gates — the next user
            // PlayAsync does that. The clock stays seeked + paused. A user seek reaches here,
            // and so does a playlist's in-place rewind of a paused item or of one that has not
            // played (media-clips decision 5).
            // Before the first play the sink has not been activated, and the first-play
            // branch of PlayAsync activates it, so it is left alone here.
            if (_hasAudio && _renderersActivated)
            {
                await _audioSink!.ActivateAsync(cancellationToken).ConfigureAwait(false);
                await _audioSink!.PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // Resume playing from the target WITHOUT going through PlayAsync's first-play
            // branch (which would restart the clock from zero, undoing our Seek). Reactivate
            // audio, bring the graph back up per graphPolicy, open gates, resume audio.
            if (_hasAudio)
                await _audioSink!.ActivateAsync(cancellationToken).ConfigureAwait(false);

            if (graphPolicy == GraphPolicy.Rebuild)
                StartSessionTasks();
            else
                LaunchSessionTasks();

            OpenGates();

            if (_hasAudio)
                await _audioSink!.ResumeAsync(cancellationToken).ConfigureAwait(false);

            // Step 8: let the destination frame arrive, then put the clocks on it. This is
            // what makes the seek return once the target is actually showable rather than
            // once the pipeline has been told to go and get it.
            await SettleClocksOnSeekTargetAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the run and waits for its tasks to end, then frees the pacer, the pump pipeline, the
    /// decoders and the demux session.
    /// </summary>
    /// <remarks>
    /// The wait covers tasks a seek left held when its own wait for them was cancelled, so nothing
    /// is freed while a pump or a graph from before the seek still uses it (#707). It lasts as long
    /// as the run takes to unwind once cancelled: a sink or a configurator's node that ignores its
    /// token holds it until the call returns. A demuxer read that waits on a stalled source does
    /// not, since the run's cancellation interrupts it (#709).
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        var failures = new List<Exception>();

        // Open the gates so any blocked operator bodies unblock and the
        // subsequent cancellation propagates through cleanly.
        //
        // A detached runtime keeps them shut. What waits at them would reach
        // the sinks it shares with the item that is playing, and the gate's
        // wait ends on the cancellation anyway.
        if (_attached)
            OpenGates();

        try
        {
            await StopGraphAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        // Tear down the select-by-clock pacer after the graph is stopped (so the
        // video sink node is no longer delivering into it). This stops its
        // delivery loop and disposes any buffered frames; it does NOT dispose the
        // wrapped _videoSink — the session is a user, not the owner, of that sink
        // (ADR-0044).
        if (_videoPacer is not null)
        {
            try
            {
                await _videoPacer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            _videoPacer = null;
        }

        // A detached runtime shares the audio sink and the position clock with the item that is
        // playing, and never touched either, so it leaves them as they are.
        if (_hasAudio && _attached)
        {
            try
            {
                await _audioSink!.DeactivateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (_attached)
            _clock.Stop();

        if (_pipeline is not null)
        {
            Take(Step.DisposingPipeline);
            try
            {
                await _pipeline.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            _pipeline = null;
        }
        if (_videoDecoder is not null)
        {
            Take(Step.DisposingVideoDecoder);
            try
            {
                await _videoDecoder.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            _videoDecoder = null;
        }
        if (_audioDecoder is not null)
        {
            Take(Step.DisposingAudioDecoder);
            try
            {
                await _audioDecoder.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            _audioDecoder = null;
        }
        if (_demux is not null)
        {
            Take(Step.DisposingDemux);
            try
            {
                await _demux.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            _demux = null;
        }

        if (_ownedClockSource is not null)
        {
            try
            {
                await _ownedClockSource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        try
        {
            _sessionCts?.Dispose();
        }
        catch { }
        _sessionCts = null;

        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException(
                "SubstrateSession teardown encountered multiple failures.",
                failures
            );
    }

    // ── Session-task lifecycle (single graph per session run) ───────

    private void StartSessionTasks()
    {
        if (_pipeline is null)
            return;

        // Build a fresh graph topology, then launch the pump + graph tasks over
        // it. SeekAsync's restart goes through here (a fresh graph after the
        // CTS-cancel of in-flight work); the RepeatMode.One loop boundary goes
        // through RewindToStartAsync, which re-runs the cached _graph instead.
        ThrowIfRunGoing();
        _graph = BuildGraph();
        LaunchSessionTasks();
    }

    /// <summary>
    /// Refuses a launch while a task of the last run has not ended. Every caller has waited for
    /// that run's tasks, or holds none, so this does not throw: it keeps a second pump or graph
    /// from ever running beside the first on the same demuxer and decoders (#707).
    /// </summary>
    private void ThrowIfRunGoing()
    {
        _runTasks = _runTasks.ForgetEnded(HasEnded);
        if (!_runTasks.IsEmpty)
        {
            throw new InvalidOperationException(
                "A playback run cannot launch while the run before it is still going."
            );
        }
    }

    private static bool HasEnded(Task task) => task.IsCompleted;

    /// <summary>
    /// Cancels the session token, unless it is cancelled already, then disposes its source.
    /// </summary>
    /// <remarks>
    /// Every caller runs once the run's tasks have ended, the lateness-recovery walk among them, so
    /// the cancellation stops nothing that still runs. It keeps a source from being disposed with
    /// its token uncancelled, which a rewind that waited for a run without stopping it used to do.
    /// </remarks>
    private void RetireSessionToken()
    {
        if (_sessionCts is not { } cts)
            return;
        _sessionCts = null;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    /// <summary>
    /// Builds the session graph topology (source → pace → gate → consumer chain → sink)
    /// and returns it, or <see langword="null"/> when there is no stream to pump. Pure
    /// topology construction: it does not start the CTS or any task. Split out of
    /// <see cref="StartSessionTasks"/> so the graph instance can be retained and re-run by
    /// <see cref="RewindToStartAsync"/> without rebuilding it every loop.
    /// </summary>
    private FrameFlow.Graph.Graph? BuildGraph()
    {
        var hasVideo = _videoDecoder is not null && _videoSink is not null;
        var hasAudio = _audioDecoder is not null && _audioSink is not null;

        if (!hasVideo && !hasAudio)
        {
            // No stream to pump — caller fires end-of-stream directly.
            return null;
        }

        // Build the graph: source → gate → (consumer chain) → sink.
        // The pause gate is a session-level concern (the session's pause
        // state) and runs BEFORE the configurator regardless of where the
        // chain terminates, so every frame any configurator-wired sink sees is
        // pause-gated.
        //
        // <b>Pacing (ADR-0057 Stage 2): out of the graph, into the sink.</b>
        // Video used to carry an in-graph <see cref="PaceUntil"/> operator that
        // awaited the master clock while holding the frame inside the operator —
        // pinning a decode-pool lease across the wait and starving the
        // FFmpeg-default hwframe pool on a long wait (the confirmed
        // choppiness + lockstep-drop coupling, perf survey §A1). For the
        // <b>single-sink</b> path the pacer is now <see cref="ClockSelectVideoSink"/>
        // (_videoPacer): frames arrive at decode rate, the graph sink-pump
        // releases its reference to each frame immediately (no held lease), and the
        // decorator selects the frame due "now" on the clock at delivery time,
        // dropping late ones. So no clock wait happens inside the graph.
        //
        // <b>Configurator-terminated path keeps PaceUntil.</b> When the consumer
        // supplies a configurator but NO sink (AvaloniaMulticast,
        // LiveCaptioning — fan-out / inference chains that wire their own
        // sinks), there is no single sink to decorate, and the substrate's
        // pull/forward node model can't express a buffering clock-pump operator
        // without a much larger change. Those paths therefore retain the in-graph
        // PaceUntil so they stay paced (the "first seconds accelerated" footgun
        // stays fixed). They were never the confirmed held-lease problem (the
        // bug is the single-sink zero-copy presenter), so their lease
        // characteristic is unchanged.
        var graph = new FrameFlow.Graph.Graph();

        if (hasVideo)
        {
            // One shape: source → gate → (configurator) → select-by-clock pacer wrapping the
            // registered sink. Pacing happens in the decorator at delivery time, not in the
            // graph, so the graph holds no decode lease across a clock wait. Edges stay at the
            // substrate default (capacity 1, blocking); the decorator's ring provides the
            // read-ahead slack that keeps the hwframe pool filled.
            //
            // There is no second shape any more. A configurator that wired its own terminal used
            // to run here behind an in-graph PaceUntil, which is the carve-out ADR-0057 took and
            // the chain contract removes: a consumer that needs extra sinks wires them on Branch
            // edges and returns its trunk open.
            WireVideo(
                graph,
                BeforeRunEnd(
                    FromRunFloor(_videoDecoder!.AsSourceNode("video-source"), AdmitVideo),
                    static frame => frame.Pts
                ),
                _videoGate.AsOperator("video-gate"),
                _videoConfigurator,
                (_videoPacer ?? (IVideoSink)_videoSink!).AsSinkNode("video-sink")
            );
        }

        if (hasAudio)
        {
            // Audio needs no pacer: the sink consumes at realtime by feeding the device. The
            // gate still runs upstream of the configurator, so a pause halts the taps a
            // configurator wires as well as the sink.
            var chain = graph.Pipeline(
                    BeforeRunEnd(
                        FromRunFloor(_audioDecoder!.AsSourceNode("audio-source"), AdmitAudio),
                        static buffer => buffer.PresentationTime
                    )
                )
                .Then(_audioGate.AsOperator("audio-gate"));
            if (_audioConfigurator is not null)
                chain = _audioConfigurator(chain);
            chain.To(_audioSink!.AsSinkNode("audio-sink"));
        }

        return graph;
    }

    /// <summary>
    /// <paramref name="source"/>, delivering nothing presented at or after the current run's end.
    /// An item past it is released as soon as it is decoded, and the source reads on until the
    /// decoder finishes the packets the pump fed before the end.
    /// </summary>
    /// <remarks>
    /// The end is read for each item rather than captured, because a rewind re-runs the retained
    /// graph for a run whose end may differ: the next clip of the same source.
    /// </remarks>
    private SourceNode<T> BeforeRunEnd<T>(SourceNode<T> source, Func<T, TimeSpan> timestamp)
        where T : class, IRefCounted =>
        new(
            source.Id,
            async ct =>
            {
                while (await source.Body(ct).ConfigureAwait(false) is { } item)
                {
                    if (timestamp(item).Ticks < Volatile.Read(ref _runEndTicks))
                        return item;
                    item.Dispose();
                }
                return null;
            },
            source.OnError,
            source.Cleanup,
            source.OnBudget,
            source.Emits
        );

    /// <summary>
    /// <paramref name="source"/>, delivering what <paramref name="admit"/> lets through of each
    /// item as it is decoded. <paramref name="admit"/> owns the item it is given: it returns it,
    /// returns what replaces it, or releases it and returns null.
    /// </summary>
    /// <remarks>
    /// A run that starts past a keyframe decodes from the keyframe. The pacer refuses the video
    /// frames before the start, but only once the pause gate passes them, and nothing refused the
    /// audio. Refusing both at the source lets a run behind a closed gate decode on to its start: a
    /// runtime that has warmed up, and one prepared ahead, go on decoding until what they play first
    /// waits at the gate (media-clips decision 8). Otherwise audio from the keyframe fills its queue
    /// at the gate, and the demuxer stops feeding the video decoder before it reaches the start.
    /// </remarks>
    private static SourceNode<T> FromRunFloor<T>(SourceNode<T> source, Func<T, T?> admit)
        where T : class, IRefCounted =>
        new(
            source.Id,
            async ct =>
            {
                while (await source.Body(ct).ConfigureAwait(false) is { } item)
                {
                    if (admit(item) is { } admitted)
                        return admitted;
                }
                return null;
            },
            source.OnError,
            source.Cleanup,
            source.OnBudget,
            source.Emits
        );

    // A frame presented before the run's start is released.
    private IVideoFrame? AdmitVideo(IVideoFrame frame)
    {
        if (_videoFloor.Admits(frame.Pts))
            return frame;
        frame.Dispose();
        return null;
    }

    // Audio that ends at or before the run's start is released, and a buffer that crosses the start
    // is cut at it, so the audio starts where the picture does.
    private PcmAudioBuffer? AdmitAudio(PcmAudioBuffer buffer)
    {
        if (_audioFloor.Pending is not { } floor)
            return buffer;

        var before = PcmAudioTrim.FramesBefore(buffer.PresentationTime, buffer.SampleRate, floor);
        if (before >= buffer.FrameCount)
        {
            buffer.Dispose();
            return null;
        }

        _audioFloor.Spend();
        if (before == 0)
            return buffer;
        try
        {
            return PcmAudioTrim.Cut(buffer, (int)before);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Where a run starts on one of the graph's sources: armed before the run launches, and spent
    /// by the first item at or past it, as <see cref="ClockSelectBuffer.Admit"/> spends the pacer's
    /// floor. Read and spent only by the source's own filter while the run is under way.
    /// </summary>
    private sealed class RunFloor
    {
        private long _ticks = long.MinValue;

        public void Arm(TimeSpan floor) =>
            Volatile.Write(ref _ticks, floor > TimeSpan.Zero ? floor.Ticks : long.MinValue);

        /// <summary>The floor, or null once it is spent or when the run has none.</summary>
        public TimeSpan? Pending =>
            Volatile.Read(ref _ticks) is var ticks && ticks != long.MinValue ? TimeSpan.FromTicks(ticks) : null;

        public void Spend() => Volatile.Write(ref _ticks, long.MinValue);

        /// <summary>Whether an item at <paramref name="timestamp"/> is delivered, spending the floor if so.</summary>
        public bool Admits(TimeSpan timestamp)
        {
            if (Pending is { } floor && timestamp < floor)
                return false;
            Spend();
            return true;
        }
    }

    /// <summary>
    /// The video path's one shape: source, then the pause gate, then the consumer's
    /// configurator, then the sink. <see cref="BuildGraph"/> and
    /// <see cref="VideoFrameBudget"/> both wire it here, so the budget is computed on the path
    /// that runs.
    /// </summary>
    private static void WireVideo(
        FrameFlow.Graph.Graph graph,
        SourceNode<IVideoFrame> source,
        OperatorNode<IVideoFrame, IVideoFrame> gate,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configurator,
        SinkNode<IVideoFrame> sink
    )
    {
        var chain = graph.Pipeline(source).Then(gate);
        if (configurator is not null)
            chain = configurator(chain);
        chain.To(sink);
    }

    /// <summary>
    /// The frame budget of a session's video path over <paramref name="sink"/> (ADR-0081,
    /// decision 3): the most decoded frames the path can hold at once, or the node that leaves
    /// it unbounded.
    /// </summary>
    /// <remarks>
    /// Computed on a copy of the path with a stand-in source and the pacer's declaration, so it
    /// is known before the decoder opens and can size the decoder's pool. The configurator runs
    /// once more to build the copy, as it does for every graph a seek rebuilds, and its contract
    /// is to wire the same path each time.
    /// </remarks>
    internal static FrameBudget VideoFrameBudget(
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configurator,
        IVideoSink sink
    )
    {
        var (graph, source) = VideoProbe(configurator, sink);
        return graph!.FrameBudgetFor(source!);
    }

    /// <summary>
    /// A copy of a session's video path over <paramref name="sink"/>, with a stand-in source and
    /// the pacer's declaration, built before the decoder opens. Its budget sizes the decoder's
    /// pool, and once the decoder has opened it says whether a node on the path cannot take the
    /// memory domains the decoder hands out (#435).
    /// </summary>
    internal static (FrameFlow.Graph.Graph? Graph, OutputPort<IVideoFrame>? Source) VideoProbe(
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configurator,
        IVideoSink sink
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        var graph = new FrameFlow.Graph.Graph();
        var source = new SourceNode<IVideoFrame>("video-source", static _ => default);
        WireVideo(
            graph,
            source,
            new PausableGate<IVideoFrame>().AsOperator("video-gate"),
            configurator,
            new PacerDeclaration(sink).AsSinkNode("video-sink")
        );
        return (graph, source.Output);
    }

    /// <summary>
    /// Stands in for the pacer the session will build over a sink, for a budget computed before
    /// the pacer exists. It declares what the pacer will, and presents nothing.
    /// </summary>
    private sealed class PacerDeclaration(IVideoSink inner) : IVideoSink
    {
        public int? MaxHeldFrames => ClockSelectVideoSink.MaxHeldFramesOver(inner);

        public FrameMemoryDomains AcceptedDomains => inner.AcceptedDomains;

        public IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends => inner.PreferredBackends;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct) =>
            throw new InvalidOperationException("A budget's stand-in sink is never run.");

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Starts the demux-pump and graph-run tasks against the current <see cref="_graph"/>
    /// on a fresh session CTS. Split out of <see cref="StartSessionTasks"/> so the rewind
    /// path can re-launch over the retained graph without rebuilding the topology. When
    /// <see cref="_graph"/> is <see langword="null"/> (no stream to pump), fires end-of-stream
    /// directly — preserving the old empty-stream behavior.
    /// </summary>
    private void LaunchSessionTasks()
    {
        if (_pipeline is null)
            return;

        // One run at a time: the last run's tasks have ended.
        ThrowIfRunGoing();

        RetireSessionToken();
        _sessionCts = new CancellationTokenSource();
        var ct = _sessionCts.Token;

        Interlocked.Exchange(ref _eofFired, 0);

        InstallNewTimeline();

        var graph = _graph;
        if (graph is null)
        {
            _ = Task.Run(() => FireEndOfStreamAsync(), CancellationToken.None);
            return;
        }

        // ADR-0057 Stage 2: reset the select-by-clock pacer's per-run drain state so a
        // prior run's "drained" signal can't satisfy this run's EOS gate. Done here in the
        // per-launch path (not in BuildGraph) so a cheap RewindToStartAsync re-launch over
        // the retained graph re-arms it too — A1 (ClockSelectVideoSink) composing with B2
        // (cheap rewind).
        // A run that starts before the item's end stops there: the pump reads no further, and
        // the end filter drops what was decoded at or past it. A run that starts at or past the
        // end, after a seek, plays to the end of the source (media-clips decision 6).
        TimeSpan? runEnd = _itemEnd is { } end && _pendingSeekFloor < end ? end : null;
        Volatile.Write(ref _runEndTicks, runEnd?.Ticks ?? long.MaxValue);
        _pipeline.ReadUntil = runEnd;

        // The run starts at the seek floor. The sources refuse what comes before it, so a run
        // behind a closed gate decodes on to its start, and the pacer holds the same floor for the
        // destination frame it settles on.
        _videoFloor.Arm(_pendingSeekFloor);
        _audioFloor.Arm(_pendingSeekFloor);

        _videoPacer?.BeginRun(_pendingSeekFloor, _pendingSeekSettles);
        _pendingSeekFloor = TimeSpan.Zero;
        _pendingSeekSettles = false;

        // Demux pump task — feeds packets into decoder queues. Runs
        // until EOF or session cancel.
        var pumpTask = Task.Run(
            async () =>
            {
                try
                {
                    Take(Step.PumpStarted);
                    var reachedEof = false;
                    try
                    {
                        await _pipeline!.RunDemuxPumpAsync(ct).ConfigureAwait(false);
                        reachedEof = !ct.IsCancellationRequested;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        ReportWorkerFault(ex);
                        return;
                    }

                    if (reachedEof)
                    {
                        // The flush marker waits for space in a decoder's queue. Once the run
                        // is cancelled the graph stops draining it, so a full queue never frees
                        // up, and a stop waiting for this pump would wait for good: pass the
                        // run's token so the flush gives up. The queues are completed either way.
                        try
                        {
                            await FinalizeDecodersAsync(ct).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
                finally
                {
                    Take(Step.PumpEnded);
                }
            },
            CancellationToken.None
        );

        // The walk sets the video decoder's readback and discard level, so it is held with the
        // run's other tasks: a reposition resets that decoder and the dispose frees it.
        Task? recoveryTask = null;
        if (LatenessRecovery is { Enabled: true } recovery)
        {
            recoveryTask = Task.Run(
                async () =>
                {
                    try
                    {
                        Take(Step.RecoveryStarted);
                        await RunRecoveryWalkAsync(recovery, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Take(Step.RecoveryEnded);
                    }
                },
                CancellationToken.None
            );
        }

        var graphTask = Task.Run(
            async () =>
            {
                try
                {
                    Take(Step.GraphStarted);
                    await graph.RunAsync(ct).ConfigureAwait(false);

                    // ADR-0057 Stage 2: the graph forwards frames to the
                    // select-by-clock pacer at DECODE rate, so graph completion
                    // does NOT mean the clip finished PLAYING. Gate EOS on the
                    // pacer draining its buffer at clock cadence — otherwise a
                    // video-only clip (no audio pump to gate the graph) would
                    // signal Ended early and a RepeatMode.One loop would fire
                    // every few ms. (With audio, the audio sink pump already
                    // holds the graph to real-time; this also covers no-audio.)
                    if (!ct.IsCancellationRequested && _videoPacer is not null)
                    {
                        _videoPacer.SignalInputComplete();
                        await _videoPacer.WaitForDrainAsync(ct).ConfigureAwait(false);
                    }

                    // Graph completion means the audio sink has been handed the last buffer, not
                    // that it has been heard: an OpenAL sink still has up to a pool of buffers
                    // queued and a partial block staged, and whoever acts on the end of stream
                    // deactivates it (#609). With no video there is no pacer above to wait out,
                    // so an audio-only item ended about a second early and lost that second.
                    // Every item with audio waits, so ended means heard; with video the pacer has
                    // already held this to the audio clock, and the wait is the audio's tail.
                    if (!ct.IsCancellationRequested && _hasAudio)
                        await _audioSink!.DrainAsync(ct).ConfigureAwait(false);

                    if (!ct.IsCancellationRequested && Interlocked.Exchange(ref _eofFired, 1) == 0)
                    {
                        _callbacks.OnEndOfStream();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    ReportWorkerFault(ex);
                }
                finally
                {
                    Take(Step.GraphEnded);
                }
            },
            CancellationToken.None
        );

        _runTasks = RunTasks<Task>.Launched(pumpTask, graphTask, recoveryTask);
    }

    /// <summary>
    /// Gives the run about to launch a timeline of its own, and has the decoders stamp it on
    /// everything they decode from now on (ADR-0084, decision 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by <see cref="LaunchSessionTasks"/> and nowhere else, so every run gets a new id: an
    /// item's first run at warm-up, which its first play opens the gates on rather than replacing,
    /// every relaunch after <see cref="RepositionAsync"/>, a prepared runtime's run at its
    /// warm-up, and the first run of every new session.
    /// </para>
    /// <para>
    /// The place is the guarantee. A launch follows the previous run's stop and, after a break,
    /// the <c>ResetForSeek</c> pass that drops every packet read before it and flushes the codecs
    /// (<see cref="RepositionAsync"/> steps 2 and 5), and it precedes the tasks that decode. So
    /// nothing from before the break can come out under the new id, and nothing from the new run
    /// under the old one. Starting the tasks publishes the id to the decode threads.
    /// </para>
    /// </remarks>
    private void InstallNewTimeline()
    {
        var timeline = TimelineId.Next();
        if (_videoDecoder is { } video)
            video.Timeline = timeline;
        if (_audioDecoder is { } audio)
            audio.Timeline = timeline;
        _logger.LogDebug(RunLaunchedMessage, RunNumber, timeline);
    }

    /// <summary>
    /// The Debug line <see cref="InstallNewTimeline"/> writes for each launch, with the run number
    /// and the timeline it launches on as its two arguments. The integration tests read launches
    /// from it.
    /// </summary>
    internal const string RunLaunchedMessage = "Run {RunNumber} launches on {Timeline}.";

    /// <summary>
    /// Reports a fault in the pump, the graph or the video pacer: a sink that keeps throwing, or a
    /// fault in the pacer's own delivery (#703). The shared hardware device this item decoded on
    /// is dropped first, a sink's fault included: a fault can mean the device is no longer usable,
    /// after a driver reset for instance, and the next item then creates a new one rather than
    /// failing on it too.
    /// </summary>
    /// <remarks>
    /// The fault is reported even when dropping the device throws. Whatever called this has
    /// stopped, a halted pacer included, and only the report ends the item; a drop that threw
    /// before it would leave the item playing a picture that no longer moves.
    /// </remarks>
    private void ReportWorkerFault(Exception ex)
    {
        try
        {
            if (_videoDecoder?.BoundBackend is { } backend)
                DeviceCache?.Drop(backend);
        }
        catch (Exception dropFailure)
        {
            _logger.LogWarning(
                dropFailure,
                "Dropping the shared decode device after a fault threw; the fault is reported regardless."
            );
        }
        _callbacks.OnWorkerFaulted(ex);
    }

    /// <summary>
    /// Cancels the run, unless it is cancelled already, and waits for its tasks to end. See
    /// <see cref="WaitForSessionTasksAsync"/> for a wait that <paramref name="cancellationToken"/>
    /// cuts short.
    /// </summary>
    private async ValueTask StopGraphAsync(CancellationToken cancellationToken)
    {
        var cts = _sessionCts;
        if (cts is null || cts.IsCancellationRequested)
        {
            await WaitForSessionTasksAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException) { }

        await WaitForSessionTasksAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops only the demux pump (leaving the graph running). Used by
    /// <see cref="SeekAsync"/> to halt packet flow into the decoders
    /// before draining their queues. Today this still tears the demux
    /// pump task down because <see cref="DecodingPipeline.RunDemuxPumpAsync"/>
    /// uses a single CT; a future refinement would give each task its
    /// own CT for true per-subgraph cancellation.
    /// </summary>
    private async ValueTask StopDemuxPumpAsync(CancellationToken cancellationToken)
    {
        // For now this is equivalent to StopGraphAsync because the
        // session CT covers both tasks. Differentiation lands once
        // per-task cancellation is exposed.
        await StopGraphAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the run's tasks to end, and forgets them once they have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wait that <paramref name="cancellationToken"/> cuts short forgets only the tasks that have
    /// ended, and throws <see cref="OperationCanceledException"/>. The rest stay held, so the next
    /// stop waits for them: a seek that supersedes this one before it seeks the demuxer or resets
    /// the decoders, and the dispose before it frees them (#707). Scrubbing cancels seeks in this
    /// wait routinely, since a scrub sends the next seek as soon as the last is accepted.
    /// </para>
    /// <para>
    /// It throws rather than returning, so a caller cannot go on as though the run had ended. A
    /// reposition would otherwise advance <see cref="RunNumber"/> past a run that can still raise
    /// its end-of-stream, and touch the demuxer beside its pump. A task that faulted has ended,
    /// and its fault has been reported through the callbacks.
    /// </para>
    /// </remarks>
    private async ValueTask WaitForSessionTasksAsync(CancellationToken cancellationToken)
    {
        if (_runTasks.IsEmpty)
            return;

        Take(Step.WaitingForRun);
        try
        {
            await Task.WhenAll(_runTasks.Held).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cut short by the token, or a task faulted. Which tasks have ended decides.
        }

        _runTasks = _runTasks.ForgetEnded(HasEnded);
        if (!_runTasks.IsEmpty)
        {
            throw new OperationCanceledException(
                "The wait for the playback run's tasks was cancelled before they ended.",
                cancellationToken
            );
        }
    }

    private Task FireEndOfStreamAsync()
    {
        if (Interlocked.Exchange(ref _eofFired, 1) == 0)
            _callbacks.OnEndOfStream();
        return Task.CompletedTask;
    }
}
