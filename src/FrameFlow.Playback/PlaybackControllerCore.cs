// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Threading.Channels;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback.Commands;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stateless;
using Stateless.Graph;

namespace FrameFlow.Playback;

/// <summary>
/// Core playback controller managing three orthogonal state machines (primary playback,
/// seeking, repeat) with channel-serialized command dispatch. All public commands are
/// thin wrappers around <see cref="PostAndWaitAsync"/> that serialize through a bounded
/// channel and are processed sequentially on the dispatch loop.
/// </summary>
/// <remarks>
/// <para>
/// The primary playback machine is the pure <see cref="PlaybackProtocol"/> fold executed
/// by the shell's action interpreter (<see cref="RunPlaybackAsync"/>): the protocol decides
/// the next state and the ordered effect list, and the interpreter performs each effect,
/// emits the public-state projection, and follows the loading auto-chain — the Stateless
/// <c>OnEntry</c>/<c>OnExit</c> executor it replaced is retired (architecture review §2.1;
/// ADR-0055's sibling pattern). The seeking and repeat regions remain Stateless machines.
/// </para>
/// <para>
/// The machine is driven against the internal fine-grained
/// <see cref="InternalPlaybackState"/> enum so that loading substates remain available
/// for dispatch logic and diagnostics. Consumers observe the collapsed public
/// <see cref="PlaybackState"/> surface — loading substates project to
/// <see cref="PlaybackState.Loading"/> and are emitted only on the first entry so
/// observers do not see redundant <c>Loading → Loading</c> transitions.
/// </para>
/// </remarks>
internal sealed partial class PlaybackControllerCore : IPlaybackController, IAsyncDisposable
{
    // ── Primary playback state ─────────────────────────────────────────
    // The primary playback machine is no longer a Stateless StateMachine: the
    // pure PlaybackProtocol.Advance fold is the DECISION authority and the
    // dispatch shell's action interpreter (RunPlaybackAsync) is the EXECUTOR
    // (architecture review §2.1; ADR-0055's sibling pattern one layer up). The
    // single source of truth for the current internal state is this field,
    // mutated only on the dispatch loop thread (the OnTransitioned projection
    // point). The seeking and repeat regions remain Stateless — they were never
    // part of the lifted transition table.
    private InternalPlaybackState _state = InternalPlaybackState.Idle;
    private readonly StateMachine<SeekState, SeekTrigger> _seeking;
    private readonly StateMachine<RepeatMode, RepeatTrigger> _repeat;

    // ── Command channel ────────────────────────────────────────────────
    private readonly Channel<IPlayerCommand> _commandChannel =
        Channel.CreateBounded<IPlayerCommand>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );

    private readonly Task _dispatchLoop;

    // ── Observable subjects ────────────────────────────────────────────
    private readonly PlaybackSubject<StateTransition<PlaybackState>> _playbackStateSubject = new();
    private readonly PlaybackSubject<StateTransition<SeekState>> _seekStateSubject = new();
    private readonly PlaybackSubject<StateTransition<RepeatMode>> _repeatModeSubject = new();
    private readonly PlaybackSubject<LoopRestarted> _loopRestartedSubject = new();
    private readonly PlaybackSubject<LoopStalled> _loopStalledSubject = new();
    private readonly PlaybackSubject<PlaybackError> _errorSubject = new();
    private readonly PlaybackSubject<TimeSpan> _positionTickSubject = new();

    // ── Loop-stall watchdog ────────────────────────────────────────────
    // Folds each position tick through the pure LoopStallEvaluator to catch a
    // failed RepeatMode.One restart (position overruns duration while the clock
    // keeps advancing — the "frozen frame, seeker still moving" failure). The
    // evaluator + edge flag are mutated only on the position-tick callback (a
    // single producer), so they need no lock. The current verdict is mirrored
    // into volatile fields so GetDiagnostics can surface it as level-triggered
    // state (ADR-0034), alongside the edge-triggered LoopStalled observable and
    // the LoopStallMetrics counter.
    private LoopStallEvaluator _loopStallEvaluator = LoopStallEvaluator.Create(
        TimeSpan.FromSeconds(2)
    );
    private bool _loopWasStalled;
    private volatile bool _loopStalledNow;

    // Which session produced the next snapshot. Incremented on every CreateSession so a
    // consumer can tell that a load happened between two polls: the demux and decoder
    // counters restart at zero there while the consumer's long-lived sink keeps climbing,
    // and nothing else in the snapshot distinguishes that from a burst of activity.
    // Written on the action pump, read by GetDiagnostics from any thread.
    private int _sessionGeneration;
    private long _loopOverrunTicks;
    private IDisposable? _loopStallSubscription;

    // ── Session ────────────────────────────────────────────────────────
    private readonly IPlaybackSessionFactory _sessionFactory;
    private IPlaybackSession? _session;
    private IMediaSource? _loadedSource;

    // ── Loaded-media snapshot (captured after InitializeAsync succeeds) ──
    // Per ADR-0028 §6, Duration and MediaInfo are immutable once loading
    // completes, so the controller caches them directly rather than delegating
    // through the session on every consumer access. Both are cleared when the
    // session is disposed.
    private MediaInfo? _loadedMediaInfo;
    private TimeSpan _loadedDuration;

    // ── Active seek orchestration ──────────────────────────────────────
    private Task? _activeSeekTask;
    private CancellationTokenSource? _activeSeekCancellation;
    private long _activeSeekOperationId;
    private long _nextSeekOperationId;
    private readonly Queue<FireTriggerCommand> _pendingSeekDrainCommands = new();

    // ── Position ticker worker ─────────────────────────────────────────
    private readonly IPlaybackClock _clock;
    private readonly WorkerBinding<PositionTickerWorker> _tickerBinding;

    // ── Internal bookkeeping ───────────────────────────────────────────
    private readonly ILogger<PlaybackControllerCore> _logger;
    private PlaybackError? _pendingLoadFailure;
    private int _loopCount;
    private bool _disposed;

    private const string DisposeFailureMessage = "PlaybackController is disposing.";

    /// <summary>
    /// Initializes a new <see cref="PlaybackControllerCore"/> with all three state machines
    /// configured, a session factory for creating pipeline sessions, and the dispatch
    /// loop started.
    /// </summary>
    public PlaybackControllerCore(
        ILogger<PlaybackControllerCore> logger,
        IPlaybackSessionFactory sessionFactory,
        IPlaybackClock clock,
        IOptions<FrameFlowPlaybackOptions>? playbackOptions = null
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tickerBinding = new WorkerBinding<PositionTickerWorker>(
            () => new PositionTickerWorker(_clock, _positionTickSubject, _logger),
            onError: null,
            logger: _logger
        );

        // Initialize state machines. The repeat region starts at the configured
        // initial mode (default: Off) so consumers can establish a starting repeat
        // preference via options without an imperative SetRepeatModeAsync call.
        var initialRepeatMode = playbackOptions?.Value?.InitialRepeatMode ?? RepeatMode.Off;
        _seeking = new StateMachine<SeekState, SeekTrigger>(SeekState.NotSeeking);
        _repeat = new StateMachine<RepeatMode, RepeatTrigger>(initialRepeatMode);

        ConfigureSeekingMachine();
        ConfigureRepeatMachine();

        // Start the single-threaded dispatch loop.
        _dispatchLoop = Task.Run(DispatchLoopAsync);

        // Loop-stall watchdog. The position ticker is alive precisely when this
        // failure manifests (state stays Playing and the clock keeps advancing),
        // so its 250ms cadence is the right sampling source — no extra timer.
        _loopStallSubscription = _positionTickSubject.Subscribe(OnPositionTickForLoopStall);
    }

    /// <summary>
    /// Folds one position tick through <see cref="LoopStallEvaluator"/> and raises
    /// <see cref="LoopStalled"/> once on the rising edge of a detected stall.
    /// Runs on the position ticker's loop (single producer of the evaluator state).
    /// </summary>
    private void OnPositionTickForLoopStall(TimeSpan position)
    {
        if (_disposed)
            return;

        var sample = new LoopStallSample(
            NowTicks: Stopwatch.GetTimestamp(),
            PositionTicks: position.Ticks,
            DurationTicks: _loadedDuration.Ticks,
            RepeatOne: _repeat.State == RepeatMode.One,
            Playing: IsActivelyPresenting,
            LoopCount: Volatile.Read(ref _loopCount)
        );

        var outcome = _loopStallEvaluator.Observe(in sample);
        _loopStallEvaluator = outcome.Next;

        // Mirror the current verdict into the level-triggered state that
        // GetDiagnostics reads (ADR-0034 poll surface).
        _loopStalledNow = outcome.Stalled;
        Volatile.Write(ref _loopOverrunTicks, outcome.Stalled ? outcome.OverrunTicks : 0);

        if (outcome.Stalled && !_loopWasStalled)
        {
            _loopWasStalled = true;
            var overrun = TimeSpan.FromSeconds((double)outcome.OverrunTicks / Stopwatch.Frequency);
            LoopStallMetrics.RecordLoopStall();
            LogLoopStalled(
                sample.LoopCount,
                position.TotalSeconds,
                _loadedDuration.TotalSeconds,
                overrun.TotalSeconds
            );
            _loopStalledSubject.OnNext(
                new LoopStalled(sample.LoopCount, position, _loadedDuration, overrun)
            );
        }
        else if (!outcome.Stalled)
        {
            _loopWasStalled = false;
        }
    }

    // ── IPlaybackController — Commands ─────────────────────────────────

    /// <inheritdoc />
    public Task<Result> LoadAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        return PostAndWaitAsync(
            new LoadCommand(source) { CancellationToken = cancellationToken },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<Result> UnloadAsync(CancellationToken cancellationToken = default) =>
        PostAndWaitAsync(
            new FireTriggerCommand(PlaybackTrigger.Unload)
            {
                CancellationToken = cancellationToken,
            },
            cancellationToken
        );

    /// <inheritdoc />
    public Task<Result> PlayAsync(CancellationToken cancellationToken = default) =>
        PostAndWaitAsync(
            new FireTriggerCommand(PlaybackTrigger.Play) { CancellationToken = cancellationToken },
            cancellationToken
        );

    /// <inheritdoc />
    public Task<Result> PauseAsync(CancellationToken cancellationToken = default) =>
        PostAndWaitAsync(
            new FireTriggerCommand(PlaybackTrigger.Pause) { CancellationToken = cancellationToken },
            cancellationToken
        );

    /// <inheritdoc />
    public Task<Result> SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default
    ) =>
        PostAndWaitAsync(
            new SeekCommand(position) { CancellationToken = cancellationToken },
            cancellationToken
        );

    /// <inheritdoc />
    public Task<Result> SetRepeatModeAsync(
        RepeatMode mode,
        CancellationToken cancellationToken = default
    ) =>
        PostAndWaitAsync(
            new SetRepeatCommand(mode) { CancellationToken = cancellationToken },
            cancellationToken
        );

    // ── IPlaybackController — State (read-only) ────────────────────────

    /// <inheritdoc />
    public PlaybackState State => _state.ToPublicState();

    /// <inheritdoc />
    public SeekState SeekingState => _seeking.State;

    /// <inheritdoc />
    public RepeatMode RepeatMode => _repeat.State;

    /// <inheritdoc />
    public bool IsActivelyPresenting =>
        _state == InternalPlaybackState.Playing && _seeking.State == SeekState.NotSeeking;

    /// <inheritdoc />
    public TimeSpan Position => _clock.Position;

    /// <inheritdoc />
    public TimeSpan Duration => _loadedDuration;

    /// <inheritdoc />
    public MediaInfo? MediaInfo => _loadedMediaInfo;

    /// <inheritdoc />
    /// <remarks>
    /// ADR-0034: composes the pipeline snapshot from the live session (or
    /// <see cref="PipelineDiagnosticsSnapshot.Empty"/> when unloaded) with
    /// controller-owned state. Computes A/V drift from the audio sink's
    /// presentation time and the video sink's last-presented PTS — both
    /// fields ride out of the same <c>GetPipelineDiagnostics</c> call so
    /// the drift is computed against a coherent snapshot.
    /// </remarks>
    public PlaybackDiagnosticsSnapshot GetDiagnostics()
    {
        var pipeline = _session?.GetPipelineDiagnostics() ?? PipelineDiagnosticsSnapshot.Empty;

        // A/V drift: positive = video ahead of audio. Null when either side
        // hasn't produced timed data yet. Computed from the snapshot fields
        // (no fresh subsystem read), so it inherits the snapshot's coherence.
        TimeSpan? avSyncDrift = null;
        var audioPts = pipeline.AudioSink.PresentationTime;
        var videoPts = pipeline.VideoSink.LastPresentedPresentationTime;
        if (videoPts is { } v && audioPts != TimeSpan.Zero && pipeline.AudioSink.IsActive)
        {
            avSyncDrift = v - audioPts;
        }

        var loopStalled = _loopStalledNow;
        TimeSpan? loopOverrun = loopStalled
            ? TimeSpan.FromSeconds((double)Volatile.Read(ref _loopOverrunTicks) / Stopwatch.Frequency)
            : null;

        return new PlaybackDiagnosticsSnapshot(
            State: State,
            SeekingState: SeekingState,
            RepeatMode: RepeatMode,
            Position: Position,
            Duration: Duration,
            MediaInfo: MediaInfo,
            Pipeline: pipeline,
            AvSyncDrift: avSyncDrift,
            LoopStalled: loopStalled,
            LoopOverrun: loopOverrun,
            SessionGeneration: Volatile.Read(ref _sessionGeneration)
        );
    }

    // ── IPlaybackController — Observable events ────────────────────────

    /// <inheritdoc />
    public IObservable<StateTransition<PlaybackState>> PlaybackStateChanged =>
        _playbackStateSubject;

    /// <inheritdoc />
    public IObservable<StateTransition<SeekState>> SeekStateChanged => _seekStateSubject;

    /// <inheritdoc />
    public IObservable<StateTransition<RepeatMode>> RepeatModeChanged => _repeatModeSubject;

    /// <inheritdoc />
    public IObservable<LoopRestarted> LoopRestarted => _loopRestartedSubject;

    /// <inheritdoc />
    public IObservable<LoopStalled> LoopStalled => _loopStalledSubject;

    /// <inheritdoc />
    public IObservable<PlaybackError> ErrorOccurred => _errorSubject;

    /// <inheritdoc />
    public IObservable<TimeSpan> PositionTick => _positionTickSubject;

    // ── Internal trigger posting ───────────────────────────────────────

    /// <summary>
    /// Posts an <see cref="InternalTriggerCommand"/> to the command channel without
    /// blocking. Used by session callbacks to route triggers back through the
    /// single-threaded dispatch loop.
    /// </summary>
    /// <remarks>
    /// Failures (channel full or closed) are swallowed and logged — pipeline worker
    /// threads must never block or throw on callback invocation.
    /// </remarks>
    private void PostInternalAsync(PlaybackTrigger trigger, Exception? error = null)
    {
        if (_disposed)
        {
            LogInternalTriggerIgnoredAfterDisposal(trigger.ToString());
            return;
        }

        var cmd = new InternalTriggerCommand(trigger) { Error = error };
        if (!_commandChannel.Writer.TryWrite(cmd))
        {
            LogInternalTriggerDropped(trigger.ToString());
        }
        else
        {
            LogInternalTriggerPosted(trigger.ToString());
        }
    }

    /// <summary>
    /// Launches a seek on a background task and records it as the active controller-owned
    /// operation. Completion, cancellation, and faults are serialized back through the
    /// command channel via <see cref="SeekOutcomeCommand"/>.
    /// </summary>
    /// <param name="loopRewind">
    /// When <see langword="true"/>, the session operation is the cheap
    /// <see cref="IPlaybackSession.RewindToStartAsync"/> (the <c>RepeatMode.One</c> loop
    /// boundary) instead of <see cref="IPlaybackSession.SeekAsync"/>(<paramref name="position"/>).
    /// Everything else — operation id, the active-seek cancellation registration, and the
    /// <see cref="SeekOutcomeCommand"/> completion plumbing — is identical, so the loop still
    /// flows through the seek state machine: <c>SeekStateChanged</c> observers fire,
    /// <see cref="IsActivelyPresenting"/> reports false during the rewind, and a concurrent
    /// user seek cancels the loop rewind via the same cancellation infrastructure. The loop
    /// rewind is always to <see cref="TimeSpan.Zero"/>, so callers pass that as
    /// <paramref name="position"/> for the diagnostics/log line.
    /// </param>
    private void StartSeekRunner(IPlaybackSession session, TimeSpan position, bool loopRewind = false)
    {
        var operationId = ++_nextSeekOperationId;
        var seekCancellation = new CancellationTokenSource();
        var previousSeekCancellation = _activeSeekCancellation;

        _activeSeekOperationId = operationId;
        _activeSeekCancellation = seekCancellation;
        _activeSeekTask = Task.Run(() =>
            RunSeekAsync(session, position, operationId, seekCancellation, loopRewind)
        );

        CancelSeek(previousSeekCancellation);
        LogSeekAccepted(operationId, position.TotalSeconds);
    }

    private async Task RunSeekAsync(
        IPlaybackSession session,
        TimeSpan position,
        long operationId,
        CancellationTokenSource seekCancellation,
        bool loopRewind = false
    )
    {
        try
        {
            // REVERTED 2026-06-12: route the RepeatMode.One loop
            // boundary through the full SeekAsync(0) — which rebuilds a fresh decode graph (and a
            // fresh borrowed FFmpeg device) each loop — instead of SubstrateSession's cheap
            // RewindToStartAsync override (perf 1c72925), which reused the retained graph + device
            // across loops. That warm-across-loops decode device is the suspected accumulator behind
            // the loop-boundary present-stall (the UI-thread VideoProcessorBlt wedge); a full seek
            // hands out a clean device each loop. Trades per-loop rebuild CPU for that freshness.
            // Un-revert (restore the override call) once off-thread-Blt recovery makes a wedge
            // recoverable regardless of trigger.
            if (loopRewind)
                await session.SeekAsync(TimeSpan.Zero, seekCancellation.Token).ConfigureAwait(false);
            else
                await session.SeekAsync(position, seekCancellation.Token).ConfigureAwait(false);
            await PostSeekOutcomeAsync(new SeekOutcomeCommand(operationId)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (seekCancellation.IsCancellationRequested)
        {
            await PostSeekOutcomeAsync(new SeekOutcomeCommand(operationId) { WasCanceled = true })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PostSeekOutcomeAsync(new SeekOutcomeCommand(operationId) { Error = ex })
                .ConfigureAwait(false);
        }
        finally
        {
            seekCancellation.Dispose();
        }
    }

    private async Task PostSeekOutcomeAsync(SeekOutcomeCommand outcome)
    {
        try
        {
            await _commandChannel.Writer.WriteAsync(outcome).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            LogSeekOutcomeIgnoredAfterDisposal(
                outcome.OperationId,
                outcome.WasCanceled,
                outcome.Error is not null
            );
        }
    }

    private static void CancelSeek(CancellationTokenSource? seekCancellation)
    {
        if (seekCancellation is null)
        {
            return;
        }

        try
        {
            seekCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The runner already cleaned it up.
        }
    }

    private static bool RequiresSeekDrain(PlaybackTrigger trigger) =>
        trigger is PlaybackTrigger.Pause or PlaybackTrigger.Unload;

    private void QueueSeekDrainCommand(FireTriggerCommand command)
    {
        if (_activeSeekOperationId > 0 && _pendingSeekDrainCommands.Count == 0)
        {
            LogSeekCancelRequested(_activeSeekOperationId, command.Trigger.ToString());
        }

        _pendingSeekDrainCommands.Enqueue(command);
        CancelSeek(_activeSeekCancellation);
    }

    private void HandleDisposedCommand(IPlayerCommand command)
    {
        switch (command)
        {
            case InternalTriggerCommand internalTrigger:
                LogInternalTriggerIgnoredAfterDisposal(internalTrigger.Trigger.ToString());
                command.Completion.TrySetResult(Result.Ok());
                break;
            case SeekOutcomeCommand seekOutcome:
                LogSeekOutcomeIgnoredAfterDisposal(
                    seekOutcome.OperationId,
                    seekOutcome.WasCanceled,
                    seekOutcome.Error is not null
                );
                command.Completion.TrySetResult(Result.Ok());
                break;
            default:
                command.Completion.TrySetResult(
                    Result.Fail(ErrorCategory.System, DisposeFailureMessage)
                );
                break;
        }
    }

    private void PrepareActiveSeekForDisposal()
    {
        FailPendingSeekDrainCommands(DisposeFailureMessage);

        var activeOperationId = _activeSeekOperationId;
        var activeSeekCancellation = _activeSeekCancellation;

        _activeSeekOperationId = 0;
        _activeSeekTask = null;
        _activeSeekCancellation = null;

        if (activeOperationId > 0)
        {
            LogSeekCancelRequested(activeOperationId, nameof(DisposeAsync));
        }

        CancelSeek(activeSeekCancellation);
        activeSeekCancellation?.Dispose();
    }

    private async Task NormalizeSeekStateAfterDisposalAsync()
    {
        if (_seeking.CanFire(SeekTrigger.SeekCompleted))
        {
            await _seeking.FireAsync(SeekTrigger.SeekCompleted);
        }
    }

    /// <summary>
    /// Builds the immutable callback channel that the controller injects into
    /// every session it creates. Each callback posts an internal trigger through
    /// the command channel so the dispatch loop processes session notifications
    /// against the state machines sequentially.
    /// </summary>
    private SessionCallbacks CreateSessionCallbacks() =>
        new(
            OnEndOfStream: () => PostInternalAsync(PlaybackTrigger.LastFrameRendered),
            OnWorkerFaulted: ex => PostInternalAsync(PlaybackTrigger.FatalError, ex),
            OnBufferReady: () => PostInternalAsync(PlaybackTrigger.BufferReady),
            OnBufferUnderrun: () => PostInternalAsync(PlaybackTrigger.BufferUnderrun)
        );

    // ── Pure-core EXECUTOR (architecture review §2.1, ADR-0055 sibling) ─────────
    //
    // The primary-playback machine is now driven end-to-end by the pure
    // PlaybackProtocol.Advance fold: it is both the DECISION authority (next state +
    // ordered action list) and — via RunPlaybackAsync below — the script the dispatch
    // shell EXECUTES. The Stateless OnEntry/OnExit/InternalTransition executor it
    // replaced is gone; the 40 cell-by-cell PlaybackProtocolTests plus the dispatch
    // PlaybackDispatchProtocolTests are the authority for the table, exactly as
    // ADR-0055 left the decoder one layer down. The channel-dispatch shell (ADR-0023)
    // is unchanged — this purifies only what it dispatches, not how commands are
    // serialized.
    //
    // RunPlaybackAsync is the imperative half of the Mealy split (DecodeDriver's
    // counterpart): it folds the current state through Advance, performs the effect
    // each PlaybackAction names in order, emits the OnTransitioned public-state
    // projection at the exit→entry boundary (the point Stateless flipped State and
    // raised OnTransitioned), threads the parameterized payloads (Load's source,
    // FatalError's error), and follows every FireTrigger auto-chain to the settled
    // state — the same loop PlaybackProtocolTests.Drive runs against the pure core.

    /// <summary>
    /// Snapshots the orthogonal guard inputs the pure <see cref="PlaybackProtocol"/>
    /// reads (the repeat region's mode). <see cref="PlaybackInputs.HasSession"/> is
    /// reported <see langword="true"/> here so the protocol's <c>Handled</c> verdict
    /// matches the structural permitted-trigger set exactly — the session-existence
    /// guard on the replay-from-<c>Ended</c> path is applied separately by the shell
    /// (<see cref="TryHandleReplayFromEndedAsync"/> / the <see cref="SeekCommand"/> arm),
    /// not by the <c>CanFire</c> gate.
    /// </summary>
    private PlaybackInputs CurrentPlaybackInputs() =>
        new(RepeatOne: _repeat.State == RepeatMode.One, HasSession: true);

    /// <summary>
    /// The pure-core authority for "is <paramref name="trigger"/> permitted from the
    /// current primary-playback state". Consults <see cref="PlaybackProtocol.Advance"/>;
    /// behaviour is identical to the previous Stateless <c>CanFire</c> call.
    /// </summary>
    private bool CanFirePlayback(PlaybackTrigger trigger) =>
        PlaybackProtocol.Advance(_state, trigger, CurrentPlaybackInputs()).Handled;

    /// <summary>
    /// The action interpreter — the imperative shell that executes a
    /// <see cref="PlaybackProtocol"/> decision and follows its auto-chain. Drives the
    /// machine from <see cref="_state"/> on <paramref name="trigger"/>: folds through
    /// <see cref="PlaybackProtocol.Advance"/>, performs each <see cref="PlaybackAction"/>
    /// in order, and re-enters on every <see cref="PlaybackActionKind.FireTrigger"/>
    /// until the chain settles — the runtime twin of <c>PlaybackProtocolTests.Drive</c>.
    /// </summary>
    /// <param name="trigger">The trigger to fire from the current state.</param>
    /// <param name="source">
    /// The media source carried by a <see cref="PlaybackTrigger.Load"/> — consumed by the
    /// <see cref="PlaybackActionKind.CreateSession"/> / <see cref="PlaybackActionKind.InitializeSession"/>
    /// actions. Null for every other trigger.
    /// </param>
    /// <param name="error">
    /// The error carried by a <see cref="PlaybackTrigger.FatalError"/> — consumed by the
    /// <see cref="PlaybackActionKind.RaiseError"/> action (and the <c>Error</c>-entry
    /// disposal). Null for every other trigger.
    /// </param>
    /// <remarks>
    /// A not-handled decision is a no-op (the caller's <see cref="CanFirePlayback"/>
    /// gate already decided whether an unpermitted trigger is a silent stale-drop or an
    /// <see cref="ErrorCategory.InvalidOperation"/> failure). When an
    /// <see cref="PlaybackActionKind.InitializeSession"/> or
    /// <see cref="PlaybackActionKind.WarmUp"/> action faults, the remaining actions
    /// (including any trailing auto-chain) are abandoned and the loop re-enters with
    /// <see cref="PlaybackTrigger.FatalError"/> from the destination state — exactly the
    /// in-handler error re-fire the Stateless loading entries performed.
    /// </remarks>
    private async Task RunPlaybackAsync(
        PlaybackTrigger trigger,
        IMediaSource? source = null,
        PlaybackError? error = null
    )
    {
        // The auto-chain follow-ups (HeadersReceived / MetadataParsed / BufferReady)
        // never carry a payload; only the initially-fired trigger does. Capture the
        // payloads once and let them ride the first iteration.
        PlaybackTrigger? next = trigger;
        var pendingSource = source;
        var pendingError = error;

        while (next is { } current)
        {
            next = null;

            var sourceState = _state;
            var decision = PlaybackProtocol.Advance(
                sourceState,
                current,
                CurrentPlaybackInputs()
            );
            if (!decision.Handled)
                return;

            var destination = decision.NextState;
            var changesState = destination != sourceState;
            var projectionEmitted = false;

            // Emit the OnTransitioned projection (log + public-state event) and the
            // destination-entry log at the exit→entry boundary: after the source's
            // OnExit actions, before the destination's OnEntry actions, with _state
            // already flipped — the exact ordering Stateless's HandleTransitioningTrigger
            // used (ExitAsync → State = dest → OnTransitioned → EnterStateAsync). An
            // internal transition (RunLoopRewind, destination == source) never crosses
            // the boundary, so it raises no projection — matching Stateless, which does
            // not fire OnTransitioned for an internal transition.
            void EmitTransitionBoundary()
            {
                if (projectionEmitted)
                    return;
                projectionEmitted = true;
                if (!changesState)
                    return;
                _state = destination;
                EmitPlaybackTransition(sourceState, destination, current);
                LogStateEntry(destination, current, pendingSource, pendingError);
            }

            var faulted = false;
            foreach (var action in decision.Actions)
            {
                if (action.Kind == PlaybackActionKind.FireTrigger)
                {
                    // The auto-chain is always the terminal action of a cell; run the
                    // boundary first (e.g. WarmUp's entry preceded BufferReady) then
                    // queue the follow-up for the next loop iteration.
                    EmitTransitionBoundary();
                    next = action.FollowUp;
                    continue;
                }

                // The sole OnExit effect in the whole machine is StopTicker (Playing's
                // OnExit). It must run while _state is still the source, before the
                // projection. Every other action is destination-entry work and runs
                // after the boundary.
                if (!IsExitPhaseAction(action.Kind))
                    EmitTransitionBoundary();

                var faultError = await ExecuteActionAsync(
                    action.Kind,
                    pendingSource,
                    pendingError
                )
                    .ConfigureAwait(false);
                if (faultError is not null)
                {
                    // InitializeSession / WarmUp faulted: abandon the rest of this
                    // decision (including any trailing auto-chain) and route FatalError
                    // from the destination, carrying the load/buffering failure.
                    faulted = true;
                    next = PlaybackTrigger.FatalError;
                    pendingError = faultError;
                    break;
                }
            }

            // A decision with no entry actions (or an all-exit decision) still has to
            // flip the state and project — flush the boundary if no action did.
            if (!faulted)
                EmitTransitionBoundary();

            // Payloads are spent once the decision that consumed them has run; the
            // auto-chain that follows carries none (except the FatalError error we just
            // set above, which the next iteration's RaiseError consumes).
            if (next != PlaybackTrigger.FatalError)
                pendingError = null;
            pendingSource = null;
        }
    }

    /// <summary>
    /// Whether <paramref name="kind"/> is a source-state <c>OnExit</c> effect (runs
    /// before the state flips) rather than a destination <c>OnEntry</c> effect. In this
    /// machine the only configured <c>OnExit</c> is <see cref="InternalPlaybackState.Playing"/>'s
    /// ticker stop, so <see cref="PlaybackActionKind.StopTicker"/> is the lone exit-phase
    /// action; every other action is entry-phase. Keeping it a named predicate documents
    /// the one place the lifted action list distinguishes the two halves of a transition.
    /// </summary>
    private static bool IsExitPhaseAction(PlaybackActionKind kind) =>
        kind == PlaybackActionKind.StopTicker;

    /// <summary>
    /// Performs the single effect <paramref name="kind"/> names, returning a non-null
    /// <see cref="PlaybackError"/> when the effect faulted in a way that must route the
    /// machine to <c>Error</c> (an <see cref="PlaybackActionKind.InitializeSession"/> or
    /// <see cref="PlaybackActionKind.WarmUp"/> failure), or <see langword="null"/> on
    /// success. This is the one-action-per-call effect surface the pure decision drives.
    /// </summary>
    private async Task<PlaybackError?> ExecuteActionAsync(
        PlaybackActionKind kind,
        IMediaSource? source,
        PlaybackError? error
    )
    {
        switch (kind)
        {
            case PlaybackActionKind.CreateSession:
                // Per ADR-0028 §4, callbacks are injected at construction time so the
                // callback channel is never partially wired.
                _session = _sessionFactory.CreateSession(_clock, CreateSessionCallbacks());
                Interlocked.Increment(ref _sessionGeneration);
                LogSessionCreated();
                break;

            case PlaybackActionKind.InitializeSession:
                try
                {
                    await _session!.InitializeAsync(source!).ConfigureAwait(false);
                    // Capture the loaded media snapshot for direct controller reads
                    // (ADR-0028 §6). Cleared in DisposeSessionAsync.
                    _loadedMediaInfo = _session.MediaInfo;
                    _loadedDuration = _session.MediaInfo?.Duration ?? TimeSpan.Zero;
                }
                catch (Exception ex)
                {
                    LogSessionInitializationFailed(ex.Message);
                    // Match the Stateless Initializing entry: dispose before routing the
                    // fault (the Error entry's DisposeSession is then a no-op).
                    await DisposeSessionAsync().ConfigureAwait(false);
                    var loadFailure = new PlaybackError(
                        ErrorCategory.System,
                        "Session initialization failed",
                        ex
                    );
                    _pendingLoadFailure = loadFailure;
                    return loadFailure;
                }
                break;

            case PlaybackActionKind.WarmUp:
                // Pre-warm so a video frame is at the gate before Play opens it; without
                // it hardware decoders pay cold-start on the first Play and audio runs
                // ahead of video. A no-op for audio-only sources.
                {
                    var session = _session;
                    if (session is not null)
                    {
                        try
                        {
                            await session.WarmUpAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogSessionInitializationFailed(ex.Message);
                            var bufferingFailure = new PlaybackError(
                                ErrorCategory.System,
                                "Initial buffering failed",
                                ex
                            );
                            _pendingLoadFailure = bufferingFailure;
                            return bufferingFailure;
                        }
                    }
                }
                break;

            case PlaybackActionKind.PlaySession:
                await EnterPlayingFromPlayAsync().ConfigureAwait(false);
                break;

            case PlaybackActionKind.PauseSession:
                {
                    var session = _session;
                    if (session is not null)
                        await session.PauseAsync().ConfigureAwait(false);
                }
                break;

            case PlaybackActionKind.StartTicker:
                await _tickerBinding.StartAsync().ConfigureAwait(false);
                break;

            case PlaybackActionKind.StopTicker:
                await _tickerBinding.StopAsync().ConfigureAwait(false);
                break;

            case PlaybackActionKind.FreezeClock:
                // Freeze the clock so reported Position stops climbing past Duration. The
                // documented ADR-0028 §1 exception to session-only clock mutation: at
                // end-of-stream the session takes no further action, and Play/Seek out of
                // Ended both route through session.PlayAsync / SeekAsync which resume the
                // clock, so no resume is needed here.
                _clock.Pause();
                break;

            case PlaybackActionKind.DisposeSession:
                await DisposeSessionAsync().ConfigureAwait(false);
                break;

            case PlaybackActionKind.RaiseError:
                if (error is not null)
                    _errorSubject.OnNext(error);
                break;

            case PlaybackActionKind.RunLoopRewind:
                await RunLoopRewindAsync().ConfigureAwait(false);
                break;
        }

        return null;
    }

    /// <summary>
    /// The <c>RepeatMode.One</c> loop boundary: the internal transition that never leaves
    /// <see cref="InternalPlaybackState.Playing"/>. Increments the loop counter, raises
    /// <c>LoopRestarted</c>, and routes a rewind through the seek state machine so
    /// <c>SeekStateChanged</c> observers fire, <see cref="IsActivelyPresenting"/> reports
    /// false during the loop, and a concurrent user seek can cancel it via the same
    /// cancellation infrastructure (ADR-0028 §2).
    /// </summary>
    private async Task RunLoopRewindAsync()
    {
        // Interlocked so the loop-stall watchdog, reading _loopCount from the
        // position-ticker thread, sees this increment.
        var loopCount = Interlocked.Increment(ref _loopCount);
        LogLoopRestarted(loopCount, "RepeatOne");
        _loopRestartedSubject.OnNext(new LoopRestarted(loopCount, Duration));
        if (_session is not null)
        {
            await _seeking.FireAsync(SeekTrigger.SeekRequested);
            await _seeking.FireAsync(SeekTrigger.FlushStarted);
            StartSeekRunner(_session, TimeSpan.Zero, loopRewind: true);
        }
    }

    /// <summary>
    /// Emits the public <see cref="PlaybackState"/> projection for a real state change —
    /// the body of the former <c>OnTransitionedAsync</c> handler. Logs every internal-state
    /// transition, but raises the observable only when the collapsed public projection
    /// actually changes, so observers never see a redundant <c>Loading → Loading</c> as the
    /// loading substates advance.
    /// </summary>
    private void EmitPlaybackTransition(
        InternalPlaybackState source,
        InternalPlaybackState destination,
        PlaybackTrigger trigger
    )
    {
        LogPlaybackStateTransition(source.ToString(), destination.ToString(), trigger.ToString());
        var previous = source.ToPublicState();
        var current = destination.ToPublicState();
        if (!EqualityComparer<PlaybackState>.Default.Equals(previous, current))
        {
            _playbackStateSubject.OnNext(new StateTransition<PlaybackState>(previous, current));
        }
    }

    /// <summary>
    /// Emits the destination state's entry log — the leading <c>Log…Entry</c> call each
    /// Stateless <c>OnEntry</c> handler made, preserved at the same point (right after the
    /// projection, before the entry effects). Keyed on the destination so the entry trace
    /// is unchanged from the configured machine.
    /// </summary>
    private void LogStateEntry(
        InternalPlaybackState destination,
        PlaybackTrigger trigger,
        IMediaSource? source,
        PlaybackError? error
    )
    {
        switch (destination)
        {
            case InternalPlaybackState.Initializing:
                LogInitializingEntry(source?.DisplayName ?? string.Empty);
                break;
            case InternalPlaybackState.Preparing:
                LogPreparingEntry();
                break;
            case InternalPlaybackState.InitialBuffering:
                LogInitialBufferingEntry();
                break;
            case InternalPlaybackState.Paused:
                LogPausedEntry();
                break;
            case InternalPlaybackState.Playing:
                LogPlayingEntry();
                break;
            case InternalPlaybackState.Rebuffering:
                LogRebufferingEntry();
                break;
            case InternalPlaybackState.Ended:
                LogEndedEntry();
                break;
            case InternalPlaybackState.Unloaded:
                LogUnloadedEntry();
                break;
            case InternalPlaybackState.Error:
                if (error is not null)
                    LogErrorEntry(error.Category.ToString(), error.Message);
                break;
        }
    }

    private async Task<bool> TryFirePlaybackTriggerAsync(
        IPlayerCommand command,
        PlaybackTrigger trigger
    )
    {
        if (!CanFirePlayback(trigger))
        {
            var msg = $"Cannot {trigger} from {_state}";
            LogInvalidOperation(trigger.ToString(), _state.ToString());
            command.Completion.TrySetResult(Result.Fail(ErrorCategory.InvalidOperation, msg));
            return false;
        }

        await RunPlaybackAsync(trigger);
        return true;
    }

    private async Task<bool> TryRouteEndedSeekThroughPlaybackAsync(
        IPlayerCommand command,
        TimeSpan position
    )
    {
        if (_state != InternalPlaybackState.Ended)
        {
            return true;
        }

        if (!CanFirePlayback(PlaybackTrigger.Seek))
        {
            var msg = $"Cannot Seek from {_state}";
            LogInvalidOperation("Seek", _state.ToString());
            command.Completion.TrySetResult(Result.Fail(ErrorCategory.InvalidOperation, msg));
            return false;
        }

        // Drives the playback machine Ended → InitialBuffering → (warm) → Paused; the
        // actual seek to `position` is launched by the caller via StartSeekRunner, so the
        // playback-machine Seek transition consumes no payload (its action list only warms).
        _ = position;
        await RunPlaybackAsync(PlaybackTrigger.Seek);
        return true;
    }

    private async Task<bool> TryHandleReplayFromEndedAsync(FireTriggerCommand command)
    {
        if (command.Trigger != PlaybackTrigger.Play || _state != InternalPlaybackState.Ended)
        {
            return false;
        }

        if (_session is null || _loadedSource is null)
        {
            var msg = $"Cannot {command.Trigger} from {_state}";
            LogInvalidOperation(command.Trigger.ToString(), _state.ToString());
            command.Completion.TrySetResult(Result.Fail(ErrorCategory.InvalidOperation, msg));
            return true;
        }

        LogReplayRecoveryStarted(_loadedSource.DisplayName);
        await RunPlaybackAsync(PlaybackTrigger.Unload);

        var loadResult = await LoadSourceAsync(_loadedSource).ConfigureAwait(false);
        if (!loadResult.IsSuccess)
        {
            command.Completion.TrySetResult(loadResult);
            return true;
        }

        await RunPlaybackAsync(PlaybackTrigger.Play);
        command.Completion.TrySetResult(Result.Ok());
        return true;
    }

    private async Task CompletePendingSeekDrainCommandsAsync(long operationId)
    {
        while (_pendingSeekDrainCommands.Count > 0)
        {
            var command = _pendingSeekDrainCommands.Dequeue();
            LogPlaybackTriggerAfterSeekDrain(command.Trigger.ToString(), operationId);

            try
            {
                if (!await TryFirePlaybackTriggerAsync(command, command.Trigger))
                {
                    continue;
                }

                command.Completion.TrySetResult(Result.Ok());
            }
            catch (Exception ex)
            {
                command.Completion.TrySetException(ex);
            }
        }
    }

    private void FailPendingSeekDrainCommands(string message)
    {
        while (_pendingSeekDrainCommands.Count > 0)
        {
            var command = _pendingSeekDrainCommands.Dequeue();
            command.Completion.TrySetResult(Result.Fail(ErrorCategory.System, message));
        }
    }

    private async Task HandleSeekOutcomeAsync(SeekOutcomeCommand outcome)
    {
        if (outcome.OperationId <= 0 || (outcome.WasCanceled && outcome.Error is not null))
        {
            LogMalformedSeekOutcome(
                outcome.OperationId,
                outcome.WasCanceled,
                outcome.Error is not null
            );
            return;
        }

        if (outcome.OperationId != _activeSeekOperationId)
        {
            LogStaleSeekOutcome(outcome.OperationId, _activeSeekOperationId);
            return;
        }

        var activeSeekCancellation = _activeSeekCancellation;

        _activeSeekOperationId = 0;
        _activeSeekTask = null;
        _activeSeekCancellation = null;
        activeSeekCancellation?.Dispose();

        if (_seeking.CanFire(SeekTrigger.SeekCompleted))
        {
            await _seeking.FireAsync(SeekTrigger.SeekCompleted);
        }

        if (outcome.WasCanceled)
        {
            LogSeekCancelled(outcome.OperationId);
            await CompletePendingSeekDrainCommandsAsync(outcome.OperationId);
            return;
        }

        if (outcome.Error is not null)
        {
            LogSeekFaulted(outcome.OperationId, outcome.Error.Message);
            if (CanFirePlayback(PlaybackTrigger.FatalError))
            {
                var error = new PlaybackError(
                    ErrorCategory.System,
                    outcome.Error.Message,
                    outcome.Error
                );
                await RunPlaybackAsync(PlaybackTrigger.FatalError, error: error);
            }

            FailPendingSeekDrainCommands(outcome.Error.Message);
            return;
        }

        LogSeekCompleted(outcome.OperationId);
        await CompletePendingSeekDrainCommandsAsync(outcome.OperationId);
    }

    // ── Orthogonal-region state machines (seeking, repeat) ──────────────
    //
    // The primary playback machine is no longer configured here — it is the pure
    // PlaybackProtocol fold executed by RunPlaybackAsync above. Only the seeking and
    // repeat regions remain Stateless machines (ADR-0023's orthogonal regions); they
    // were never part of the lifted transition table.

    private void ConfigureSeekingMachine()
    {
        _seeking.OnTransitionedAsync(t =>
        {
            LogSeekStateTransition(
                t.Source.ToString(),
                t.Destination.ToString(),
                t.Trigger.ToString()
            );
            _seekStateSubject.OnNext(new StateTransition<SeekState>(t.Source, t.Destination));
            return Task.CompletedTask;
        });

        _seeking
            .Configure(SeekState.NotSeeking)
            .Permit(SeekTrigger.SeekRequested, SeekState.SeekPending);

        _seeking
            .Configure(SeekState.SeekPending)
            .Permit(SeekTrigger.FlushStarted, SeekState.SeekInProgress);

        _seeking
            .Configure(SeekState.SeekInProgress)
            .Permit(SeekTrigger.SeekCompleted, SeekState.NotSeeking)
            .Permit(SeekTrigger.SeekRequested, SeekState.SeekPending);
    }

    private void ConfigureRepeatMachine()
    {
        _repeat.OnTransitionedAsync(t =>
        {
            LogRepeatModeTransition(
                t.Source.ToString(),
                t.Destination.ToString(),
                t.Trigger.ToString()
            );
            _repeatModeSubject.OnNext(new StateTransition<RepeatMode>(t.Source, t.Destination));
            return Task.CompletedTask;
        });

        _repeat
            .Configure(RepeatMode.Off)
            .Permit(RepeatTrigger.SelectOne, RepeatMode.One)
            .Permit(RepeatTrigger.SelectAll, RepeatMode.All);

        _repeat
            .Configure(RepeatMode.One)
            .Permit(RepeatTrigger.SelectOff, RepeatMode.Off)
            .Permit(RepeatTrigger.SelectAll, RepeatMode.All);

        _repeat
            .Configure(RepeatMode.All)
            .Permit(RepeatTrigger.SelectOff, RepeatMode.Off)
            .Permit(RepeatTrigger.SelectOne, RepeatMode.One);
    }

    // ── Dispatch Loop ──────────────────────────────────────────────────

    private async Task DispatchLoopAsync()
    {
        await foreach (var cmd in _commandChannel.Reader.ReadAllAsync())
        {
            try
            {
                cmd.CancellationToken.ThrowIfCancellationRequested();
                LogDispatchCommand(cmd.GetType().Name);

                if (_disposed)
                {
                    HandleDisposedCommand(cmd);
                    continue;
                }

                switch (cmd)
                {
                    case InternalTriggerCommand itc:
                        // Error triggers carry exception context that must reach the error
                        // state from any state that permits FatalError. RunPlaybackAsync
                        // routes it (DisposeSession + RaiseError) for every permitting state
                        // and no-ops for Idle / Unloaded / Error — where a worker-fault
                        // callback can only be a stale notification from an already-disposed
                        // session, and the machine's outcome is the same drop the protocol
                        // table prescribes (PlaybackProtocolTests.FatalError_RoutesToError_…).
                        if (itc.Trigger == PlaybackTrigger.FatalError && itc.Error is not null)
                        {
                            var error = new PlaybackError(
                                ErrorCategory.System,
                                itc.Error.Message,
                                itc.Error
                            );
                            await RunPlaybackAsync(PlaybackTrigger.FatalError, error: error);
                        }
                        else if (CanFirePlayback(itc.Trigger))
                        {
                            // Handled includes the RepeatMode.One LastFrameRendered
                            // internal transition (loop-vs-end): the pure core returns
                            // a handled internal decision (RunLoopRewind) that the
                            // interpreter performs without leaving Playing.
                            await RunPlaybackAsync(itc.Trigger);
                        }
                        else
                        {
                            // Stale trigger — e.g. LastFrameRendered arrived after
                            // the user paused or stopped. Drop it silently.
                            LogStaleInternalTrigger(itc.Trigger.ToString(), _state.ToString());
                        }
                        break;

                    case SeekOutcomeCommand soc:
                        await HandleSeekOutcomeAsync(soc);
                        break;

                    case FireTriggerCommand ftc:
                        if (await TryHandleReplayFromEndedAsync(ftc))
                        {
                            continue;
                        }

                        if (
                            RequiresSeekDrain(ftc.Trigger)
                            && _seeking.State == SeekState.SeekInProgress
                        )
                        {
                            QueueSeekDrainCommand(ftc);
                            continue;
                        }

                        if (!await TryFirePlaybackTriggerAsync(ftc, ftc.Trigger))
                        {
                            continue;
                        }
                        break;

                    case LoadCommand lc:
                        if (!CanFirePlayback(PlaybackTrigger.Load))
                        {
                            var msg = $"Cannot Load from {_state}";
                            LogInvalidOperation("Load", _state.ToString());
                            cmd.Completion.TrySetResult(
                                Result.Fail(ErrorCategory.InvalidOperation, msg)
                            );
                            continue;
                        }

                        var loadResult = await LoadSourceAsync(lc.Source).ConfigureAwait(false);
                        if (!loadResult.IsSuccess)
                        {
                            cmd.Completion.TrySetResult(loadResult);
                            continue;
                        }
                        break;

                    case SeekCommand sc:
                        if (_session is not null)
                        {
                            if (!await TryRouteEndedSeekThroughPlaybackAsync(cmd, sc.Position))
                            {
                                continue;
                            }

                            await _seeking.FireAsync(SeekTrigger.SeekRequested);
                            await _seeking.FireAsync(SeekTrigger.FlushStarted);
                            StartSeekRunner(_session, sc.Position);
                        }
                        else
                        {
                            var msg = $"Cannot Seek — no active session";
                            LogInvalidOperation("Seek", _state.ToString());
                            cmd.Completion.TrySetResult(
                                Result.Fail(ErrorCategory.InvalidOperation, msg)
                            );
                            continue;
                        }
                        break;

                    case SetRepeatCommand src:
                        var trigger = src.Mode switch
                        {
                            RepeatMode.Off => RepeatTrigger.SelectOff,
                            RepeatMode.One => RepeatTrigger.SelectOne,
                            RepeatMode.All => RepeatTrigger.SelectAll,
                            _ => throw new ArgumentOutOfRangeException(
                                nameof(src.Mode),
                                src.Mode,
                                null
                            ),
                        };

                        if (!_repeat.CanFire(trigger))
                        {
                            // Already in this mode — treat as success.
                            cmd.Completion.TrySetResult(Result.Ok());
                            continue;
                        }

                        await _repeat.FireAsync(trigger);
                        break;
                }

                cmd.Completion.TrySetResult(Result.Ok());
            }
            catch (OperationCanceledException)
            {
                cmd.Completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                LogDispatchException(ex.Message);
                cmd.Completion.TrySetException(ex);
            }
        }
    }

    // ── PostAndWaitAsync ───────────────────────────────────────────────

    private async Task<Result> PostAndWaitAsync(
        IPlayerCommand command,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        await using var reg = ct.Register(() => command.Completion.TrySetCanceled(ct));

        await _commandChannel.Writer.WriteAsync(command, ct);
        return await command.Completion.Task;
    }

    // ── Session lifecycle helpers ──────────────────────────────────────

    /// <summary>
    /// Disposes the current session and nulls the field. Safe to call when
    /// <see cref="_session"/> is already null.
    /// </summary>
    private async ValueTask DisposeSessionAsync()
    {
        if (_session is not null)
        {
            LogSessionDisposing();
            await _session.DisposeAsync();
            _session = null;
        }

        _loadedMediaInfo = null;
        _loadedDuration = TimeSpan.Zero;
    }

    private async Task<Result> LoadSourceAsync(IMediaSource source)
    {
        _pendingLoadFailure = null;
        await RunPlaybackAsync(PlaybackTrigger.Load, source: source).ConfigureAwait(false);

        if (_pendingLoadFailure is { } loadFailure)
        {
            _pendingLoadFailure = null;
            return Result.Fail(loadFailure);
        }

        _loadedSource = source;
        return Result.Ok();
    }

    private async Task EnterPlayingFromPlayAsync()
    {
        // Per ADR-0028 §3, the session tracks first-play vs resume internally.
        // PlayAsync takes the appropriate path without controller-side state.
        var session = _session;
        if (session is null)
            return;

        await session.PlayAsync().ConfigureAwait(false);
    }

    // ── DOT graph diagnostics (R017) ──────────────────────────────────

    /// <summary>
    /// Generates DOT graph strings for all three state machines. The seeking and repeat
    /// regions render from their live Stateless <see cref="UmlDotGraph"/>; the primary
    /// playback graph renders from the pure <see cref="PlaybackProtocol"/> transition
    /// table (its authoritative source now that the Stateless playback machine is retired).
    /// Useful for visualizing configured transitions during development and diagnostics.
    /// </summary>
    /// <returns>
    /// A <see cref="DotGraphSet"/> containing named DOT strings for the
    /// playback, seeking, and repeat state machines.
    /// </returns>
    public DotGraphSet GenerateDotGraphs()
    {
        LogDotGraphGeneration();

        var playbackDot = PlaybackProtocol.ToDotGraph();
        var seekingDot = UmlDotGraph.Format(_seeking.GetInfo());
        var repeatDot = UmlDotGraph.Format(_repeat.GetInfo());

        return new DotGraphSet(playbackDot, seekingDot, repeatDot);
    }

    // ── IAsyncDisposable ───────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Stop folding position ticks before the ticker stops emitting them.
        _loopStallSubscription?.Dispose();
        _loopStallSubscription = null;

        PrepareActiveSeekForDisposal();
        await _tickerBinding.StopAsync();

        _commandChannel.Writer.TryComplete();
        await _dispatchLoop;

        PrepareActiveSeekForDisposal();
        await NormalizeSeekStateAfterDisposalAsync();
        await DisposeSessionAsync();

        _playbackStateSubject.Dispose();
        _seekStateSubject.Dispose();
        _repeatModeSubject.Dispose();
        _loopRestartedSubject.Dispose();
        _loopStalledSubject.Dispose();
        _errorSubject.Dispose();
        _positionTickSubject.Dispose();

        LogDisposed();
    }
}
