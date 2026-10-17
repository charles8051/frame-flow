// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Decoding;
using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback.Core;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Playback;

/// <summary>
/// Opens an item's <see cref="SubstrateMedia"/> with the settings of the
/// <see cref="SubstrateSession"/> that plays it.
/// </summary>
internal sealed class SubstrateMediaOpener : IItemMediaOpener
{
    public IVideoSink? VideoSink { get; init; }

    public IAudioSink? AudioSink { get; init; }

    public HardwareDecodeMode HardwareMode { get; init; } = HardwareDecodeMode.Auto;

    // Null reaches the decoder, which resolves it to the process probe (#181).
    public FrameFlow.Media.HardwareDecodeCapabilities? HardwareCapabilities { get; init; }

    // Null: derived from the video path (#294).
    public bool? YieldHardwareFrames { get; init; }

    // Borrowed by the item's decoder; owned above the player, so it outlives the item.
    public HardwareDevice? HardwareDevice { get; init; }

    // The caller's order; empty defers to the sink's, then the platform's (#532).
    public IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends { get; init; } = [];

    public IReadOnlyList<string> ExcludedCodecs { get; init; } = [];

    // The software decoder's thread count; 0 chooses from the processor count (#788).
    public int DecoderThreads { get; init; }

    /// <inheritdoc cref="SubstrateSession.DeviceCache"/>
    public HardwareDeviceCache? DeviceCache { get; init; }

    public Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? VideoConfigurator { get; init; }

    public Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? AudioConfigurator { get; init; }

    /// <inheritdoc cref="SubstrateSession.LatenessRecovery"/>
    public LatenessRecoveryOptions? LatenessRecovery { get; init; }

    /// <inheritdoc cref="SubstrateSession.FormatAnnouncer"/>
    public VideoFormatAnnouncer? FormatAnnouncer { get; init; }

    /// <inheritdoc cref="SubstrateSession.SinkFailures"/>
    public VideoSinkFailures? SinkFailures { get; init; }

    /// <summary>The callbacks the pacer reports a starved or fed ring and a failed sink call to.</summary>
    public required SessionCallbacks Callbacks { get; init; }

    /// <summary>
    /// Where the pacer reports a fault in its own delivery (#703, #711): straight to the item's
    /// fault path, not through a run's work.
    /// </summary>
    public required Action<Exception> OnPacerFaulted { get; init; }

    /// <inheritdoc cref="SubstrateSession.OnFlushingDecoders"/>
    public Action<CancellationToken>? OnFlushingDecoders { get; init; }

    public required ILoggerFactory LoggerFactory { get; init; }

    public async ValueTask<IItemMedia> OpenAsync(IMediaSource source, CancellationToken cancellationToken) =>
        await SubstrateMedia.OpenAsync(source, this, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// The native objects one item plays on: the demux session, a decoder for each stream a sink
/// plays, the decoding pipeline, the select-by-clock pacer in front of the video sink, and the
/// graph, wired with <see cref="PausableGate{T}"/> operators for pause and resume.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pause and resume by gates, not by cancelling.</b> The first cut of the item runtime paused by
/// cancelling the graph and rebuilding it on resume, which faulted natively in FFmpeg's decoders: the
/// codec context was left unstable by a cancellation in the middle of a decode. The graph, the
/// decoders and the demux pump keep running across a pause instead. A gate sits between each
/// source and its sink, and a pause closes it, so frames pile up in the bounded edge channels
/// upstream of it. A resume opens it, and they drain.
/// </para>
/// <para>
/// <b>Pacing at delivery.</b> The graph's video sink node targets the pacer, whose
/// <c>PresentAsync</c> enqueues and returns at once, so the graph never holds a decode lease
/// across a clock wait (ADR-0057 Stage 2). The pacer selects the frame due on the master clock.
/// </para>
/// <para>
/// <b>Passive.</b> It starts no work of its own: <see cref="SubstrateSession"/> decides when each
/// member runs (<see cref="IItemMedia"/>).
/// </para>
/// </remarks>
internal sealed class SubstrateMedia : IItemMedia
{
    // Upper bound on a single video frame's pacing wait (PaceUntil defense-in-depth).
    // Orders of magnitude above any legitimate per-frame wait (~one frame interval) or
    // brief clock catch-up, so it only trips on a genuinely misaligned/stalled master
    // clock — turning what was a permanent presenter freeze into choppy-but-alive.
    private static readonly TimeSpan PaceWaitCap = TimeSpan.FromSeconds(5);

    private readonly SubstrateMediaOpener _settings;
    private readonly ILogger _logger;
    private readonly TimeSpan _duration;

    // Each is set until it is freed, and freed only once no work uses it.
    private IDemuxSession? _demux;
    private VideoDecoder? _videoDecoder;
    private AudioDecoder? _audioDecoder;
    private DecodingPipeline? _pipeline;

    // ── Presenter-side select-by-clock pacing (ADR-0057 Stage 2). ──
    // Replaces the in-graph PaceUntil operator. Built by OpenPacer (when there's a video sink)
    // around the video sink, bound to the item's master clock. The graph's video sink node targets
    // THIS, not the raw sink: its PresentAsync enqueues and returns at once, so the graph never
    // holds a decode lease across a clock wait (the held-lease → hwframe-pool-exhaustion coupling
    // the perf survey confirmed is gone). The decorator wraps but does not own the video sink.
    // Flushed on seek; freed at the teardown.
    private ClockSelectVideoSink? _pacer;

    // ADR-0056: the set of pre-seek-stateful participants, reset uniformly on the seek
    // path so the orchestrator can't apply a partial invalidation checklist. Built once
    // at the open alongside the components it covers.
    private readonly IReadOnlyList<ISeekResettable> _seekResettables;

    // ── Gates inserted into the graph for pause control. ──────────
    // Gates start CLOSED so frames don't drain before the first play opens them.
    private readonly PausableGate<IVideoFrame> _videoGate = new(initiallyOpen: false);
    private readonly PausableGate<PcmAudioBuffer> _audioGate = new(initiallyOpen: false);

    // The graph topology BeginRun built. A graph instance is re-runnable: RunAsync re-wires fresh
    // edge channels and re-pumps every node each call, and the decoder source adapters re-create a
    // fresh decode enumerator after EOS (DecoderSourceAdapters: enumerator nulls out on EOS, ??=
    // rebuilds it). A seek rebuilds a fresh graph; a rewind reuses this one to skip the per-loop
    // teardown and rebuild. Null until the first run, and for an item with no stream to play.
    private FrameFlow.Graph.Graph? _graph;

    // The end the current run stops at, in ticks, or long.MaxValue when it has none. Written only
    // before a run starts, and read by the end filter on the graph's sources as each frame or
    // buffer is decoded (media-clips decision 6).
    private long _runEndTicks = long.MaxValue;

    // Where the current run starts, for each of the graph's sources. The floor filter on each
    // releases what is presented before it as soon as it is decoded. Armed before a run starts.
    private readonly RunFloor _videoFloor = new();
    private readonly RunFloor _audioFloor = new();

    private SubstrateMedia(
        SubstrateMediaOpener settings,
        IDemuxSession demux,
        VideoDecoder? videoDecoder,
        AudioDecoder? audioDecoder,
        DecodingPipeline pipeline
    )
    {
        _settings = settings;
        _logger = settings.LoggerFactory.CreateLogger<SubstrateSession>();
        _demux = demux;
        _videoDecoder = videoDecoder;
        _audioDecoder = audioDecoder;
        _pipeline = pipeline;
        _duration = demux.MediaInfo?.Duration ?? TimeSpan.Zero;

        // ADR-0056: register every pre-seek-stateful participant once, here, next to where they're
        // constructed. Order matches the historical seek discipline (decoders before the
        // pipeline's pending-packet discard).
        var resettables = new List<ISeekResettable>(3);
        if (videoDecoder is not null)
            resettables.Add(videoDecoder);
        if (audioDecoder is not null)
            resettables.Add(audioDecoder);
        resettables.Add(pipeline);
        _seekResettables = resettables;
    }

    /// <inheritdoc/>
    public ItemResources Resources =>
        (_demux is null ? ItemResources.None : ItemResources.Demux)
        | (_videoDecoder is null ? ItemResources.None : ItemResources.VideoDecoder)
        | (_audioDecoder is null ? ItemResources.None : ItemResources.AudioDecoder)
        | (_pipeline is null ? ItemResources.None : ItemResources.Pipeline)
        | (_pacer is null ? ItemResources.None : ItemResources.Pacer);

    /// <inheritdoc/>
    public MediaInfo? MediaInfo => _demux?.MediaInfo;

    /// <inheritdoc/>
    public TimeSpan Duration => _duration;

    /// <inheritdoc/>
    public Task? FirstFrameDecoded => _videoDecoder?.FirstFrameDecoded;

    /// <inheritdoc/>
    public HardwareDecodeBackendKind? BoundBackend => _videoDecoder?.BoundBackend;

    // Whether the item has audio to play: a decoder, which is made only for a sink.
    private bool HasAudio => _audioDecoder is not null && _settings.AudioSink is not null;

    /// <summary>
    /// Opens <paramref name="source"/>: the demux session, a decoder for each stream a sink plays,
    /// and the pipeline. One that fails frees what it allocated, the pipeline included, in reverse
    /// allocation order, and leaves nothing behind (#725).
    /// </summary>
    internal static async ValueTask<SubstrateMedia> OpenAsync(
        IMediaSource source,
        SubstrateMediaOpener settings,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var loggerFactory = settings.LoggerFactory;
        var logger = loggerFactory.CreateLogger<SubstrateSession>();
        var demuxFactory = new DemuxSessionFactory(loggerFactory);
        var videoSink = settings.VideoSink;
        var audioSink = settings.AudioSink;

        IDemuxSession? demux = null;
        VideoDecoder? videoDecoder = null;
        AudioDecoder? audioDecoder = null;
        DecodingPipeline? pipeline = null;

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
            var videoHasConsumer = videoSink is not null;
            var audioHasConsumer = audioSink is not null;

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
                var hardwareDisabled = settings.HardwareMode == HardwareDecodeMode.Disabled;
                var videoProbe = HardwareFrameChoice.NeedsProbe(settings.YieldHardwareFrames, hardwareDisabled)
                    ? VideoProbe(settings.VideoConfigurator, videoSink!)
                    : default;
                var pathBudget = videoProbe.Graph?.FrameBudgetFor(videoProbe.Source!);

                // Frames stay on the GPU only in a domain the whole path takes, and which domain
                // they are in depends on the backend the decoder binds (#566). Before it opens,
                // the decision is whether any GPU domain gets through.
                var gpuAccepted = settings.YieldHardwareFrames is null && videoProbe.Graph is { } probeGraph
                    ? probeGraph.FrameDomainsAcceptedFrom(videoProbe.Source!, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly)
                    : FrameMemoryDomains.None;
                var hardwareFrames = HardwareFrameChoice.Decide(
                    settings.YieldHardwareFrames,
                    hardwareDisabled,
                    settings.YieldHardwareFrames is null && gpuAccepted == FrameMemoryDomains.None
                        ? videoProbe.Graph?.FrameDomainMismatchFor(
                            videoProbe.Source!, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly)
                        : null,
                    pathBudget);

                var backendOrder = DecodeBackendOrder.Decide(
                    settings.PreferredBackends,
                    videoSink!.PreferredBackends,
                    hardwareFrames.Yield,
                    settings.HardwareDevice?.Backend);
                if (!hardwareDisabled && !backendOrder.IsDefault)
                {
                    logger.LogInformation("Hardware decode order: {Reason}.", backendOrder.Reason);
                }

                var videoBudget = hardwareFrames.Yield ? pathBudget : null;
                var videoFactory = DecoderFactories.CreateVideo(
                    new HardwareDecodeOptions
                    {
                        Mode = settings.HardwareMode,
                        PreferredBackends = backendOrder.Preferred,
                        ExcludedCodecs = settings.ExcludedCodecs,
                    },
                    settings.HardwareCapabilities,
                    loggerFactory,
                    new VideoDecoderOptions
                    {
                        HeldHardwareFrames = videoBudget?.Frames ?? 0,
                        Device = settings.HardwareDevice,
                        DeviceCache = settings.DeviceCache,
                        DecoderThreads = settings.DecoderThreads,
                    }
                );
                videoDecoder = videoFactory(demux) as VideoDecoder;
                if (videoDecoder?.BoundBackend is { } bound)
                {
                    hardwareFrames = HardwareFrameChoice.ForBackend(
                        hardwareFrames,
                        settings.YieldHardwareFrames,
                        bound,
                        hardwareFrames.Yield
                            ? videoProbe.Graph?.FrameDomainMismatchFor(
                                videoProbe.Source!, HardwareFrameDomains.Of(bound), FrameDomainRule.CpuOnly)
                            : null);
                }

                if (!hardwareDisabled)
                {
                    logger.LogInformation(
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
                audioDecoder = DecoderFactories.CreateAudio(loggerFactory)(demux) as AudioDecoder;
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

            // The media made below owns it from here, and the catch frees it if the media is not
            // made. CA2000 cannot follow it there.
#pragma warning disable CA2000
            pipeline = new DecodingPipeline(
                concreteDemux,
                videoDecoder,
                audioDecoder,
                loggerFactory.CreateLogger<DecodingPipeline>()
            );
#pragma warning restore CA2000

            return new SubstrateMedia(settings, demux, videoDecoder, audioDecoder, pipeline);
        }
        catch (Exception failure)
        {
            // What was allocated is freed in reverse order, and nothing of it is kept (#725). A
            // free that fails is kept with the open's own failure rather than dropped.
            var failures = new List<Exception> { failure };
            await FreeQuietlyAsync(pipeline, failures).ConfigureAwait(false);
            await FreeQuietlyAsync(audioDecoder, failures).ConfigureAwait(false);
            await FreeQuietlyAsync(videoDecoder, failures).ConfigureAwait(false);
            await FreeQuietlyAsync(demux, failures).ConfigureAwait(false);
            if (failures.Count > 1)
                throw new AggregateException("Opening the item failed, and so did freeing what it had opened.", failures);
            throw;
        }
    }

    private static async ValueTask FreeQuietlyAsync(IAsyncDisposable? resource, List<Exception> failures)
    {
        if (resource is null)
            return;
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    /// <inheritdoc/>
    public void OpenPacer(IClockSource master)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (_settings.VideoSink is not { } videoSink || _pacer is not null)
            return;

        var callbacks = _settings.Callbacks;
        var pacer = new ClockSelectVideoSink(
            videoSink,
            master,
            _settings.LoggerFactory.CreateLogger("FrameFlow.Playback.ClockSelect.Video"),
            maxWait: PaceWaitCap,
            formatAnnouncer: _settings.FormatAnnouncer,
            sinkFailures: _settings.SinkFailures,
            // A source that stops delivering starves the ring; the controller reports
            // Rebuffering until frames flow again (#547).
            onStarved: callbacks.OnBufferUnderrun,
            onFed: callbacks.OnBufferReady,
            // A sink that throws costs the frame, and the controller reports it on
            // ErrorOccurred, at most once every few seconds, and plays on. One that keeps
            // throwing faults the item, as a fault in the graph or the pump does, and so
            // does a fault in the pacer's own delivery (#703).
            onSinkFailed: callbacks.OnRecoverableError,
            onFaulted: _settings.OnPacerFaulted
        );

        // Seat the cap on the gate state this pacer is born into — a runtime prepared while
        // paused (a playlist advance mid-pause) must not arm the cap against a stopped clock.
        // OpenGates/CloseGates keep the pair in step from here on (#127).
        if (!_videoGate.IsOpen)
            pacer.Pause();
        _pacer = pacer;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The gates and the pacer's cap are one decision, so they move together here. The pacer
    /// knows only the clock, and a clock that is stopped because the gates are shut looks exactly
    /// like a stalled one — which is how a pause came to force-present the ring a frame at a time
    /// (#127).
    /// </remarks>
    public void OpenGates()
    {
        if (_videoDecoder is { } decoder)
            decoder.PoolWatchdogSuspended = false;
        _pacer?.Resume();
        _videoGate.Open();
        _audioGate.Open();
    }

    /// <inheritdoc/>
    public void CloseGates()
    {
        _videoGate.Close();
        _audioGate.Close();
        _pacer?.Pause();

        // Holders stop releasing while the gates are shut, so a decoder parked at its pool
        // budget is waiting on purpose (ADR-0081 decision 5).
        if (_videoDecoder is { } decoder)
            decoder.PoolWatchdogSuspended = true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The frames buffered in the pacer were decoded against the timeline before the break; the
    /// clock is about to be seated on the target, so one left there would present at the wrong
    /// moment.
    /// </remarks>
    public void FlushPacer() => _pacer?.Flush();

    /// <inheritdoc/>
    public void BeginRun(TimelineId timeline, ItemGraphPolicy graph, TimeSpan floor, TimeSpan? end, bool holdForSettle)
    {
        // A seek builds a fresh graph topology; the loop's rewind runs the one it has again,
        // which skips the per-loop teardown and rebuild on a looping item.
        if (graph == ItemGraphPolicy.Rebuild || _graph is null)
            _graph = BuildGraph();

        InstallTimeline(timeline);
        if (_graph is null)
            return;

        // A run that starts before the item's end stops there: the pump reads no further, and
        // the end filter drops what was decoded at or past it. A run that starts at or past the
        // end, after a seek, plays to the end of the source (media-clips decision 6). The core
        // decides which.
        Volatile.Write(ref _runEndTicks, end?.Ticks ?? long.MaxValue);
        _pipeline!.ReadUntil = end;

        // The run starts at the seek floor. The sources refuse what comes before it, so a run
        // behind a closed gate decodes on to its start, and the pacer holds the same floor for the
        // destination frame it settles on.
        _videoFloor.Arm(floor);
        _audioFloor.Arm(floor);

        // What the video floor refuses need not be decoded in full: up to the first frame at or
        // past the floor, the decoder skips the non-reference frames (#784).
        if (_videoDecoder is { } decoder)
            decoder.PrerollFloor = floor;

        // ADR-0057 Stage 2: reset the select-by-clock pacer's per-run drain state so a prior
        // run's "drained" signal can't satisfy this run's end of stream. Done for every run, so
        // a rewind over the retained graph re-arms it too.
        _pacer?.BeginRun(floor, holdForSettle);
    }

    /// <summary>
    /// Has the decoders stamp the timeline of the run about to start on everything they decode from
    /// now on (ADR-0084, decision 2). The session gave the run its timeline when it took the run's
    /// number.
    /// </summary>
    /// <remarks>
    /// The place is the guarantee. A run starts once the previous run's work has ended and, after
    /// a break, after the <c>ResetForSeek</c> pass that drops every packet read before it and
    /// flushes the codecs, and before the work that decodes. So nothing from before the break can
    /// come out under the new id, and nothing from the new run under the old one. Starting the
    /// work publishes the id to the decode threads.
    /// </remarks>
    private void InstallTimeline(TimelineId timeline)
    {
        if (_videoDecoder is { } video)
            video.Timeline = timeline;
        if (_audioDecoder is { } audio)
            audio.Timeline = timeline;
    }

    /// <inheritdoc/>
    public async Task RunPumpAsync(CancellationToken cancellationToken)
    {
        var pipeline = _pipeline!;
        await pipeline.RunDemuxPumpAsync(cancellationToken).ConfigureAwait(false);

        // The pump returns when it is cancelled too, and only an end it reached is flushed.
        cancellationToken.ThrowIfCancellationRequested();

        // The flush marker waits for space in a decoder's queue. Once the run is cancelled the
        // graph stops draining it, so a full queue never frees up, and a stop waiting for this
        // pump would wait for good: the flush takes the run's token, and gives up with the run.
        // The queues are completed either way.
        _settings.OnFlushingDecoders?.Invoke(cancellationToken);
        await pipeline.FinalizeDecodersAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc/>
    public async Task RunGraphAsync(CancellationToken cancellationToken)
    {
        await _graph!.RunAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // ADR-0057 Stage 2: the graph forwards frames to the select-by-clock pacer at DECODE
        // rate, so graph completion does NOT mean the clip finished PLAYING. The end of stream
        // waits for the pacer to drain its buffer at clock cadence — otherwise a video-only clip
        // (no audio pump to gate the graph) would signal Ended early and a RepeatMode.One loop
        // would fire every few ms. (With audio, the audio sink pump already holds the graph to
        // real-time; this also covers no-audio.)
        if (_pacer is { } pacer)
        {
            pacer.SignalInputComplete();
            await pacer.WaitForDrainAsync(cancellationToken).ConfigureAwait(false);
        }

        // Graph completion means the audio sink has been handed the last buffer, not that it has
        // been heard: an OpenAL sink still has up to a pool of buffers queued and a partial block
        // staged, and whoever acts on the end of stream deactivates it (#609). With no video there
        // is no pacer above to wait out, so an audio-only item ended about a second early and lost
        // that second. Every item with audio waits, so ended means heard; with video the pacer
        // has already held this to the audio clock, and the wait is the audio's tail.
        if (HasAudio)
            await _settings.AudioSink!.DrainAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Walks <see cref="LatenessRecoveryPolicy.Path"/> from measured lateness, one settle window
    /// at a time, applying each rung to the decoder. It lives in the playback layer rather than in
    /// the decoder because ADR-0003 puts timing policy here, and the item already holds both
    /// halves — the pacer that measures lateness and the decoder that acts on it.
    /// </para>
    /// <para>
    /// One window of lateness is compared against the window before it, so the judgement is "did
    /// this rung help", not "is the pipeline late". That is the whole selection rule, and it is
    /// the thing the ADR's third acceptance condition is about.
    /// </para>
    /// <para>
    /// A fault ends the walk, and the runtime reports it as a recoverable error: the walk is an
    /// optimisation, and playback carries on at whatever rung it had reached.
    /// </para>
    /// </remarks>
    public async Task RunRecoveryWalkAsync(CancellationToken cancellationToken)
    {
        var options = _settings.LatenessRecovery
            ?? throw new InvalidOperationException("A lateness-recovery walk runs only when it is enabled.");
        var step = 0;
        TimeSpan? lagAtWindowStart = null;
        var recoveredFor = TimeSpan.Zero;
        var lastTick = Stopwatch.GetTimestamp();

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
            if (_pacer?.PresentationLag is not { } lag)
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

        void ApplyStep(int index)
        {
            if (_videoDecoder is not { } decoder)
                return;
            var rung = LatenessRecoveryPolicy.Path[index];
            decoder.ReadbackEveryN = rung.ReadbackEveryN;
            decoder.SetDiscardLevel(rung.Discard);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// For a target of 0 the BACKWARD flag lands at the file start: a true rewind to the start.
    /// </remarks>
    public ValueTask SeekAsync(TimeSpan target, CancellationToken cancellationToken) =>
        _demux!.SeekAsync(target, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Every participant that holds state from before the break folds its whole seek reset into
    /// <c>ResetForSeek</c>: the decoders replace their packet queue and flush the codec, and the
    /// pipeline drops the pump's retained pre-seek packet, which belongs to the timeline before
    /// the seek and would otherwise become the stale-PTS head of the stream after it, anchoring
    /// the master clock behind the target. Registering the participants in one list mechanises
    /// the ADR-0048 seek-discipline audit: the four historical seek-state-leak bugs all came from
    /// a missing step in a hand-listed checklist.
    /// </remarks>
    public void ResetForSeek()
    {
        foreach (var resettable in _seekResettables)
            resettable.ResetForSeek();
    }

    /// <inheritdoc/>
    public long PacerRun => _pacer?.CurrentRunId ?? 0;

    /// <inheritdoc/>
    public Task<TimeSpan?> WaitForSeekTargetAsync(long run, TimeSpan cap, CancellationToken cancellationToken) =>
        _pacer is { } pacer
            ? pacer.WaitForSeekTargetAsync(run, cap, cancellationToken)
            : Task.FromResult<TimeSpan?>(null);

    /// <inheritdoc/>
    public void ReleaseSettle(long run) => _pacer?.ReleaseSeekSettle(run);

    /// <inheritdoc/>
    /// <remarks>
    /// The pacer's dispose stops its delivery loop and releases the frames it buffers; it does
    /// not dispose the video sink, which the item uses and does not own (ADR-0044). The pipeline
    /// frees only the packet the pump retained; the decoders and the demux session are freed on
    /// their own.
    /// </remarks>
    public async ValueTask FreeAsync(ItemResources resource)
    {
        IAsyncDisposable? freed;
        switch (resource)
        {
            case ItemResources.Pacer:
                freed = _pacer;
                _pacer = null;
                break;
            case ItemResources.Pipeline:
                freed = _pipeline;
                _pipeline = null;
                break;
            case ItemResources.AudioDecoder:
                freed = _audioDecoder;
                _audioDecoder = null;
                break;
            case ItemResources.VideoDecoder:
                freed = _videoDecoder;
                _videoDecoder = null;
                break;
            case ItemResources.Demux:
                freed = _demux;
                _demux = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(resource), resource, "The media frees one of its own resources at a time.");
        }

        if (freed is not null)
            await freed.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public DecodedMediaStreamDiagnosticsSnapshot StreamDiagnostics
    {
        get
        {
            if (_demux is not { } demux)
                return DecodedMediaStreamDiagnosticsSnapshot.Empty;

            return new DecodedMediaStreamDiagnosticsSnapshot(
                Demux: demux.GetDiagnostics(),
                VideoDecoder: _videoDecoder?.GetDiagnostics() ?? VideoDecoderDiagnosticsSnapshot.Empty,
                AudioDecoder: _audioDecoder?.GetDiagnostics() ?? AudioDecoderDiagnosticsSnapshot.Empty,
                VideoChannelDepth: 0,
                AudioChannelDepth: 0
            );
        }
    }

    /// <inheritdoc/>
    public int VideoFramesDroppedLate => _pacer?.DroppedLate ?? 0;

    /// <inheritdoc/>
    public TimeSpan? VideoPresentationLag => _pacer?.PresentationLag;

    // ── The graph ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the run's graph topology (source → gate → consumer chain → sink) and returns it, or
    /// <see langword="null"/> when there is no stream to pump. Pure topology construction: it
    /// starts nothing.
    /// </summary>
    private FrameFlow.Graph.Graph? BuildGraph()
    {
        var hasVideo = _videoDecoder is not null && _settings.VideoSink is not null;
        var hasAudio = HasAudio;

        if (!hasVideo && !hasAudio)
        {
            // No stream to pump: the run is only its end of stream.
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
        // <b>single-sink</b> path the pacer is now <see cref="ClockSelectVideoSink"/>:
        // frames arrive at decode rate, the graph sink-pump
        // releases its reference to each frame immediately (no held lease), and the
        // decorator selects the frame due "now" on the clock at delivery time,
        // dropping late ones. So no clock wait happens inside the graph.
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
                _settings.VideoConfigurator,
                (_pacer ?? (IVideoSink)_settings.VideoSink!).AsSinkNode("video-sink")
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
            if (_settings.AudioConfigurator is not null)
                chain = _settings.AudioConfigurator(chain);
            chain.To(_settings.AudioSink!.AsSinkNode("audio-sink"));
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
    /// Where a run starts on one of the graph's sources: armed before the run starts, and spent
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
    /// The frame budget of an item's video path over <paramref name="sink"/> (ADR-0081,
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
    /// A copy of an item's video path over <paramref name="sink"/>, with a stand-in source and
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
    /// Stands in for the pacer an item will build over a sink, for a budget computed before
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
}
