// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Decoding;
using FrameFlow.Media.Diagnostics;
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
internal sealed class SubstrateSession : IPlaybackSession
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
        GraphChain<VideoFrameRef>,
        GraphChain<VideoFrameRef>
    >? _videoConfigurator;
    private readonly Func<
        GraphChain<PcmAudioBufferRef>,
        GraphChain<PcmAudioBufferRef>
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
    private readonly PausableGate<VideoFrameRef> _videoGate = new(initiallyOpen: false);
    private readonly PausableGate<PcmAudioBufferRef> _audioGate = new(initiallyOpen: false);

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
    private Task? _pumpTask;
    private Task? _graphTask;
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
    private bool _disposed;
    private readonly FrameFlow.Media.HardwareDecodeCapabilities _hwCapabilities;
    private readonly bool _yieldHardwareFrames;

    public SubstrateSession(
        IVideoSink? videoSink,
        IAudioSink? audioSink,
        IPlaybackClock clock,
        SessionCallbacks callbacks,
        HardwareDecodeMode hwMode = HardwareDecodeMode.Auto,
        FrameFlow.Media.HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? videoConfigurator = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? audioConfigurator = null,
        bool yieldHardwareFrames = false
    )
    {
        ArgumentNullException.ThrowIfNull(clock);

        _videoSink = videoSink;
        _audioSink = audioSink;
        _clock = clock;
        _hwMode = hwMode;
        _hwCapabilities = hardwareDecodeCapabilities ?? FrameFlow.Media.HardwareDecodeCapabilities.Empty;
        _yieldHardwareFrames = yieldHardwareFrames;
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
        );
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

    public async ValueTask InitializeAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
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
            var videoHasConsumer = _videoSink is not null || _videoConfigurator is not null;
            var audioHasConsumer = _audioSink is not null || _audioConfigurator is not null;

            if (videoHasConsumer)
            {
                // Packet-queue depth is left at the VideoDecoderOptions default (512).
                // Block mode (ADR-0060) already paces the no-audio pump correctly, so a
                // smaller depth would only trim read-ahead (512 video packets ≈ 512
                // frames ≈ ~20 s at 25 fps); it is not needed for correctness and is not
                // applied here to avoid any production behavior change. The
                // VideoDecoderOptions.PacketQueueCapacity knob is available if a future,
                // separately-validated tuning pass wants a tighter no-audio queue.
                var videoFactory = DecoderFactories.CreateVideo(
                    new HardwareDecodeOptions { Mode = _hwMode },
                    _hwCapabilities,
                    _loggerFactory
                );
                videoDecoder = videoFactory(demux) as VideoDecoder;
                if (videoDecoder is not null)
                {
                    videoDecoder.YieldHardwareFrames = _yieldHardwareFrames;

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
                    maxWait: PaceWaitCap
                );
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
        // Idempotent: re-entry while tasks are already running is a no-op
        // beyond the first-frame await below.
        if (
            _pumpTask is null
            || _pumpTask.IsCompleted
            || _graphTask is null
            || _graphTask.IsCompleted
        )
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
        if (_pumpTask is not null)
            watch.Add(_pumpTask);
        if (_graphTask is not null)
            watch.Add(_graphTask);

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

        if (!_renderersActivated)
        {
            // First play: activate sinks, start clock, open the gates so
            // frames flow. Session tasks were normally started by
            // WarmUpAsync during the controller's InitialBuffering state,
            // so the graph is already running and the first frame is
            // sitting upstream of the video gate ready to render.
            //
            // <b>Null-only restart guard.</b> If a caller skipped warmup
            // entirely (e.g. a test that bypasses the state machine),
            // _pumpTask is still null and we have to start the graph
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
            // boundary. The seek path is unaffected: it seeds its own baseline
            // before this branch runs (and reactivates outside PlayAsync's
            // first-play branch entirely), so this never overrides a seek target.
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
            _ownedClockSource?.Start();

            if (_pumpTask is null || _graphTask is null)
            {
                StartSessionTasks();
            }

            _videoGate.Open();
            _audioGate.Open();

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
        // Rebuilding on _pumpTask.IsCompleted would instead restart the demux
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

        _videoGate.Open();
        _audioGate.Open();

        if (_hasAudio)
            await _audioSink!.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

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

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        // Pause the audio sink first so the user hears silence
        // immediately. Then close the gates — frames already in the
        // sink-side channel may drain (one frame each), but no new
        // frames pass the gate until resume.
        if (_hasAudio)
            await _audioSink!.PauseAsync(cancellationToken).ConfigureAwait(false);

        _videoGate.Close();
        _audioGate.Close();

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
    public ValueTask RewindToStartAsync(CancellationToken cancellationToken = default)
    {
        if (_pipeline is null || _demux is null)
            return ValueTask.CompletedTask;

        // If the graph was never built (e.g. a session that never reached
        // StartSessionTasks), there is nothing to reuse — fall back to the full seek
        // (Rebuild + Cancel), which builds/initialises whatever is missing.
        if (_graph is null)
            return RepositionAsync(
                TimeSpan.Zero,
                GraphPolicy.Rebuild,
                TaskPolicy.Cancel,
                cancellationToken
            );

        // A RepeatMode.One loop boundary is reached at natural EOS, so nothing is in
        // flight: settle the already-finished tasks WITHOUT cancelling
        // (TaskPolicy.AwaitClean) and re-run the retained graph (GraphPolicy.Reuse),
        // skipping the per-loop CTS-cancel and topology rebuild.
        return RepositionAsync(
            TimeSpan.Zero,
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
        /// non-EOS safety path).
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

        var wasPaused = _clock.IsPaused;

        // Supersede any floor a previous discontinuity recorded but never launched: this
        // reposition is now the one that decides where playback resumes.
        _pendingSeekFloor = TimeSpan.Zero;
        _pendingSeekSettles = false;

        // Step 1: close gates so no frames forward to sinks during the reposition, and
        // pause audio so we don't hear the in-flight pre-discontinuity audio buffer.
        // (At a clean EOS loop boundary the gates are open and the sinks have drained;
        // closing here is defensive symmetry that also covers the AwaitClean safety path.)
        _videoGate.Close();
        _audioGate.Close();

        if (_hasAudio)
            await _audioSink!.PauseAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: settle the pump + graph tasks. Safe regardless of policy because the
        // gates are already closed — no in-flight decode the stop could interrupt mid-call.
        //   • Cancel (seek): always cancel the session CTS and wait — a user seek can land
        //     while the graph is actively running.
        //   • AwaitClean (rewind): at EOS the tasks have already completed, so just await
        //     them with NO CTS cancel (the whole point — avoids the per-loop graph teardown
        //     and the cancel-mid-GPU-op wedge). Fall back to the cancel path only if a task
        //     is somehow still running.
        // Both StopGraphAsync and WaitForSessionTasksAsync null _pumpTask/_graphTask.
        if (taskPolicy == TaskPolicy.Cancel)
        {
            await StopGraphAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var pumpRunning = _pumpTask is { IsCompleted: false };
            var graphRunning = _graphTask is { IsCompleted: false };
            if (pumpRunning || graphRunning)
                await StopGraphAsync(cancellationToken).ConfigureAwait(false);
            else
                await WaitForSessionTasksAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2b (ADR-0057 Stage 2): drop any frames buffered in the select-by-clock pacer.
        // They were decoded against the pre-discontinuity timeline; the clock is about to
        // rebase to the target, so a lingering pre-discontinuity frame would present at the
        // wrong moment. The graph is already stopped, so no new frame races this flush. (At a
        // clean EOS loop boundary the pacer is already drained, so this is a no-op there; it
        // matters on the AwaitClean safety path, where StopGraphAsync stopped a live run.)
        _videoPacer?.Flush();

        // Step 3: deactivate audio so we can reactivate fresh at the target position.
        if (_hasAudio)
            await _audioSink!.DeactivateAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 4: reposition the demuxer. For target 0 with the BACKWARD flag this lands at
        // the file start (a true rewind-to-start) — the same call the seek path makes.
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
        foreach (var resettable in _seekResettables)
        {
            resettable.ResetForSeek();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 6: reset the clock to the target position.
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

        _sessionCts?.Dispose();
        _sessionCts = null;

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
            // PlayAsync does that. The clock stays seeked + paused. (Reachable only via the
            // user-seek path; a loop rewind is fired from Playing with the clock running.)
            if (_hasAudio)
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

            _videoGate.Open();
            _audioGate.Open();

            if (_hasAudio)
                await _audioSink!.ResumeAsync(cancellationToken).ConfigureAwait(false);

            // Step 8: let the destination frame arrive, then put the clocks on it. This is
            // what makes the seek return once the target is actually showable rather than
            // once the pipeline has been told to go and get it.
            await SettleClocksOnSeekTargetAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        var failures = new List<Exception>();

        // Open the gates so any blocked operator bodies unblock and the
        // subsequent cancellation propagates through cleanly.
        _videoGate.Open();
        _audioGate.Open();

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

        if (_hasAudio)
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

        _clock.Stop();

        if (_pipeline is not null)
        {
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
        _graph = BuildGraph();
        LaunchSessionTasks();
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
        var hasVideoConfiguratorOnly =
            _videoDecoder is not null && _videoSink is null && _videoConfigurator is not null;
        var hasAudioConfiguratorOnly =
            _audioDecoder is not null && _audioSink is null && _audioConfigurator is not null;

        if (!hasVideo && !hasAudio && !hasVideoConfiguratorOnly && !hasAudioConfiguratorOnly)
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
        // releases each VideoFrameRef immediately (no held lease), and the
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

        if (_videoDecoder is not null && (hasVideo || hasVideoConfiguratorOnly))
        {
            var src = _videoDecoder.AsSourceNode("video-source");
            var gate = _videoGate.AsOperator("video-gate");

            if (hasVideo)
            {
                // Single-sink path: source → gate → (configurator) →
                // select-by-clock pacer wrapping the real sink. Pacing happens
                // in the decorator at delivery time, NOT in the graph, so the
                // graph holds no decode lease across a clock wait. Edges stay at
                // the substrate default (capacity=1 + Block); the decorator's
                // ring provides the read-ahead slack that keeps the hwframe pool
                // filled.
                var chain = graph.Pipeline(src).Then(gate);
                if (_videoConfigurator is not null)
                    chain = _videoConfigurator(chain);
                chain.To((_videoPacer ?? (IVideoSink)_videoSink!).AsSinkNode("video-sink"));
            }
            else
            {
                // Configurator-only path (no single sink to decorate): keep the
                // in-graph PaceUntil, upstream of the gate (so a frame forwarded
                // on the wait-cap is held by the closed gate, not leaked), then
                // hand the paced + gated chain to the configurator, which wires
                // its own sink(s). The substrate's node model can't express a
                // buffering clock-pump operator, so this path retains the prior
                // pacing shape; it was never the confirmed held-lease problem.
                var pace = PaceUntil.Create<VideoFrameRef>(
                    "video-pace",
                    _clockSource,
                    f => f.Frame.Pts,
                    _loggerFactory.CreateLogger("FrameFlow.Playback.PaceUntil.Video"),
                    maxWait: PaceWaitCap
                );
                var chain = graph.Pipeline(src).Then(pace).Then(gate);
                _videoConfigurator!(chain);
            }
        }

        if (_audioDecoder is not null && (hasAudio || hasAudioConfiguratorOnly))
        {
            var src = _audioDecoder.AsSourceNode("audio-source");
            var gate = _audioGate.AsOperator("audio-gate");

            // Audio doesn't need PaceUntil — the audio sink consumes
            // at realtime by virtue of feeding the device. The gate
            // still runs upstream of the configurator for symmetry +
            // so pause halts taps in configurator-only chains.
            var chain = graph.Pipeline(src).Then(gate);

            if (hasAudio)
            {
                if (_audioConfigurator is not null)
                    chain = _audioConfigurator(chain);
                chain.To(_audioSink!.AsSinkNode("audio-sink"));
            }
            else
            {
                _audioConfigurator!(chain);
            }
        }

        return graph;
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

        // Dispose any prior CTS (defensive).
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();
        var ct = _sessionCts.Token;

        Interlocked.Exchange(ref _eofFired, 0);

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
        _videoPacer?.BeginRun(_pendingSeekFloor, _pendingSeekSettles);
        _pendingSeekFloor = TimeSpan.Zero;
        _pendingSeekSettles = false;

        // Demux pump task — feeds packets into decoder queues. Runs
        // until EOF or session cancel.
        _pumpTask = Task.Run(
            async () =>
            {
                var reachedEof = false;
                try
                {
                    await _pipeline!.RunDemuxPumpAsync(ct).ConfigureAwait(false);
                    reachedEof = !ct.IsCancellationRequested;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _callbacks.OnWorkerFaulted(ex);
                    return;
                }

                if (reachedEof)
                {
                    try
                    {
                        await _pipeline!
                            .FinalizeDecodersAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch { }
                }
            },
            CancellationToken.None
        );

        _graphTask = Task.Run(
            async () =>
            {
                try
                {
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

                    if (!ct.IsCancellationRequested && Interlocked.Exchange(ref _eofFired, 1) == 0)
                    {
                        _callbacks.OnEndOfStream();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _callbacks.OnWorkerFaulted(ex);
                }
            },
            CancellationToken.None
        );
    }

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

    private async ValueTask WaitForSessionTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = new[] { _pumpTask, _graphTask }
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();

        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            _pumpTask = null;
            _graphTask = null;
        }
    }

    private Task FireEndOfStreamAsync()
    {
        if (Interlocked.Exchange(ref _eofFired, 1) == 0)
            _callbacks.OnEndOfStream();
        return Task.CompletedTask;
    }
}
