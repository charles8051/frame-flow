// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Core;
using FrameFlow.Media.Diagnostics;
using FrameFlow.Playback.Core;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Input = FrameFlow.Playback.Core.ItemRuntimeInput;
using RuntimeAction = FrameFlow.Playback.Core.ItemRuntimeAction;

namespace FrameFlow.Playback;

/// <summary>
/// The item runtime: plays one playlist item's media on the playlist's shared sinks and position
/// clock. The shell of <see cref="ItemRuntimeProtocol"/> (ADR-0086, decision 2): the core decides
/// what happens and in what order, and this performs it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One input at a time.</b> Every command, every awaited action's outcome and every end of a
/// run's work is an input, queued and stepped one at a time by a single reader, never re-entrantly.
/// The reader performs a step's immediate actions in order, then starts its awaited action, if
/// any, with the command's token, and queues the action's outcome when it finishes. Nothing that
/// runs while an action is under way, a cancellation's callback included, steps the core: it
/// queues an input.
/// </para>
/// <para>
/// <b>What the shell holds.</b> The item's <see cref="IItemMedia"/> (the demux session, the
/// decoders, the pipeline, the pacer and the graph), a <see cref="WorkScope"/> for each run, the
/// clocks, and the commands its callers are waiting on. The core decides when a run starts, when it
/// is cancelled, and when anything is sought, reset or freed: only once no work of any run is
/// pending. The run number advances only then, so an end of stream a run raises carries its own.
/// </para>
/// <para>
/// <b>A run's work.</b> A run is a pump, a graph and, when it is enabled, a lateness-recovery walk,
/// or for an item with no stream to play only its end of stream, each started under the run's
/// scope and fed back as it ends: Completed, Cancelled on the run's cancellation, or Faulted. The
/// core reports each fault and raises the end of stream once, from those inputs.
/// </para>
/// <para>
/// <b>A caller's cancellation is the caller's.</b> It ends that caller's command, and the stop the
/// command was waiting for goes on. A command's task completes when the core completes the command,
/// never earlier on its token. A command whose token is cancelled when it is called does nothing,
/// and its task is cancelled.
/// </para>
/// <para>
/// <b>Pause and resume by gates.</b> The graph, the decoders and the demux pump keep running
/// across a pause, behind the gates (<see cref="SubstrateMedia"/>). A seek, a rewind, and a play
/// or a warm-up after a seek cut short, reposition: stop the run, wait for its work, seek, reset,
/// and start the next run over a fresh graph, or over the last one for a rewind. Superseding seeks
/// coalesce into one unwind and one seek.
/// </para>
/// </remarks>
internal sealed class SubstrateSession : IPlaylistItemRuntime
{
    /// <summary>
    /// Upper bound on waiting for a post-seek run's destination frame before giving up and
    /// leaving the clocks where they are.
    /// </summary>
    /// <remarks>
    /// The wait is the decoder walking from the keyframe to the seek target — tens of
    /// milliseconds on ordinary content, and 0.73 s measured on a 1080p60 file whose only
    /// keyframe is at 0.0. Five seconds is far past any of that, so this only trips when the
    /// frame is never coming: a target past the end of the stream, or a stalled decoder.
    /// </remarks>
    private static readonly TimeSpan SeekSettleCap = TimeSpan.FromSeconds(5);

    // The teardown's frees after the media's, in the order the core asks for them.
    private static readonly ItemResources[] MediaResources =
    [
        ItemResources.Pacer,
        ItemResources.Pipeline,
        ItemResources.AudioDecoder,
        ItemResources.VideoDecoder,
        ItemResources.Demux,
    ];

    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly IPlaybackClock _clock;
    private readonly SessionCallbacks _callbacks;
    private readonly HardwareDecodeMode _hwMode;
    private readonly Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? _videoConfigurator;
    private readonly Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? _audioConfigurator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly FrameFlow.Media.HardwareDecodeCapabilities? _hwCapabilities;
    private readonly bool? _yieldHardwareFrames;
    private readonly HardwareDevice? _hardwareDevice;
    private readonly IReadOnlyList<HardwareDecodeBackendKind> _preferredBackends;
    private readonly IReadOnlyList<string> _excludedCodecs;
    private int _decoderThreads;
    private ColorMatrix _colorMatrix;

    // The master pacing clock (IClockSource consumed by the pacer). Selected per item at the open,
    // once the stream probe reveals whether THIS item actually has a decodable audio stream the
    // sink will play — not in the constructor, where "an IClockSource audio sink is attached" is
    // the wrong question (a video-only item paced against an unactivated audio sink's frozen sample
    // counter would stall). _ownedClock is the wallclock the runtime was constructed with, until the
    // open replaces it with the audio sink's clock or the teardown frees it.
    private IClockSource _clockSource;
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2213", Justification = "Freed by the open that replaces it, or by the teardown's Free(Clock).")]
    private WallClockSource? _ownedClock;

    // Where a detached runtime was positioned, for the attach to seat the shared clocks on. Read
    // and written by the reader only.
    private TimeSpan _detachedOrigin;

    // What the open opened. Written by the open's action, before its outcome is queued; read by
    // the reader, the run's work and the diagnostics.
    private volatile IItemMedia? _media;
    private IMediaSource? _source;

    // ── The core and its queue ──────────────────────────────────────────────

    private readonly Channel<Arrival> _inputs = Channel.CreateUnbounded<Arrival>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly Task _reader;

    // The core's state. Made at the first input, once the init-only settings are in, and stepped
    // by the reader only.
    private ItemRuntimeState? _state;
    private bool _detached;
    private int _runNumber;
    private int _nextCommand;

    // The current run's number and the timeline it presents under (ADR-0084, decision 7).
    private readonly Lock _timelineGate = new();
    private (int Run, TimelineId Timeline) _runTimeline = (-1, TimelineId.None);

    // The commands whose callers are waiting, by number. The reader's only.
    private readonly Dictionary<int, Call> _calls = [];

    // Each run's scope, held until its work has ended and a later run, or the teardown, has seen
    // its stop complete (ADR-0086, decision 1). The reader cancels; a run's start adds and lets go.
    private readonly Lock _runsGate = new();
    private readonly Dictionary<int, RunScope> _runs = [];

    // The awaited actions: one at a time, and the core waits for each before a dispose begins.
    private readonly WorkScope _actions = new();
    private volatile string? _actionUnderWay;

    // Which command, or the dispose (-1), last began waiting for a run's work: for Step.WaitingForRun.
    private int? _waiting;

    private readonly DisposeOnce _dispose = new();
    private readonly TaskCompletionSource _disposeBegun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _tornDown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _sharedReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Exception> _teardownFailures = [];

    // What the cancellation's callbacks of a run's stop threw, which the dispose throws.
    private readonly List<Exception> _stopFailures = [];

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
    /// Whose timers measure how long a stop has waited for work that has not ended, before the
    /// session says what it is (ADR-0086, decision 3.3). Internal: tests run it on a fake.
    /// </summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>How long a stop waits for its work before it names what has not ended.</summary>
    internal static TimeSpan StopWarningInterval => SlowWorkWarning.DefaultInterval;

    /// <summary>
    /// Opens the runtime detached: prepared ahead while another item plays, it leaves the shared
    /// position clock, audio sink and audio clock alone until <see cref="AttachAsync"/>.
    /// </summary>
    internal bool Detached
    {
        init => _detached = value;
    }

    /// <summary>How many threads the item's video decoder uses in software (#788). 0 chooses.</summary>
    internal int DecoderThreads
    {
        init => _decoderThreads = value;
    }

    /// <summary>The matrix the item's video frames convert with (#841).</summary>
    internal ColorMatrix ColorMatrix
    {
        init => _colorMatrix = value;
    }

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
        IReadOnlyList<string>? excludedCodecs = null,
        TimeProvider? wallclockTime = null
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
        // will play (the ADR-0003 audio-master rule, evaluated per item), and frees it then. Its
        // time is WallClockSource's own unless a test steps it on a fake.
        _ownedClock = new WallClockSource(wallclockTime);
        _clockSource = _ownedClock;

        // It waits for the first input, and the dispose waits for it.
        _reader = ReadAsync();
    }

    // ── Read-only surface ───────────────────────────────────────────────────

    public MediaInfo? MediaInfo => _media?.MediaInfo;

    public TimeSpan Duration => _media?.Duration ?? TimeSpan.Zero;

    /// <summary>
    /// The number of the item's current run. It advances each time a seek, a rewind, or a play or
    /// warm-up after a seek cut short stops the run in progress, once that run's work has ended and
    /// before the next run starts.
    /// </summary>
    /// <remarks>
    /// Read it inside <see cref="SessionCallbacks.OnEndOfStream"/>. The session raises a run's end
    /// of stream while that run is the current one, before it steps anything else, so the number
    /// read there is that run's. A seek whose wait for the run is cancelled does not advance it,
    /// since the run has not ended (#707). A caller that handles the end of stream later, on
    /// another thread, compares it with this property then, and drops the end of stream if a seek
    /// or rewind has replaced the run in between.
    /// </remarks>
    public int RunNumber => Volatile.Read(ref _runNumber);

    /// <summary>
    /// Completes once a dispose has let go of everything the runtime shares with the playlist: the
    /// pacer is freed, the audio deactivated and an attached runtime's position clock stopped. The
    /// dispose then frees the pipeline, the decoders and the demuxer, which are the runtime's own.
    /// Completes when the dispose does, at the latest, and never faults (#780).
    /// </summary>
    public Task SharedReleased => _sharedReleased.Task;

    /// <summary>
    /// The timeline the current run presents under: every frame and buffer it decodes carries it.
    /// <see cref="TimelineId.None"/> before the runtime has taken its first command.
    /// </summary>
    /// <remarks>
    /// Each run number gets its own timeline when the number is first taken, at the open or once a
    /// seek or rewind has stopped the run it interrupts, and before that run starts. So a run held
    /// until Play, after a paused seek or before the first play, names its timeline here before
    /// anything carries it. The run's start installs that timeline on the decoders, on the place
    /// decision 2 of ADR-0084 fixes. Read it where <see cref="RunNumber"/> is read: after a command
    /// completes, the two describe the same run.
    /// </remarks>
    public TimelineId Timeline
    {
        get
        {
            lock (_timelineGate)
                return _runTimeline.Timeline;
        }
    }

    // A run number takes a timeline the first time it is seen, and keeps it. Run numbers only grow.
    private TimelineId TimelineOf(int run)
    {
        lock (_timelineGate)
        {
            if (_runTimeline.Run != run)
                _runTimeline = (run, TimelineId.Next());
            return _runTimeline.Timeline;
        }
    }

    /// <summary>
    /// What a session does around its run's work: its start and end, a stop's wait for it, and
    /// what a reposition and the dispose then do to the state it uses. Internal: tests read the
    /// order the steps are taken in, and act at one to drive a race (#707).
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

        /// <summary>A stop, a command's or the dispose's, begins waiting for the run's work.</summary>
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
    /// on the pump's thread. Internal: a seam for tests.
    /// </summary>
    internal Action<CancellationToken>? OnFlushingDecoders { get; set; }

    /// <summary>
    /// Called with the name of a piece of a run's work once its run's scope has let it go, which
    /// is after the session has been told how it ended. Internal: a seam for tests, set before the
    /// session opens.
    /// </summary>
    internal Action<string>? OnWorkLetGo { get; set; }

    /// <summary>
    /// Awaited before each piece of a run's work is started, with the piece. Internal: a seam for
    /// tests, which hold a run's start between two pieces to drive a race.
    /// </summary>
    internal Func<ItemWork, Task>? OnStartingWork { get; set; }

    // The session performing a step's immediate action on this thread, if any.
    [ThreadStatic]
    private static SubstrateSession? t_performing;

    /// <summary>
    /// Whether the calling thread is inside this session's performance of a step's immediate
    /// action: true in a callback the session raises in step order. Internal: tests read it in
    /// the end-of-stream callback.
    /// </summary>
    internal bool IsPerformingStep => t_performing == this;

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

    // ── Commands ────────────────────────────────────────────────────────────

    public ValueTask InitializeAsync(IMediaSource source, CancellationToken cancellationToken = default) =>
        InitializeAsync(source, TimeSpan.Zero, end: null, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Before the first play a positioning holds the session: it moves the demuxer, resets the
    /// decoders, seats both clocks on the start, and starts nothing (#639). The warm-up that
    /// follows decodes from the keyframe at or before the start, and the floor keeps every frame
    /// before the start off the sink.
    /// </para>
    /// <para>
    /// An open that fails frees everything it allocated, in reverse order (#725). A second open
    /// of a session that has opened is refused with <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    public ValueTask InitializeAsync(IMediaSource source, TimeSpan start, TimeSpan? end, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Issue(new Call(ItemCommandKind.Initialize, cancellationToken) { Source = source, Position = start, End = end });
    }

    /// <summary>
    /// Starts the run behind the closed gates and waits for its first frame, so the hardware
    /// decoder's cold start is paid before the first play. Only video has one: an item without it
    /// warms up at once.
    /// </summary>
    public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
        Issue(new Call(ItemCommandKind.WarmUp, cancellationToken));

    /// <summary>
    /// The first play, or a resume. A play after a seek cut short repositions where the clock
    /// stands before it starts a run (#714).
    /// </summary>
    public ValueTask PlayAsync(CancellationToken cancellationToken = default) =>
        Issue(new Call(ItemCommandKind.Play, cancellationToken));

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
        Issue(new Call(ItemCommandKind.Pause, cancellationToken));

    /// <summary>
    /// Seeks to <paramref name="position"/>: stops the run, waits for its work, seeks the demuxer,
    /// resets the decoders and seats the clocks, then, on an item that is playing, starts the next
    /// run over a fresh graph and settles the clocks on its destination frame. On one that is not,
    /// it holds there with the clocks still, the session's own wallclock among them (#739), and the
    /// next play starts the run.
    /// </summary>
    /// <remarks>
    /// A seek that arrives while another waits for the run supersedes it: the earlier one is
    /// cancelled, and the two cost one unwind and one seek.
    /// </remarks>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        Issue(new Call(ItemCommandKind.Seek, cancellationToken) { Position = position });

    /// <inheritdoc/>
    /// <remarks>
    /// A <c>RepeatMode.One</c> loop boundary is reached at the end of the stream, so the run's
    /// pump and graph have already ended. The rewind is a seek to the start that runs the graph it
    /// has again rather than building another, which saves the per-loop teardown and rebuild on a
    /// looping item.
    /// </remarks>
    public ValueTask RewindToStartAsync(CancellationToken cancellationToken = default) =>
        Issue(new Call(ItemCommandKind.Rewind, cancellationToken) { Position = TimeSpan.Zero, KeepsEnd = true });

    /// <inheritdoc/>
    /// <remarks>
    /// The rewind <see cref="RewindToStartAsync"/> describes, to <paramref name="position"/>
    /// rather than to zero: the start of the item this runtime now plays, which for a clip is
    /// partway into the source. The runs that follow stop at <paramref name="end"/>, that item's
    /// end.
    /// </remarks>
    public ValueTask RewindToAsync(TimeSpan position, TimeSpan? end, CancellationToken cancellationToken) =>
        Issue(new Call(ItemCommandKind.Rewind, cancellationToken) { Position = position, End = end });

    /// <inheritdoc/>
    /// <remarks>
    /// Seats the shared position clock and the master clock's baseline on where the runtime was
    /// positioned, as a positioning while attached would have, and completes once it has: a
    /// command called after it finds the runtime attached, and the position clock reads where the
    /// runtime was positioned. The caller has stopped the position clock and disposed the item that
    /// was playing, so nothing else is using them. A runtime that is disposed has nothing to attach.
    /// </remarks>
    public ValueTask AttachAsync()
    {
        var attaching = new Attaching();
        return _inputs.Writer.TryWrite(attaching) ? new ValueTask(attaching.Done.Task) : ValueTask.CompletedTask;
    }

    private ValueTask Issue(Call call)
    {
        // Cancelled before it was called: the command does nothing.
        if (call.Caller.IsCancellationRequested)
            return ValueTask.FromCanceled(call.Caller);

        call.Id = Interlocked.Increment(ref _nextCommand);
        if (!_inputs.Writer.TryWrite(new Called(call)))
            return ValueTask.FromException(new ObjectDisposedException(nameof(SubstrateSession)));
        return new ValueTask(call.Done.Task);
    }

    private void Post(Input input) => _inputs.Writer.TryWrite(new Fed(input));

    // ── The reader ──────────────────────────────────────────────────────────

    private async Task ReadAsync()
    {
        var reader = _inputs.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var arrival))
            {
                try
                {
                    await StepAsync(arrival).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A defect in the shell. The input is lost, and the runtime goes on. A command
                    // the core never stepped fails its caller, rather than leaving it waiting.
                    _logger.LogError(ex, "The item runtime could not take {Input}.", arrival);
                    if (arrival is Called { Call: { Stepped: false } lost })
                        Fail(lost, ex);
                    if (arrival is Attaching attaching)
                        attaching.Done.TrySetException(ex);
                }
            }
        }
    }

    private async Task StepAsync(Arrival arrival)
    {
        var state = _state ??= ItemRuntimeState.Initial(
            attached: !_detached,
            latenessRecovery: LatenessRecovery is { Enabled: true }
        );

        Input input;
        if (arrival is Called { Call: var call })
        {
            _calls[call.Id] = call;
            call.Action = CancellationTokenSource.CreateLinkedTokenSource(call.Caller);

            // What a command reads as it arrives, the position clock among them, can throw. The
            // command then fails, as a call that threw used to, and the core never sees it.
            try
            {
                input = InputOf(call, state);
            }
            catch (Exception ex)
            {
                Fail(call, ex);
                return;
            }
        }
        else if (arrival is Attaching)
        {
            input = new Input.Attach();
        }
        else
        {
            input = ((Fed)arrival).Input;
        }

        if (input is Input.Performed or Input.Opened)
            _actionUnderWay = null;

        var (next, step) = ItemRuntimeProtocol.Step(state, input);
        _state = next;
        TimelineOf(next.Run);
        Volatile.Write(ref _runNumber, next.Run);
        if (arrival is Called { Call: var stepped })
            stepped.Stepped = true;

        // A dispose waits for the action under way, and cuts it short.
        if (next.DisposeRequested && !state.DisposeRequested && next.Command is { } busy && _calls.TryGetValue(busy.Id, out var under))
            CancelAction(under);
        if (next.Phase is ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed)
            _disposeBegun.TrySetResult();

        foreach (var action in step.Actions)
            await PerformAsync(action).ConfigureAwait(false);

        TakeWaiting(next);

        // The caller's cancellation is stepped as an input, once the command has been: it ends a
        // command that waits for work, and one whose action is under way ends with the action,
        // whose token it also cancels.
        if (arrival is Called { Call: var issued } && _calls.ContainsKey(issued.Id) && issued.Caller.CanBeCanceled)
        {
            issued.Registration = issued.Caller.UnsafeRegister(
                static state =>
                {
                    var (session, id) = ((SubstrateSession, int))state!;
                    session.Post(new Input.CallerCancelled(id));
                },
                (this, issued.Id)
            );
        }

        if (step.Awaited is { } awaited)
            StartAwaited(awaited, next);

        if (arrival is Attaching attached)
            attached.Done.TrySetResult();
    }

    // Cuts short a command's action for the dispose. Its token's callbacks run here, on the
    // reader, and only queue inputs or let the action end; one that throws is logged, and the
    // others have all run.
    private void CancelAction(Call call)
    {
        try
        {
            call.Action?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cutting short the item runtime's {Command} for its dispose threw.", call);
        }
    }

    // Fails a command the core never stepped, and forgets it.
    private void Fail(Call call, Exception error)
    {
        _calls.Remove(call.Id);
        call.Action?.Dispose();
        call.Done.TrySetException(error);
    }

    private Input InputOf(Call call, ItemRuntimeState state) =>
        call.Kind switch
        {
            ItemCommandKind.Initialize => new Input.Initialize(call.Id, call.Position, call.End),
            ItemCommandKind.WarmUp => new Input.WarmUp(call.Id, ClockPosition(state)),
            ItemCommandKind.Play => new Input.Play(call.Id, ClockPosition(state), ClockPaused(state)),
            ItemCommandKind.Pause => new Input.Pause(call.Id),
            ItemCommandKind.Seek => new Input.Seek(call.Id, call.Position, ClockPaused(state)),
            ItemCommandKind.Rewind => new Input.Rewind(
                call.Id,
                call.Position,
                call.KeepsEnd ? state.ItemEnd : call.End,
                ClockPaused(state)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(call), call.Kind, "Not a command."),
        };

    // Where the position clock stands as a command arrives. A runtime prepared ahead leaves the
    // shared clock alone: it stands where the runtime was positioned.
    private TimeSpan ClockPosition(ItemRuntimeState state) => state.Attached ? _clock.Position : _detachedOrigin;

    // Whether the position clock is paused as a command arrives. A detached runtime has not played,
    // so whether a reposition plays on does not depend on it.
    private bool ClockPaused(ItemRuntimeState state) => !state.Attached || _clock.IsPaused;

    // Takes Step.WaitingForRun when a command, or the dispose, begins waiting for a run's work.
    private void TakeWaiting(ItemRuntimeState state)
    {
        int? waiting = state.Command is { Stage: ItemCommandStage.Stopping } command
            ? command.Id
            : state.Phase == ItemRuntimePhase.Disposing && state.Status == ItemRunStatus.Stopping
                ? -1
                : null;
        if (waiting is not null && waiting != _waiting)
            Take(Step.WaitingForRun);
        _waiting = waiting;
    }

    // ── Immediate actions ───────────────────────────────────────────────────

    private async ValueTask PerformAsync(RuntimeAction action)
    {
        switch (action)
        {
            // The teardown's: what fails is collected, and the dispose throws it (decision 3.4).
            case RuntimeAction.Free free:
                await FreeAsync(free.Resource).ConfigureAwait(false);
                return;
            case RuntimeAction.DeactivateAudio:
                try
                {
                    await _audioSink!.DeactivateAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _teardownFailures.Add(ex);
                }
                return;
            case RuntimeAction.StopClock:
                try
                {
                    _clock.Stop();
                }
                catch (Exception ex)
                {
                    _teardownFailures.Add(ex);
                }
                return;
        }

        t_performing = this;
        try
        {
            Perform(action);
        }
        catch (Exception ex) when (action is RuntimeAction.ReportFault or RuntimeAction.RaiseEndOfStream)
        {
            // A callback of the item's owner threw. What it was told stands, and it is not told again.
            _logger.LogError(ex, "The item runtime's {Action} callback threw.", action);
        }
        catch (Exception ex)
        {
            // The core takes these to succeed. One that throws, a clock's call among them, leaves
            // the item where it cannot be trusted to play on, so it is reported as a fault of the
            // item, which ends it; the command it belongs to goes on.
            _logger.LogError(ex, "The item runtime's {Action} threw.", action);
            TryReportWorkerFault(ex);
        }
        finally
        {
            t_performing = null;
        }
    }

    private void Perform(RuntimeAction action)
    {
        switch (action)
        {
            case RuntimeAction.CancelRun cancel:
                CancelRun(cancel.Run);
                break;
            case RuntimeAction.CloseGates:
                _media?.CloseGates();
                break;
            case RuntimeAction.OpenGates:
                _media?.OpenGates();
                break;
            case RuntimeAction.FlushPacer:
                _media?.FlushPacer();
                break;
            case RuntimeAction.ReleaseSettle:
                if (_media is { } held)
                    held.ReleaseSettle(held.PacerRun);
                break;
            case RuntimeAction.ResetDecoders:
                Take(Step.ResettingForSeek);
                _media!.ResetForSeek();
                break;
            case RuntimeAction.SeatClocks seat:
                if (seat.Shared)
                {
                    _clock.Seek(seat.Target);
                    // Reseats the master pacing clock's origin to the exact target, uniformly across
                    // clock masters: the wallclock and the audio sink both implement ISeekableClock.
                    // For the audio sink this seeds the origin its next activation applies, which
                    // also realigns a looping item's audio-mastered origin at every boundary
                    // (perf survey B5, covered by AudioMasteredLoopOriginTests).
                    (_clockSource as ISeekableClock)?.SeekBaseline(seat.Target);
                }
                else
                {
                    // A detached runtime records the target, and the attach seats the shared clocks
                    // on it (media-clips decision 8). The clock it owns it seats now.
                    _detachedOrigin = seat.Target;
                    _ownedClock?.SeekBaseline(seat.Target);
                }
                break;
            case RuntimeAction.SeatBaseline baseline:
                // Seats the master clock's origin before the audio sink activates, so an
                // audio-mastered clock does not rediscover it from the first buffer and drift
                // behind the decoded video across a gapless boundary.
                (_clockSource as ISeekableClock)?.SeekBaseline(baseline.Origin);
                break;
            case RuntimeAction.StartClocks start:
                _clock.Start(start.Origin);
                // Start runs the wallclock from zero and discards the baseline seated before it,
                // because the owned wallclock is the master. It is seated again once running, or
                // a seek before the first play paced the target's frames against a clock at zero
                // (#639).
                if (_ownedClock is { } wallclock)
                {
                    wallclock.Start();
                    wallclock.Seek(start.Origin);
                }
                break;
            case RuntimeAction.PauseClocks:
                _clock.Pause();
                _ownedClock?.Pause();
                break;
            case RuntimeAction.ResumeClocks:
                _clock.Resume();
                _ownedClock?.Resume();
                break;
            case RuntimeAction.AttachClocks:
                _clock.Seek(_detachedOrigin);
                (_clockSource as ISeekableClock)?.SeekBaseline(_detachedOrigin);
                break;
            case RuntimeAction.ReportFault fault:
                ReportFault(fault);
                break;
            case RuntimeAction.RaiseEndOfStream:
                // In step order, so RunNumber read in the callback is the run's.
                _callbacks.OnEndOfStream();
                break;
            case RuntimeAction.CompleteCommand complete:
                Complete(complete);
                break;
            case RuntimeAction.ReleaseShared:
                _sharedReleased.TrySetResult();
                break;
            case RuntimeAction.CompleteDispose:
                _inputs.Writer.TryComplete();
                _tornDown.TrySetResult();
                break;
            default:
                throw new InvalidOperationException($"'{action}' is not an immediate action.");
        }
    }

    private async ValueTask FreeAsync(ItemResources resource)
    {
        switch (resource)
        {
            case ItemResources.Pipeline:
                Take(Step.DisposingPipeline);
                break;
            case ItemResources.VideoDecoder:
                Take(Step.DisposingVideoDecoder);
                break;
            case ItemResources.AudioDecoder:
                Take(Step.DisposingAudioDecoder);
                break;
            case ItemResources.Demux:
                Take(Step.DisposingDemux);
                break;
        }

        try
        {
            if (resource == ItemResources.Clock)
            {
                var wallclock = _ownedClock;
                _ownedClock = null;
                if (wallclock is not null)
                    await wallclock.DisposeAsync().ConfigureAwait(false);
            }
            else if (_media is { } media)
            {
                await media.FreeAsync(resource).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _teardownFailures.Add(ex);
        }
    }

    private void Complete(RuntimeAction.CompleteCommand complete)
    {
        if (!_calls.Remove(complete.Command, out var call))
            return;

        call.Registration.Dispose();
        call.Action?.Dispose();
        var result = complete.Result;
        switch (result.Outcome)
        {
            case ItemCommandOutcome.Ok:
                call.Done.TrySetResult();
                break;
            case ItemCommandOutcome.Cancelled:
                call.Done.TrySetCanceled(call.Caller.IsCancellationRequested ? call.Caller : CancellationToken.None);
                break;
            case ItemCommandOutcome.Failed:
                call.Done.TrySetException(
                    result.Error ?? new InvalidOperationException($"The item runtime's {call.Kind} failed.")
                );
                break;
            default:
                call.Done.TrySetException(Refusal(call, result.Refusal));
                break;
        }
    }

    // A command the core refused, as the exception its caller sees.
    private Exception Refusal(Call call, ItemRefusal refusal) =>
        refusal switch
        {
            ItemRefusal.Disposed => new ObjectDisposedException(nameof(SubstrateSession)),
            // A start at or past a known end: the item would open with nothing in it and end at
            // once, and under RepeatMode.One it would then loop with no content. Refused at the
            // open, or at a rewind that hands the runtime to another clip of the same source, it
            // fails to start, and the playlist's failure guard counts it.
#pragma warning disable CA2208 // The open's and the rewind's start, which the message has always named.
            ItemRefusal.PastTheEnd => new ArgumentOutOfRangeException(
                "start",
                call.Position,
                $"'{_source?.DisplayName}' cannot start at {call.Position}: it is {Duration} long."
            ),
#pragma warning restore CA2208
            ItemRefusal.AlreadyOpen => new InvalidOperationException("The item runtime has opened already."),
            ItemRefusal.Detached => new InvalidOperationException(
                "A runtime prepared ahead does not play or pause until it is attached."
            ),
            _ => new InvalidOperationException(
                $"The item runtime's {call.Kind} arrived while another command's action was under way; "
                    + "its caller serialises its commands."
            ),
        };

    // ── Awaited actions ─────────────────────────────────────────────────────

    private void StartAwaited(RuntimeAction action, ItemRuntimeState state)
    {
        var call = state.Command is { } command && _calls.TryGetValue(command.Id, out var c) ? c : null;
        var token = call?.Action?.Token ?? CancellationToken.None;
        var name = NameOf(action);
        _actionUnderWay = name;
        if (!_actions.TryStart(name, t => PerformAwaitedAsync(action, call, t), token))
            Post(new Input.Performed(ItemActionOutcome.Cancelled, new ObjectDisposedException(nameof(SubstrateSession))));
    }

    private async Task PerformAwaitedAsync(RuntimeAction action, Call? call, CancellationToken token)
    {
        Input outcome;
        try
        {
            switch (action)
            {
                case RuntimeAction.Open:
                    outcome = await OpenAsync(call!.Source!, token).ConfigureAwait(false);
                    break;
                case RuntimeAction.StartRun start:
                    await StartRunAsync(start).ConfigureAwait(false);
                    outcome = Ok;
                    break;
                case RuntimeAction.SeekDemux seek:
                    Take(Step.SeekingDemux);
                    await _media!.SeekAsync(seek.Target, token).ConfigureAwait(false);
                    outcome = Ok;
                    break;
                case RuntimeAction.ActivateAudio:
                    await _audioSink!.ActivateAsync(token).ConfigureAwait(false);
                    outcome = Ok;
                    break;
                case RuntimeAction.PauseAudio:
                    await _audioSink!.PauseAsync(token).ConfigureAwait(false);
                    outcome = Ok;
                    break;
                case RuntimeAction.ResumeAudio:
                    await _audioSink!.ResumeAsync(token).ConfigureAwait(false);
                    outcome = Ok;
                    break;
                case RuntimeAction.DeactivateAudio:
                    await _audioSink!.DeactivateAsync(token).ConfigureAwait(false);
                    outcome = Ok;
                    break;
                case RuntimeAction.Settle:
                    await SettleAsync(token).ConfigureAwait(false);
                    outcome = Ok;
                    break;
                default:
                    throw new InvalidOperationException($"'{action}' is not an awaited action.");
            }
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            outcome = new Input.Performed(ItemActionOutcome.Cancelled, ex);
        }
        catch (Exception ex)
        {
            outcome = new Input.Performed(ItemActionOutcome.Failed, ex);
        }

        Post(outcome);
    }

    private static readonly Input Ok = new Input.Performed(ItemActionOutcome.Ok);

    /// <summary>
    /// Opens the item's media, then builds the pacer against the item's master clock, then, once
    /// nothing after it can fail, frees the provisional wallclock if the audio sink masters.
    /// </summary>
    /// <remarks>
    /// An open that fails frees everything it allocated, in reverse order, and leaves the session
    /// holding only the wallclock it was constructed with (#725).
    /// </remarks>
    private async Task<Input> OpenAsync(IMediaSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _source = source;
        var media = await Opener().OpenAsync(source, cancellationToken).ConfigureAwait(false);

        // Per-item master-clock selection (ADR-0003). The audio sink masters the clock only when
        // THIS item has a decodable audio stream the sink will play AND the sink can publish a
        // sample-counter clock. Otherwise the provisional wallclock remains the master. This is
        // what lets one warm audio sink span a playlist of mixed audio and silent items, and it
        // removes the stall a video-only item would hit when paced against an attached but
        // unactivated audio sink's frozen counter.
        var hasAudio = (media.Resources & ItemResources.AudioDecoder) != 0;
        IClockSource master = hasAudio && _audioSink is IClockSource audioClock ? audioClock : _ownedClock!;
        try
        {
            media.OpenPacer(master);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var resource in MediaResources)
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

        // Nothing below fails: the media is the runtime's.
        _media = media;
        if (!ReferenceEquals(master, _ownedClock) && _ownedClock is { } provisional)
        {
            _clockSource = master;
            _ownedClock = null;
            try
            {
                await provisional.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // It is disposed whatever its cancellation's callbacks threw, and nothing used it.
                _logger.LogWarning(ex, "Freeing the provisional wallclock threw; the audio sink's clock masters the item.");
            }
        }

        // The core waits on the first frame for the warm-up.
        media.FirstFrameDecoded?.ContinueWith(
            static (_, session) => ((SubstrateSession)session!).Post(new Input.FirstFrameDecoded()),
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        return new Input.Opened(
            media.Resources | (_ownedClock is null ? ItemResources.None : ItemResources.Clock),
            media.Duration
        );
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
            DecoderThreads = _decoderThreads,
            ColorMatrix = _colorMatrix,
            DeviceCache = DeviceCache,
            VideoConfigurator = _videoConfigurator,
            AudioConfigurator = _audioConfigurator,
            LatenessRecovery = LatenessRecovery,
            FormatAnnouncer = FormatAnnouncer,
            SinkFailures = SinkFailures,
            Callbacks = _callbacks,
            // The pacer's own delivery fault is reported straight away, not through a run's work
            // (#711): the pacer is no run's.
            OnPacerFaulted = TryReportWorkerFault,
            OnFlushingDecoders = token => OnFlushingDecoders?.Invoke(token),
            LoggerFactory = _loggerFactory,
        };

    /// <summary>
    /// Starts a run: readies the media for it, then starts each piece of its work under a scope of
    /// its own, which holds it until it ends.
    /// </summary>
    /// <remarks>
    /// The core starts a run only once every earlier run's work has ended. Their scopes may still
    /// be letting go of it, so they are waited for here and forgotten, and a run starts only when
    /// no older scope holds any work (ADR-0086, decision 1).
    /// </remarks>
    private async Task StartRunAsync(RuntimeAction.StartRun start)
    {
        RunScope[] older;
        lock (_runsGate)
        {
            older = [.. _runs.Values];
            _runs.Clear();
        }
        foreach (var run in older)
            await ForgetAsync(run).ConfigureAwait(false);

        var timeline = TimelineOf(start.Run);
        _media!.BeginRun(timeline, start.Graph, start.Floor, start.End, start.HoldForSettle);
        _logger.LogDebug(RunLaunchedMessage, start.Run, timeline);

        var scope = new RunScope(start.Run, new WorkScope { WorkEnded = name => OnWorkLetGo?.Invoke(name) });
        lock (_runsGate)
            _runs.Add(start.Run, scope);
        foreach (var work in (ItemWork[])[ItemWork.Pump, ItemWork.Graph, ItemWork.Recovery, ItemWork.EmptyEnd])
        {
            if ((start.Work & work) == 0)
                continue;
            if (OnStartingWork is { } starting)
                await starting(work).ConfigureAwait(false);

            // A piece that faulted already, before the start's outcome, has stopped the run, and
            // the scope starts nothing more. What it kept from starting has ended as cancelled,
            // so the core waits for nothing that never ran.
            if (!scope.Scope.TryStart(NameOf(work), t => RunWorkAsync(start.Run, work, t)))
                Post(new Input.WorkEnded(start.Run, work, ItemWorkEnding.Cancelled));
        }
    }

    /// <summary>
    /// The Debug line each run's start writes, with the run number and the timeline it starts on
    /// as its two arguments. The integration tests read starts from it.
    /// </summary>
    internal const string RunLaunchedMessage = "Run {RunNumber} launches on {Timeline}.";

    private static string NameOf(RuntimeAction action) =>
        action switch
        {
            RuntimeAction.Open => "the open",
            RuntimeAction.StartRun => "a run's start",
            RuntimeAction.SeekDemux => "the demuxer's seek",
            RuntimeAction.ActivateAudio => "the audio sink's activation",
            RuntimeAction.PauseAudio => "the audio sink's pause",
            RuntimeAction.ResumeAudio => "the audio sink's resume",
            RuntimeAction.DeactivateAudio => "the audio sink's deactivation",
            RuntimeAction.Settle => "the wait for a seek's destination frame",
            _ => action.GetType().Name,
        };

    private static string NameOf(ItemWork work) =>
        work switch
        {
            ItemWork.Pump => "the demux pump",
            ItemWork.Graph => "the graph",
            ItemWork.Recovery => "the lateness-recovery walk",
            _ => "the end of stream of an item with nothing to play",
        };

    /// <summary>
    /// Runs one piece of run <paramref name="run"/>'s work, and queues how it ended: Completed,
    /// Cancelled for an <see cref="OperationCanceledException"/> once the run is cancelled, or
    /// Faulted. It reports nothing itself: the core reports the fault, or raises the end of stream.
    /// </summary>
    private async Task RunWorkAsync(int run, ItemWork work, CancellationToken token)
    {
        var (started, ended) = work switch
        {
            ItemWork.Pump => (Step.PumpStarted, Step.PumpEnded),
            ItemWork.Graph => (Step.GraphStarted, Step.GraphEnded),
            ItemWork.Recovery => (Step.RecoveryStarted, Step.RecoveryEnded),
            _ => ((Step?)null, (Step?)null),
        };

        var ending = ItemWorkEnding.Completed;
        Exception? error = null;
        try
        {
            if (started is { } s)
                Take(s);
            await (
                work switch
                {
                    ItemWork.Pump => _media!.RunPumpAsync(token),
                    ItemWork.Graph => _media!.RunGraphAsync(token),
                    ItemWork.Recovery => _media!.RunRecoveryWalkAsync(token),
                    _ => EndOfEmptyRun(token),
                }
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ending = ItemWorkEnding.Cancelled;
        }
        catch (Exception ex)
        {
            ending = ItemWorkEnding.Faulted;
            error = ex;
        }
        finally
        {
            try
            {
                if (ended is { } e)
                    Take(e);
            }
            finally
            {
                Post(new Input.WorkEnded(run, work, ending, error));
            }
        }
    }

    // The end of stream of an item with nothing to play, raised off the thread that started the
    // run, unless the run was stopped first.
    private static Task EndOfEmptyRun(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels run <paramref name="run"/>'s scope, once, and does not wait for it: the core waits
    /// for the work's ends, which the reader steps. A stop not ended after
    /// <see cref="StopWarningInterval"/> names the work that has not (ADR-0086, decision 3.3).
    /// </summary>
    private void CancelRun(int run)
    {
        RunScope? scope;
        lock (_runsGate)
            _runs.TryGetValue(run, out scope);
        if (scope is null || scope.Stopping is not null)
            return;

        // The cancellation's callbacks, the work's own unwinding among them, run on the thread
        // pool rather than on the reader.
        scope.Stopping = WarnIfSlowAsync(scope.Scope.StopAsync(), scope);
    }

    private async Task<IReadOnlyList<WorkFault>> WarnIfSlowAsync(Task<IReadOnlyList<WorkFault>> stopping, RunScope scope)
    {
        await SlowWorkWarning
            .AwaitAsync(stopping, StopWarningInterval, TimeProvider, () => WarnRunNotEnded(scope))
            .ConfigureAwait(false);
        return await stopping.ConfigureAwait(false);
    }

    private void WarnRunNotEnded(RunScope scope) =>
        _logger.LogWarning(
            "The item runtime has waited {Seconds:F0}s for run {Run} to end, which is still running: {Work}. "
                + "A sink, a configurator's node or the source's read is not observing cancellation; the runtime waits for it.",
            StopWarningInterval.TotalSeconds,
            scope.Run,
            string.Join(", ", scope.Scope.Pending)
        );

    // Waits for a run's stop to complete. What its cancellation's callbacks threw is kept for the
    // dispose to throw, at a seek as at the dispose: the core has reported every fault the work
    // itself ended with, and a callback's is the stop's own.
    private async Task ForgetAsync(RunScope run)
    {
        foreach (var fault in await (run.Stopping ?? run.Scope.StopAsync()).ConfigureAwait(false))
        {
            _logger.LogWarning(fault.Error, "Stopping run {Run}'s {Work} threw.", run.Run, fault.Work);
            lock (_runsGate)
                _stopFailures.Add(fault.Error);
        }
    }

    /// <summary>
    /// Reports the fault the core asks for. A pump's or a graph's is the item's: its run is
    /// cancelled at once, so the rest of the run does not wait on it (#713) — a pump parked on a
    /// full decoder queue that a faulted graph no longer drains ends — then the shared device is
    /// dropped and the fault goes to <see cref="SessionCallbacks.OnWorkerFaulted"/>. A
    /// lateness-recovery walk's is recoverable: the walk is an optimisation, and playback carries
    /// on at whatever rung it had reached.
    /// </summary>
    private void ReportFault(RuntimeAction.ReportFault fault)
    {
        var error = fault.Error ?? new InvalidOperationException($"Run {fault.Run}'s {NameOf(fault.Work)} faulted.");
        if (fault.Work == ItemWork.Recovery)
        {
            _callbacks.OnRecoverableError(
                new PlaybackError(ErrorCategory.System, $"Lateness recovery stopped after a fault: {error.Message}", error)
            );
            return;
        }

        CancelRun(fault.Run);
        ReportWorkerFault(error);
    }

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

    private void TryReportWorkerFault(Exception ex)
    {
        try
        {
            ReportWorkerFault(ex);
        }
        catch (Exception reportFailure)
        {
            _logger.LogError(reportFailure, "Reporting the item's fault threw.");
        }
    }

    /// <summary>
    /// Reseats the clocks onto the first frame a post-seek run actually delivers, so the
    /// decode-forward that produced it does not count as playback time (#161), and releases the
    /// pacer's hold on that frame however the wait ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seek repositions the demuxer to the keyframe at or before the target and seats the clocks
    /// on the target. The decoder then has to walk from that keyframe to the target before anything
    /// can be shown — 0.73 s on a file with sparse keyframes. The clocks count that walk as
    /// playback, so by the time the destination frame arrives they are already past it. Reseating
    /// onto the frame that arrived removes the run-up. It is a backwards step in reported position,
    /// which is why it happens inside the seek, where a discontinuity is expected.
    /// </para>
    /// <para>
    /// Only a seek on an item that is playing settles; one taken while paused decodes forward on
    /// the following resume. And only a wallclock master is reseated: an audio-mastered clock does
    /// not advance during the walk, and reseating its origin from a video frame's time is the move
    /// ADR-0057 §B5 exists to prevent. The core asks for a settle only then.
    /// </para>
    /// </remarks>
    private async Task SettleAsync(CancellationToken cancellationToken)
    {
        if (_media is not { } media || _ownedClock is not { } wallclock)
            return;

        // Captured as the settle starts, so one that finishes late releases the hold its run
        // armed rather than one a newer run has since taken.
        var run = media.PacerRun;
        try
        {
            var reached = await media.WaitForSeekTargetAsync(run, SeekSettleCap, cancellationToken).ConfigureAwait(false);

            // Null means the run had no floor, or the target never arrived: leave the clocks alone.
            if (reached is not { } pts)
                return;

            _clock.Seek(pts);
            wallclock.Seek(pts);
        }
        finally
        {
            // The pacer holds delivery from the moment it admits the destination frame until this
            // runs, so that no frame is presented against the clock this is correcting. However
            // the settle ends, the picture is not left held.
            media.ReleaseSettle(run);
        }
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops the run and waits for its work to end, then frees the pacer, deactivates the audio
    /// sink, stops the position clock, and frees the pipeline, the decoders, the demux session and
    /// the wallclock, in reverse allocation order. A second call waits for the first and ends as it
    /// did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A command's action under way is cut short, and the dispose begins once it has ended. The
    /// wait lasts as long as the run takes to unwind once cancelled: a sink or a configurator's
    /// node that ignores its token holds it until the call returns, and after five seconds the
    /// session names what it is waiting for. A demuxer read that waits on a stalled source does not
    /// hold it, since the run's cancellation interrupts it (#709). A caller that cannot wait bounds
    /// its own wait; nothing is freed until the work ends.
    /// </para>
    /// <para>
    /// It throws what freeing threw, what deactivating the audio sink and stopping the position
    /// clock threw, and what a cancellation's callback threw as any run stopped, at a seek or here:
    /// one exception, or an <see cref="AggregateException"/> of several. A fault a run's work ended
    /// with was reported when it ended, and is not thrown again. Do not await it from a callback the
    /// session raises, which the teardown would be waiting behind.
    /// </para>
    /// </remarks>
    public ValueTask DisposeAsync() => _dispose.RunAsync(TearDownAsync);

    private async ValueTask TearDownAsync()
    {
        try
        {
            await TearDownCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            // A dispose that ends without the core's word, a failed one included, still ends.
            _sharedReleased.TrySetResult();
        }
    }

    private async ValueTask TearDownCoreAsync()
    {
        Post(new Input.Dispose());

        // An action under way, such as an open or a sink's call that ignores its token, holds the
        // dispose's start, and is named if it does for long; the run's stop names its own work.
        await SlowWorkWarning
            .AwaitAsync(_disposeBegun.Task, StopWarningInterval, TimeProvider, WarnActionHoldsDispose)
            .ConfigureAwait(false);
        await _tornDown.Task.ConfigureAwait(false);
        await _reader.ConfigureAwait(false);

        // The core freed nothing before every run's work had ended; this waits for the scopes to
        // let it go, and for the last action's task to end.
        RunScope[] runs;
        lock (_runsGate)
        {
            runs = [.. _runs.Values];
            _runs.Clear();
        }
        foreach (var run in runs)
            await ForgetAsync(run).ConfigureAwait(false);

        var failures = new List<Exception>(_teardownFailures);
        lock (_runsGate)
            failures.AddRange(_stopFailures);
        foreach (var fault in await _actions.StopAsync().ConfigureAwait(false))
            failures.Add(fault.Error);

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("SubstrateSession teardown encountered multiple failures.", failures);
    }

    private void WarnActionHoldsDispose() =>
        _logger.LogWarning(
            "The item runtime's dispose has waited {Seconds:F0}s for {Action} to end. "
                + "A sink or the source is not observing cancellation; the dispose waits for it.",
            StopWarningInterval.TotalSeconds,
            _actionUnderWay ?? "a command's action"
        );

    // ── Types ───────────────────────────────────────────────────────────────

    /// <summary>What the reader steps: a command, or an input fed back.</summary>
    private abstract record Arrival;

    private sealed record Fed(Input Input) : Arrival;

    private sealed record Called(Call Call) : Arrival;

    /// <summary>An attach, and its caller waiting for it to have been stepped.</summary>
    private sealed record Attaching : Arrival
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A command and its caller, waiting for the core to complete it.</summary>
    private sealed class Call(ItemCommandKind kind, CancellationToken caller)
    {
        public ItemCommandKind Kind { get; } = kind;

        public CancellationToken Caller { get; } = caller;

        public int Id { get; set; }

        /// <summary>Completes as the core completes the command, and never earlier.</summary>
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The token the command's actions get: the caller's, which a dispose also cancels.</summary>
        public CancellationTokenSource? Action { get; set; }

        public CancellationTokenRegistration Registration { get; set; }

        /// <summary>Whether the core has stepped it, so the core will complete it.</summary>
        public bool Stepped { get; set; }

        public IMediaSource? Source { get; init; }

        /// <summary>An open's start, a seek's target or a rewind's position.</summary>
        public TimeSpan Position { get; init; }

        public TimeSpan? End { get; init; }

        /// <summary>A rewind that keeps the item's end rather than handing the runtime another.</summary>
        public bool KeepsEnd { get; init; }

        public override string ToString() => $"{Kind} #{Id}";
    }

    /// <summary>A run's scope, and its stop once it has been cancelled.</summary>
    private sealed class RunScope(int run, WorkScope scope)
    {
        public int Run { get; } = run;

        public WorkScope Scope { get; } = scope;

        public Task<IReadOnlyList<WorkFault>>? Stopping { get; set; }
    }
}
