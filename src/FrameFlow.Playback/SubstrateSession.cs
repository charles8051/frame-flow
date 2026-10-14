// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
/// contract. Opens one item's <see cref="IItemMedia"/> (demux, decoders, decoding pipeline,
/// pacer, and a long-lived <see cref="Graph.Graph"/> wired with
/// <see cref="PausableGate{T}"/> operators) and sequences its runs: pause and resume through
/// the gates, seek and rewind through one reposition recipe, and the teardown.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pause/resume via gates, not cancel-rebuild.</b> The graph, the decoders and the demux pump
/// keep running across a pause; the gates hold the frames. See <see cref="SubstrateMedia"/>.
/// </para>
/// <para>
/// <b>Seek still rebuilds.</b> <see cref="SeekAsync"/> stops the run, waits for its work,
/// seeks the demuxer, resets the decoders, and starts a fresh run over a fresh graph.
/// </para>
/// </remarks>
internal sealed class SubstrateSession : IPlaylistItemRuntime
{
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly IPlaybackClock _clock;

    // The master pacing clock (IClockSource consumed by the pacer). Selected
    // per item at the open once the stream probe reveals whether THIS
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
    private IItemMedia? _media;
    private IMediaSource? _source;

    // Seek target awaiting the launch that will play from it, or Zero when none is pending.
    // Set once a reposition commits (RepositionAsync step 6) and consumed by the next
    // LaunchSessionTasks, which is not necessarily the same call: a seek taken while paused
    // relaunches on the following PlayAsync.
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

    // Whether the launch that consumes _pendingSeekFloor is one this session will follow
    // with a clock reseat. Only the reposition's play branch does.
    private bool _pendingSeekSettles;

    // Set at the open once we know whether the source has a decodable audio stream. Gates every
    // _audioSink lifecycle call so a video-only file doesn't spuriously activate (and later
    // deactivate) an audio sink that has nothing to play.
    private bool _hasAudio;

    // ── Long-lived per-session runtime ────────────────────────────
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
    /// Opens the item's media. Internal: a seam for tests, which drive the session over media of
    /// their own (ADR-0086, decision 6). Null opens <see cref="SubstrateMedia"/>.
    /// </summary>
    internal IItemMediaOpener? MediaOpener { get; init; }

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

    // Whether a run has built a graph a rewind can run again.
    private bool _graphBuilt;

    // ── EOF coordination ────────────────────────────────────────────
    private int _eofFired;

    // Numbers the runs of the graph; see RunNumber. Advanced by RepositionAsync once it has
    // stopped the run it interrupts, and before anything relaunches.
    private int _runNumber;
    private bool _disposed;
    private readonly FrameFlow.Media.HardwareDecodeCapabilities? _hwCapabilities;
    private readonly bool? _yieldHardwareFrames;
    private readonly HardwareDevice? _hardwareDevice;
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

        // Provisional master clock: a wallclock the session owns. The open upgrades this to the
        // audio sink's IClockSource iff this item actually has a decodable audio stream the sink
        // will play (the ADR-0003 audio-master rule, evaluated per item), and frees it then.
        _ownedClockSource = new WallClockSource();
        _clockSource = _ownedClockSource;
    }

    // ── IPlaybackSession read-only surface ──────────────────────────

    public MediaInfo? MediaInfo => _media?.MediaInfo;

    public TimeSpan Duration => _media?.Duration ?? TimeSpan.Zero;

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

    public PipelineDiagnosticsSnapshot GetPipelineDiagnostics()
    {
        var videoSink = _videoSink?.GetDiagnostics() ?? VideoSinkDiagnosticsSnapshot.Empty;
        var audioSink = _audioSink?.GetDiagnostics() ?? AudioSinkDiagnosticsSnapshot.Empty;
        var media = _media;
        return new PipelineDiagnosticsSnapshot(
            Stream: media?.StreamDiagnostics
                ?? FrameFlow.Decoding.Diagnostics.DecodedMediaStreamDiagnosticsSnapshot.Empty,
            VideoSink: videoSink,
            AudioSink: audioSink,
            // Now actually wired (ADR-0057 Stage 2): the select-by-clock pacer
            // counts frames dropped as late at delivery time.
            VideoFramesDroppedForSync: media?.VideoFramesDroppedLate ?? 0
        )
        {
            // Null without a pacer: there is no master clock to measure against, so
            // "no lag" would be a claim this session cannot make.
            VideoPresentationLag = media?.VideoPresentationLag,
        };
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
    /// start and records the pacer's floor, and starts nothing (#639).
    /// </para>
    /// <para>
    /// A start at or past a known end is refused. The item would open with nothing in it and end
    /// at once, and under <c>RepeatMode.One</c> it would then loop with no content.
    /// </para>
    /// </remarks>
    private ValueTask PositionAtStartAsync(TimeSpan start, CancellationToken cancellationToken)
    {
        ThrowIfPastTheEnd(start);
        return RepositionAsync(start, ItemGraphPolicy.Rebuild, TaskPolicy.Cancel, cancellationToken);
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

    /// <summary>
    /// Opens the item's media, then builds the pacer against the item's master clock, then, once
    /// nothing after it can fail, frees the provisional wallclock if the audio sink masters.
    /// </summary>
    /// <remarks>
    /// An open that fails frees everything it allocated, in reverse order, and leaves the session
    /// holding only the wallclock it was constructed with (#725).
    /// </remarks>
    private async ValueTask OpenAsync(IMediaSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        _source = source;
        var media = await Opener().OpenAsync(source, cancellationToken).ConfigureAwait(false);

        // Per-item master-clock selection (ADR-0003). The audio sink masters the clock only when
        // THIS item has a decodable audio stream the sink will play AND the sink can publish a
        // sample-counter clock. Otherwise the provisional wallclock remains the master.
        var hasAudio = (media.Resources & ItemResources.AudioDecoder) != 0 && _audioSink is not null;
        IClockSource master = hasAudio && _audioSink is IClockSource audioClock ? audioClock : _ownedClockSource!;
        try
        {
            media.OpenPacer(master);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var resource in new[] { ItemResources.Pipeline, ItemResources.AudioDecoder, ItemResources.VideoDecoder, ItemResources.Demux })
            {
                if ((media.Resources & resource) == 0)
                    continue;
                try
                {
                    await media.FreeAsync(resource).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
            if (failures.Count > 1)
                throw new AggregateException("Opening the item failed, and so did freeing what it had opened.", failures);
            throw;
        }

        _media = media;
        _hasAudio = hasAudio;
        if (!ReferenceEquals(master, _ownedClockSource) && _ownedClockSource is { } provisional)
        {
            _clockSource = master;
            _ownedClockSource = null;
            await provisional.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IItemMediaOpener Opener() =>
        MediaOpener
        ?? new SubstrateMediaOpener
        {
            VideoSink = _videoSink,
            AudioSink = _audioSink,
            HardwareMode = _hwMode,
            HardwareCapabilities = _hwCapabilities,
            YieldHardwareFrames = _yieldHardwareFrames,
            HardwareDevice = _hardwareDevice,
            PreferredBackends = _preferredBackends,
            ExcludedCodecs = _excludedCodecs,
            DeviceCache = DeviceCache,
            VideoConfigurator = _videoConfigurator,
            AudioConfigurator = _audioConfigurator,
            LatenessRecovery = LatenessRecovery,
            FormatAnnouncer = FormatAnnouncer,
            SinkFailures = SinkFailures,
            Callbacks = _callbacks,
            OnPacerFaulted = ReportWorkerFault,
            OnFlushingDecoders = token => OnFlushingDecoders?.Invoke(token),
            LoggerFactory = _loggerFactory,
        };

    private bool HasGraph =>
        _media is { } media && (media.Resources & (ItemResources.VideoDecoder | ItemResources.AudioDecoder)) != 0;

    public async ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_media is not { } media || _renderersActivated)
            return;

        // Audio-only sources skip warmup entirely: software audio decode has no meaningful
        // cold-start cost. The warmup window is specifically there to absorb hardware-video-
        // decoder cold-start latency; with no video, there's nothing to warm up.
        if (media.FirstFrameDecoded is not { } firstFrame)
            return;

        // Start the long-lived graph + demux pump now, while gates are still closed. Decoded
        // frames pile up in the bounded edge channels upstream of the gates; the hardware codec
        // session is set up and its expensive first decode is paid here. By the time PlayAsync
        // opens the gates, a frame is already waiting at t=0 and audio can't race ahead.
        //
        // Idempotent: re-entry while a task is still running is a no-op beyond the first-frame
        // await below. A run launched beside it would read the same demuxer and decode on the
        // same decoders.
        _runTasks = _runTasks.ForgetEnded(HasEnded);
        if (_runTasks.IsEmpty)
        {
            StartSessionTasks();
        }

        // Wait for either: the first frame to land, or the pump/graph to complete (typically
        // meaning a fault, or the rare edge case of an empty / sub-first-frame stream).
        var watch = new List<Task>(3) { firstFrame };
        if (_runTasks.Pump is { } pump)
            watch.Add(pump);
        if (_runTasks.Graph is { } graph)
            watch.Add(graph);

        var winner = await Task.WhenAny(watch)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (winner.IsFaulted)
        {
            await winner.ConfigureAwait(false);
        }
    }

    public async ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        if (_media is not { } media)
            return;

        // A seek cancelled before it committed, by a pause sent during it for instance, left the
        // run stopped, and neither branch below would start another (#714).
        await FinishCutShortRepositionAsync(cancellationToken).ConfigureAwait(false);

        if (!_renderersActivated)
        {
            // First play: activate sinks, start clock, open the gates so frames flow. Seat the
            // master pacing clock's origin to this item's start position deterministically,
            // before activating the audio sink.
            var itemOrigin = _clock.Position;
            (_clockSource as ISeekableClock)?.SeekBaseline(itemOrigin);

            _pendingSeekFloor = itemOrigin;
            _pendingSeekSettles = false;

            if (_hasAudio)
                await _audioSink!.ActivateAsync(cancellationToken).ConfigureAwait(false);

            _clock.Start(itemOrigin);

            // Start runs the wallclock from zero and discards the baseline seated above,
            // because the owned wallclock is the same object as _clockSource. It is seated
            // again once running (#639).
            if (_ownedClockSource is { } wallclock)
            {
                wallclock.Start();
                wallclock.Seek(itemOrigin);
            }

            if (_runTasks.IsEmpty)
            {
                StartSessionTasks();
            }

            media.OpenGates();

            _renderersActivated = true;
            return;
        }

        // Resume from pause. Only the seek-while-paused teardown sets _sessionCts to null; a
        // plain pause leaves the live session CT in place, and resume only reopens the gate.
        if (_sessionCts is null)
        {
            StartSessionTasks();
        }

        _clock.Resume();
        _ownedClockSource?.Resume();

        media.OpenGates();

        if (_hasAudio)
            await _audioSink!.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Repositions at the clock's position when a reposition was cancelled before it committed,
    /// so the play that follows launches a run instead of opening the gates on a stopped one.
    /// </summary>
    private ValueTask FinishCutShortRepositionAsync(CancellationToken cancellationToken) =>
        _sessionCts is { IsCancellationRequested: true }
            ? RepositionAsync(_clock.Position, ItemGraphPolicy.Rebuild, TaskPolicy.Cancel, cancellationToken)
            : ValueTask.CompletedTask;

    /// <summary>
    /// Upper bound on waiting for a post-seek run's destination frame before giving up and
    /// leaving the clocks where they are.
    /// </summary>
    private static readonly TimeSpan SeekSettleCap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reseats the clocks onto the first frame a post-seek run actually delivers, so the
    /// decode-forward that produced it does not count as playback time (#161).
    /// </summary>
    private async ValueTask SettleClocksOnSeekTargetAsync(CancellationToken cancellationToken)
    {
        // _ownedClockSource is non-null only when this session masters with a wallclock.
        if (_media is not { } media || (media.Resources & ItemResources.Pacer) == 0 || _ownedClockSource is null)
            return;

        // Captured before the wait, so a settle that finishes late releases the hold it
        // armed rather than one a newer run has since taken.
        var runId = media.PacerRun;
        try
        {
            var reached = await media.WaitForSeekTargetAsync(runId, SeekSettleCap, cancellationToken)
                .ConfigureAwait(false);

            if (reached is not { } pts)
                return;

            _clock.Seek(pts);
            _ownedClockSource.Seek(pts);
        }
        finally
        {
            media.ReleaseSettle(runId);
        }
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        if (_hasAudio)
            await _audioSink!.PauseAsync(cancellationToken).ConfigureAwait(false);

        _media?.CloseGates();

        _clock.Pause();
        _ownedClockSource?.Pause();
    }

    public ValueTask SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default
    ) =>
        RepositionAsync(position, ItemGraphPolicy.Rebuild, TaskPolicy.Cancel, cancellationToken);

    /// <inheritdoc/>
    public ValueTask RewindToStartAsync(CancellationToken cancellationToken = default) =>
        RewindToAsync(TimeSpan.Zero, _itemEnd, cancellationToken);

    /// <inheritdoc/>
    public ValueTask RewindToAsync(TimeSpan position, TimeSpan? end, CancellationToken cancellationToken)
    {
        if (_media is null)
            return ValueTask.CompletedTask;

        _itemEnd = end;

        if (position > TimeSpan.Zero)
            ThrowIfPastTheEnd(position);

        if (!_graphBuilt)
            return RepositionAsync(position, ItemGraphPolicy.Rebuild, TaskPolicy.Cancel, cancellationToken);

        return RepositionAsync(position, ItemGraphPolicy.Reuse, TaskPolicy.AwaitClean, cancellationToken);
    }

    /// <summary>How the running work is stopped in <see cref="RepositionAsync"/>.</summary>
    private enum TaskPolicy
    {
        /// <summary>Always cancel the session CTS and wait.</summary>
        Cancel,

        /// <summary>Await the (normally already-completed) tasks without cancelling.</summary>
        AwaitClean,
    }

    /// <summary>
    /// The single ordered discontinuity recipe shared by <see cref="SeekAsync"/> and the rewinds:
    /// close gates → pause audio → settle tasks → flush the pacer → deactivate audio → reposition
    /// the demuxer → uniform <c>ResetForSeek</c> pass → reseat both clocks → relaunch → reactivate
    /// + resume.
    /// </summary>
    private async ValueTask RepositionAsync(
        TimeSpan target,
        ItemGraphPolicy graphPolicy,
        TaskPolicy taskPolicy,
        CancellationToken cancellationToken
    )
    {
        if (_media is not { } media)
            return;

        var wasPaused = _clock.IsPaused || !_renderersActivated;

        _pendingSeekFloor = TimeSpan.Zero;
        _pendingSeekSettles = false;

        media.CloseGates();

        if (_hasAudio && _renderersActivated)
            await _audioSink!.PauseAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

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

        Interlocked.Increment(ref _runNumber);

        cancellationToken.ThrowIfCancellationRequested();

        media.FlushPacer();

        if (_hasAudio && _renderersActivated)
            await _audioSink!.DeactivateAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        Take(Step.SeekingDemux);
        await media.SeekAsync(target, cancellationToken).ConfigureAwait(false);

        Take(Step.ResettingForSeek);
        media.ResetForSeek();

        cancellationToken.ThrowIfCancellationRequested();

        if (!_attached)
        {
            _detachedOrigin = target;
            _ownedClockSource?.SeekBaseline(target);
        }
        else
        {
            _clock.Seek(target);
            (_clockSource as ISeekableClock)?.SeekBaseline(target);
        }

        _pendingSeekFloor = target;
        _pendingSeekSettles = !wasPaused && _ownedClockSource is not null;

        RetireSessionToken();

        if (graphPolicy == ItemGraphPolicy.Reuse)
            cancellationToken.ThrowIfCancellationRequested();

        if (wasPaused)
        {
            if (_hasAudio && _renderersActivated)
            {
                await _audioSink!.ActivateAsync(cancellationToken).ConfigureAwait(false);
                await _audioSink!.PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (_hasAudio)
                await _audioSink!.ActivateAsync(cancellationToken).ConfigureAwait(false);

            LaunchSessionTasks(graphPolicy);

            media.OpenGates();

            if (_hasAudio)
                await _audioSink!.ResumeAsync(cancellationToken).ConfigureAwait(false);

            await SettleClocksOnSeekTargetAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the run and waits for its tasks to end, then frees the pacer, the pump pipeline, the
    /// decoders and the demux session.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        var failures = new List<Exception>();

        if (_attached)
            _media?.OpenGates();

        try
        {
            await StopGraphAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        var media = _media;
        if (media is not null && (media.Resources & ItemResources.Pacer) != 0)
            await FreeAsync(media, ItemResources.Pacer, null, failures).ConfigureAwait(false);

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

        if (media is not null)
        {
            await FreeAsync(media, ItemResources.Pipeline, Step.DisposingPipeline, failures).ConfigureAwait(false);
            await FreeAsync(media, ItemResources.VideoDecoder, Step.DisposingVideoDecoder, failures).ConfigureAwait(false);
            await FreeAsync(media, ItemResources.AudioDecoder, Step.DisposingAudioDecoder, failures).ConfigureAwait(false);
            await FreeAsync(media, ItemResources.Demux, Step.DisposingDemux, failures).ConfigureAwait(false);
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

    private async ValueTask FreeAsync(IItemMedia media, ItemResources resource, Step? step, List<Exception> failures)
    {
        if ((media.Resources & resource) == 0)
            return;
        if (step is { } taken)
            Take(taken);
        try
        {
            await media.FreeAsync(resource).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    // ── Session-task lifecycle (single graph per session run) ───────

    private void StartSessionTasks()
    {
        if (_media is null)
            return;

        ThrowIfRunGoing();
        LaunchSessionTasks(ItemGraphPolicy.Rebuild);
    }

    /// <summary>
    /// Refuses a launch while a task of the last run has not ended (#707).
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
    /// Starts the demux-pump and graph-run tasks over the media's graph on a fresh session CTS.
    /// With no stream to pump, fires end-of-stream directly.
    /// </summary>
    private void LaunchSessionTasks(ItemGraphPolicy graphPolicy)
    {
        if (_media is not { } media)
            return;

        ThrowIfRunGoing();

        RetireSessionToken();
        _sessionCts = new CancellationTokenSource();
        var ct = _sessionCts.Token;

        Interlocked.Exchange(ref _eofFired, 0);

        TimeSpan? runEnd = _itemEnd is { } end && _pendingSeekFloor < end ? end : null;
        var timeline = media.BeginRun(graphPolicy, _pendingSeekFloor, runEnd, _pendingSeekSettles);
        _logger.LogDebug(RunLaunchedMessage, RunNumber, timeline);
        _pendingSeekFloor = TimeSpan.Zero;
        _pendingSeekSettles = false;

        if (!HasGraph)
        {
            _ = Task.Run(() => FireEndOfStreamAsync(), CancellationToken.None);
            return;
        }
        _graphBuilt = true;

        var pumpTask = Task.Run(
            async () =>
            {
                try
                {
                    Take(Step.PumpStarted);
                    await media.RunPumpAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    ReportWorkerFault(ex);
                }
                finally
                {
                    Take(Step.PumpEnded);
                }
            },
            CancellationToken.None
        );

        Task? recoveryTask = null;
        if (LatenessRecovery is { Enabled: true })
        {
            recoveryTask = Task.Run(
                async () =>
                {
                    try
                    {
                        Take(Step.RecoveryStarted);
                        await media.RunRecoveryWalkAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        _callbacks.OnRecoverableError(
                            new PlaybackError(
                                ErrorCategory.System,
                                $"Lateness recovery stopped after a fault: {ex.Message}",
                                ex
                            )
                        );
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
                    await media.RunGraphAsync(ct).ConfigureAwait(false);

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
    /// The Debug line each launch writes, with the run number and the timeline it launches on as
    /// its two arguments. The integration tests read launches from it.
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
            if (_media?.BoundBackend is { } backend)
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
    /// Cancels the run, unless it is cancelled already, and waits for its tasks to end.
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
    /// Waits for the run's tasks to end, and forgets them once they have. A wait that
    /// <paramref name="cancellationToken"/> cuts short forgets only the tasks that have ended,
    /// and throws <see cref="OperationCanceledException"/> (#707).
    /// </summary>
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
