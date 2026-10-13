// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using FrameFlow.Media;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Playback;

/// <summary>
/// An <see cref="IPlaybackSession"/> that presents an ordered, optionally
/// looping sequence of sources through ONE set of caller-supplied sinks. The
/// controller drives it as a single session; internally it composes a sequence
/// of per-item <see cref="SubstrateSession"/> runtimes over the <b>same</b> warm
/// sinks and the <b>same</b> clock, swapping only the decode runtime at each item
/// boundary. The video + audio sinks and their GPU resources are never disposed
/// here (ADR-0044), so the presenter stays warm across the whole playlist — the
/// per-item present-pipeline rebuild that a consumer pays today is eliminated.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a playlist looks to the controller.</b> Per-item end-of-stream is
/// intercepted here, not bubbled: when an item ends, this session advances to the
/// next item (disposing the finished runtime, rebasing the clock, warming and
/// playing the next) and the controller stays in <c>Playing</c>. The controller's
/// real <see cref="SessionCallbacks.OnEndOfStream"/> fires only when the queue is
/// exhausted <i>and</i> the repeat policy is not looping — so a looping playlist
/// looks like a session that simply never ends.
/// </para>
/// <para>
/// <b>Clock across items.</b> The controller's position clock is rebased to zero
/// at each boundary so every item reports a clean <c>0 → duration</c> timeline.
/// The per-item master pacing clock is selected inside each
/// <see cref="SubstrateSession"/> by whether <i>that</i> item has an activated
/// audio stream (audio sink when it does, wallclock otherwise) — which is what
/// lets one warm audio sink span a playlist of mixed audio/silent items.
/// </para>
/// <para>
/// <b>A shell around a pure core.</b> Every decision is made by
/// <see cref="PlaylistSessionProtocol.Step"/>. This class owns one channel and one reader. The
/// reader takes an input, steps it under the coordinator's lock, performs the step's actions and
/// awaits its awaited action, and feeds the outcome back, until the input is handled. Only then does
/// it take the next input. Commands post an input and wait for the core to complete them. Item
/// callbacks and the coordinator's requests post an input and return.
/// </para>
/// <para>
/// <b>Robustness.</b> An item that fails to open or start, or faults while it
/// plays, is skipped, so a single corrupt file does not kill the rotation. Each
/// such failure is reported to the controller through
/// <see cref="SessionCallbacks.OnRecoverableError"/>, which raises it on
/// <c>ErrorOccurred</c> without changing state. <see cref="PlaylistFailureGuard"/>
/// bubbles a fatal error when items keep failing in a row, rather than looping
/// over a queue that cannot play.
/// </para>
/// </remarks>
internal sealed class PlaylistSession : IPlaybackSession
{
    private readonly IPlaybackClock _clock;
    private readonly SessionCallbacks _controllerCallbacks;
    private readonly PlaylistCoordinator _coordinator;
    private readonly IPlaylistItemRuntimeFactory _itemFactory;
    private readonly IPlaylistSessionScheduler _scheduler;
    private readonly ILogger<PlaylistSession> _logger;
    private readonly bool _loadsSource;

    // Holds inputs, and the probes WhenIdleAsync posts. Unbounded, so posting never blocks: a
    // transition subscriber that skips or jumps posts from the reader's own thread.
    private readonly Channel<object> _inputs = Channel.CreateUnbounded<object>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly ConcurrentDictionary<int, PendingCommand> _commands = new();
    private readonly CancellationTokenSource _disposal = new();
    private readonly Task _reader;

    // Written by the reader only.
    private PlaylistSessionState _state = PlaylistSessionState.Initial;
    private IPlaylistItemRuntime? _runtime;

    // The runtime prepared ahead, and those discarded and still being disposed (media-clips
    // decision 8). Read and written by the reader, and by DisposeAsync once the reader has exited.
    private PreparedRuntime? _prepared;
    private readonly List<Task> _discarding = [];

    // Queue edits, delivered as QueueChanged so the session can prepare what now comes next.
    private IDisposable? _queueChanges;

    // Published by the reader after each step, for the synchronous members and the skip handler.
    private IPlaylistItemRuntime? _published;
    private int _publishedRun;
    private int _publishedGeneration;
    private SessionPresentation? _publishedPresentation;

    // Returned by the coordinator when this session attaches its skip and jump handlers.
    private object? _sessionToken;

    // Inputs posted and not yet handled.
    private int _inFlight;
    private int _nextCommand;
    private int _disposing;

    // Completed when the first DisposeAsync has finished, for any later call to wait on.
    private readonly TaskCompletionSource _disposed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <param name="coordinator">The queue this session plays, shared with the player.</param>
    /// <param name="clock">The controller's position clock, rebased at each item.</param>
    /// <param name="controllerCallbacks">Where the session reports to the controller.</param>
    /// <param name="itemFactory">Creates the runtime for each item.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="scheduler">
    /// Delivers item notifications and the coordinator's requests. Defaults to delivering at once.
    /// </param>
    /// <param name="loadsSource">
    /// Whether <see cref="InitializeAsync"/> makes the loaded source the queue's only item. Set for
    /// a controller that plays one source at a time.
    /// </param>
    /// <param name="prepareNext">
    /// Whether the session prepares the item its queue would take next while the current one plays.
    /// </param>
    public PlaylistSession(
        PlaylistCoordinator coordinator,
        IPlaybackClock clock,
        SessionCallbacks controllerCallbacks,
        IPlaylistItemRuntimeFactory itemFactory,
        ILoggerFactory? loggerFactory = null,
        IPlaylistSessionScheduler? scheduler = null,
        bool loadsSource = false,
        bool prepareNext = false
    )
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(itemFactory);

        _loadsSource = loadsSource;
        _state = PlaylistSessionState.Initial with { PrepareNext = prepareNext };
        _coordinator = coordinator;
        _clock = clock;
        _controllerCallbacks = controllerCallbacks;
        _itemFactory = itemFactory;
        _scheduler = scheduler ?? InlinePlaylistSessionScheduler.Instance;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PlaylistSession>();
        _reader = Task.Run(ReadAsync);
    }

    // ── IPlaybackSession read-only surface ──────────────────────────────────

    // These describe the runtime the session holds, which only a step replaces. A queue edit does
    // not: a removed or cleared current item keeps playing.
    public MediaInfo? MediaInfo => Volatile.Read(ref _published)?.MediaInfo;

    public TimeSpan Duration => Volatile.Read(ref _published)?.Duration ?? TimeSpan.Zero;

    public PipelineDiagnosticsSnapshot GetPipelineDiagnostics() =>
        Volatile.Read(ref _published)?.GetPipelineDiagnostics() ?? PipelineDiagnosticsSnapshot.Empty;

    // A replay from Ended loads a new PlaylistSession. The item it starts with is taken here,
    // before the controller unloads this one, so an edit in between cannot leave it nothing.
    public bool TryBeginReplay() => _coordinator.ReserveStart() is not null;

    // Read by the controller at Ended. The advance that ended the queue kept the item before it
    // reported the end-of-stream, and nothing replaces it while this session is Ended.
    public bool CanSeekFromEnded => Volatile.Read(ref _published) is not null;

    // Read by the controller's loop-stall watchdog on every position tick. While a loop is under way
    // the answer is true whatever the queue now says, so removing the item mid-repeat does not hide a
    // rewind that hangs. Otherwise the queue decides (decision 6 of ADR-0075-looping-on-both-players.md).
    /// <inheritdoc />
    /// <remarks>
    /// One volatile read of a value built by <see cref="Publish"/>. Composing it here instead —
    /// the loop flag from the session's state, the item from the coordinator's queue — would read
    /// two sources that are written at different moments: the queue is committed inside
    /// <c>_coordinator.Update</c> and the state is published after it, so a reader between the two
    /// could pair a repeat that is still under way for one item with the item that followed it.
    /// </remarks>
    public SessionPresentation Presentation =>
        Volatile.Read(ref _publishedPresentation) ?? SessionPresentation.Empty;

    // ── IPlaybackSession lifecycle ──────────────────────────────────────────

    public ValueTask InitializeAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
    {
        // A controller that plays one source at a time loads it as a queue of one. A playlist
        // player seeded the queue itself, and the source the controller passes is its first item.
        if (_loadsSource)
            _coordinator.LoadSource(source);

        // The first item is taken from the queue even when the token is already cancelled, and the
        // open then fails with the cancellation, so Initialize is never cancelled while it waits.
        return RunCommandAsync(
            id => new PlaylistSessionInput.Initialize(id),
            cancellationToken,
            cancellable: false
        );
    }

    public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(id => new PlaylistSessionInput.WarmUp(id), cancellationToken);

    public ValueTask PlayAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(id => new PlaylistSessionInput.Play(id), cancellationToken);

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(id => new PlaylistSessionInput.Pause(id), cancellationToken);

    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        RunCommandAsync(id => new PlaylistSessionInput.Seek(id, position), cancellationToken);

    public ValueTask RewindToStartAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(id => new PlaylistSessionInput.Rewind(id), cancellationToken);

    /// <summary>
    /// Detaches from the coordinator, cancels the item operation in flight, and completes once the
    /// item is disposed and the reader has handled every input.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            await _disposed.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            // A skip or jump requested from here on waits on the coordinator for the next session.
            if (Volatile.Read(ref _sessionToken) is { } token)
                _coordinator.DetachSession(token);

            // The token is cancelled before this returns. Its callbacks run on the thread pool, so
            // neither the reader's continuation nor a callback that throws runs on this thread.
            var cancelling = _disposal.CancelAsync();
            Post(new PlaylistSessionInput.Dispose());
            _inputs.Writer.TryComplete();

            try
            {
                await cancelling.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDisposalCancelFailed(_logger, ex);
            }

            await _reader.ConfigureAwait(false);
            _queueChanges?.Dispose();

            // The reader discards a prepared runtime when it handles Dispose. The disposals it
            // started are awaited here, after it has exited, so nothing else touches the list.
            DiscardPrepared();
            try
            {
                await Task.WhenAll(_discarding).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogItemDisposeFailed(_logger, ex);
            }

            // The reader disposes the runtime it holds when it handles Dispose. One is left only if
            // the reader stopped before that. The reader has exited, so nothing else reads the field.
            Volatile.Write(ref _published, null);
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync().ConfigureAwait(false);
                _runtime = null;
            }
            _disposal.Dispose();
            // The sinks, the clock, and the coordinator are owned by the caller — not disposed here
            // (ADR-0044).
        }
        finally
        {
            _disposed.TrySetResult();
        }
    }

    /// <summary>
    /// Completes once every input posted before the call, and every input those posted in turn, has
    /// been handled. For tests: a probe sent through the same channel.
    /// </summary>
    internal async Task WhenIdleAsync()
    {
        while (Volatile.Read(ref _inFlight) > 0)
        {
            var probe = new Probe();
            if (!_inputs.Writer.TryWrite(probe))
            {
                // Disposal has closed the channel; the reader handles what is left and exits.
                await _reader.ConfigureAwait(false);
                return;
            }
            await probe.Reached.Task.ConfigureAwait(false);
        }
    }

    // ── Posting ─────────────────────────────────────────────────────────────

    private async ValueTask RunCommandAsync(
        Func<int, PlaylistSessionInput> create,
        CancellationToken cancellationToken,
        bool cancellable = true
    )
    {
        // As a wait on a gate would, a command already cancelled has no effect.
        if (cancellable)
            cancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _nextCommand);
        var command = new PendingCommand(cancellationToken);
        _commands[id] = command;
        try
        {
            // The session is disposed. A disposed gate threw here before the session was a shell.
            if (!Post(create(id)))
                throw new ObjectDisposedException(nameof(PlaylistSession));

            PlaylistCommandResult result;
            using (
                cancellable
                    ? cancellationToken.Register(
                        static c => ((PendingCommand)c!).CancelWhileWaiting(),
                        command
                    )
                    : default
            )
            {
                result = await command.Completion.ConfigureAwait(false);
            }

            if (result.Kind != PlaylistOutcome.Ok)
            {
                ExceptionDispatchInfo.Throw(
                    result.Error ?? new OperationCanceledException(cancellationToken)
                );
            }
        }
        finally
        {
            _commands.TryRemove(id, out _);
        }
    }

    private bool Post(PlaylistSessionInput input)
    {
        Interlocked.Increment(ref _inFlight);
        if (_inputs.Writer.TryWrite(input))
            return true;
        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    // Reads what the input needs now, and delivers it through the scheduler.
    private void Deliver(PlaylistSessionInput input)
    {
        if (Volatile.Read(ref _disposing) != 0)
            return;
        _scheduler.Schedule(() =>
        {
            Post(input);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Callbacks handed to each item runtime. Recoverable errors bubble straight to the controller.
    /// End-of-stream, faults and the buffer callbacks are posted, tagged with the runtime's
    /// generation so a notification from a replaced runtime is dropped (#547 for the buffer ones). An end-of-stream also
    /// carries the runtime's run number, read when it is raised, so one raised before a seek or
    /// rewind of the same item is dropped too.
    /// </summary>
    private SessionCallbacks CreateItemCallbacks(int generation, Func<int> runNumber) =>
        new(
            OnEndOfStream: () =>
                Deliver(new PlaylistSessionInput.EndOfStream(generation, runNumber())),
            // How far a faulted item played is read when it faulted. The position clock starts from
            // the item's start for each item and each in-place rewind, and stops while paused. The
            // protocol subtracts the start.
            OnWorkerFaulted: ex =>
                Deliver(new PlaylistSessionInput.Fault(generation, ex, _clock.Position)),
            OnBufferReady: () => Deliver(new PlaylistSessionInput.BufferReady(generation)),
            OnBufferUnderrun: () => Deliver(new PlaylistSessionInput.BufferUnderrun(generation)),
            OnRecoverableError: _controllerCallbacks.OnRecoverableError,
            OnCurrentItemChanged: _controllerCallbacks.OnCurrentItemChanged,
            // An item runtime does not loop, and does not know the queue it sits in: this session
            // decides and reports every repeat and every item failure.
            OnLoopRestarted: static (_, _) => { },
            OnItemFailed: static (_, _, _) => { }
        );

    /// <summary>
    /// The coordinator's skip entry point. The skip is tagged with the generation and run state
    /// current when it is requested: a skip at Ended has nothing to end, and one that races an
    /// end-of-stream ends the same item.
    /// </summary>
    private void OnSkipRequested() =>
        Deliver(
            new PlaylistSessionInput.SkipRequested(
                Volatile.Read(ref _publishedGeneration),
                (PlaylistRunState)Volatile.Read(ref _publishedRun)
            )
        );

    /// <summary>
    /// The coordinator's jump entry point. The jump itself waits on the coordinator; this only asks
    /// for an advance to take it.
    /// </summary>
    private void OnJumpRequested() => Deliver(new PlaylistSessionInput.JumpRequested());

    // ── The reader ──────────────────────────────────────────────────────────

    private async Task ReadAsync()
    {
        var reader = _inputs.Reader;
        Exception? failure = null;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    if (item is Probe probe)
                    {
                        probe.Reached.TrySetResult();
                        continue;
                    }

                    try
                    {
                        await HandleAsync((PlaylistSessionInput)item).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Only a failure outside an input's handling reaches here, such as a controller
            // callback that throws while a failure is reported. The session stops taking input.
            failure = ex;
            LogReaderStopped(_logger, ex);
            _inputs.Writer.TryComplete(ex);
        }
        finally
        {
            // Nothing reads the channel from here. Complete what is left, so no caller waits.
            while (reader.TryRead(out var item))
            {
                if (item is Probe probe)
                    probe.Reached.TrySetResult();
                else
                    Interlocked.Decrement(ref _inFlight);
            }

            var stopped = new ObjectDisposedException(
                nameof(PlaylistSession),
                failure is null
                    ? "The playlist session was disposed."
                    : "The playlist session stopped after an unexpected failure."
            );
            foreach (var command in _commands.Values)
                command.Complete(PlaylistCommandResult.Failed(stopped));
        }
    }

    private async Task HandleAsync(PlaylistSessionInput input)
    {
        var commandId = CommandOf(input);
        if (commandId is { } id && (!_commands.TryGetValue(id, out var waiting) || !waiting.TryClaim()))
            return; // cancelled while it waited: no effect.

        try
        {
            var step = Step(input);
            while (true)
            {
                Publish();
                foreach (var action in step.Actions)
                    Perform(action);

                if (step.Awaited is { } awaited)
                    step = Step(await PerformAsync(awaited).ConfigureAwait(false));
                else if (!step.Done)
                    step = Step(new PlaylistSessionInput.Continue());
                else
                    break;
            }
        }
        catch (Exception ex)
        {
            // An unexpected failure in the shell. The input is abandoned: its command gets the
            // failure, and a notification's advance is fatal, as an orchestration failure was.
            _state = _state with { Work = null };
            Publish();
            if (commandId is { } failed && _commands.TryGetValue(failed, out var command))
                command.Complete(PlaylistCommandResult.Failed(ex));
            else
                _controllerCallbacks.OnWorkerFaulted(ex);
        }
    }

    private PlaylistSessionStep Step(PlaylistSessionInput input)
    {
        var context = new PlaylistStepContext(Disposing: Volatile.Read(ref _disposing) != 0);
        var state = _state;
        var (next, step) = _coordinator.Update(queue =>
        {
            var (s, q, st) = PlaylistSessionProtocol.Step(state, queue, input, context);
            return (q, (s, st));
        });
        _state = next;
        return step;
    }

    private void Publish()
    {
        Volatile.Write(ref _publishedRun, (int)_state.Run);
        Volatile.Write(ref _publishedGeneration, _state.Generation);
        // Built here, after the queue has been committed by Step, so both halves describe the
        // state this publish is for. One reference write, so a reader never sees half of it.
        var queue = _coordinator.Queue;
        Volatile.Write(
            ref _publishedPresentation,
            new SessionPresentation(
                _state.LoopUnderWay || queue.ExpectsRepeat,
                queue.Reported
            )
        );
        Volatile.Write(ref _published, _state.Item is null ? null : _runtime);
    }

    private static int? CommandOf(PlaylistSessionInput input) =>
        input switch
        {
            PlaylistSessionInput.Initialize i => i.Command,
            PlaylistSessionInput.WarmUp w => w.Command,
            PlaylistSessionInput.Play p => p.Command,
            PlaylistSessionInput.Pause p => p.Command,
            PlaylistSessionInput.Seek s => s.Command,
            PlaylistSessionInput.Rewind r => r.Command,
            _ => null,
        };

    // ── Actions ─────────────────────────────────────────────────────────────

    private void Perform(PlaylistSessionAction action)
    {
        switch (action)
        {
            case PlaylistSessionAction.AttachToCoordinator:
                if (Volatile.Read(ref _disposing) != 0)
                    break;
                var attached = _coordinator.AttachSession(OnSkipRequested, OnJumpRequested, PrepareAsync);
                _queueChanges ??= _coordinator.PlaylistChanged.Subscribe(new QueueChangedObserver(this));
                // Written with a full fence, as DisposeAsync writes its flag, so one of the two
                // sees the other: disposal may have started between the check and the attach, and
                // found nothing to detach.
                Interlocked.Exchange(ref _sessionToken, attached);
                if (Volatile.Read(ref _disposing) != 0)
                    _coordinator.DetachSession(attached);
                break;

            case PlaylistSessionAction.StopClock:
                _clock.Stop();
                break;

            case PlaylistSessionAction.PrepareItem prepare:
                Prepare(prepare.Item, prepare.Command);
                break;

            case PlaylistSessionAction.DiscardPrepared:
                DiscardPrepared();
                break;

            case PlaylistSessionAction.ReportCurrentItemChanged changed:
                _controllerCallbacks.OnCurrentItemChanged(changed.Info);
                break;

            case PlaylistSessionAction.ReportItemFailed failed:
                ReportItemFailed(failed);
                break;

            case PlaylistSessionAction.ReportEndOfStream:
                _controllerCallbacks.OnEndOfStream();
                break;

            case PlaylistSessionAction.ReportBufferUnderrun:
                _controllerCallbacks.OnBufferUnderrun();
                break;

            case PlaylistSessionAction.ReportBufferReady:
                _controllerCallbacks.OnBufferReady();
                break;

            case PlaylistSessionAction.ReportLoopRestarted loop:
                _controllerCallbacks.OnLoopRestarted(loop.LoopCount, loop.Item);
                break;

            case PlaylistSessionAction.ReportFatal fatal:
                _controllerCallbacks.OnWorkerFaulted(fatal.Error);
                break;

            case PlaylistSessionAction.RaiseTransition transition:
                _coordinator.RaiseTransition(
                    transition.Item,
                    transition.Info,
                    transition.Index,
                    transition.Wrapped,
                    transition.Previous,
                    transition.Reason
                );
                break;

            case PlaylistSessionAction.CompleteCommand complete:
                if (_commands.TryGetValue(complete.Command, out var command))
                    command.Complete(complete.Result);
                break;

            case PlaylistSessionAction.Log log:
                WriteLog(log);
                break;

            default:
                throw new InvalidOperationException($"'{action}' is not an immediate action.");
        }
    }

    private async ValueTask<PlaylistSessionInput.Outcome> PerformAsync(PlaylistSessionAction action)
    {
        switch (action)
        {
            case PlaylistSessionAction.OpenItem open:
            {
                // The runtime raises callbacks only once it is initialized, so it is assigned before
                // the end-of-stream callback reads it.
                IPlaylistItemRuntime? runtime = null;
                runtime = _itemFactory.CreateItem(
                    _clock,
                    CreateItemCallbacks(open.Generation, () => runtime!.RunNumber)
                );
                _runtime = runtime;
                return await RunItemAsync(
                        open.Command,
                        t => runtime.InitializeAsync(open.Source, open.Start, open.End, t)
                    )
                    .ConfigureAwait(false);
            }

            case PlaylistSessionAction.WarmUpItem warm:
                return await RunItemAsync(warm.Command, t => _runtime!.WarmUpAsync(t))
                    .ConfigureAwait(false);

            case PlaylistSessionAction.PlayItem play:
                return await RunItemAsync(play.Command, t => _runtime!.PlayAsync(t))
                    .ConfigureAwait(false);

            case PlaylistSessionAction.PauseItem pause:
                return await RunItemAsync(pause.Command, t => _runtime!.PauseAsync(t))
                    .ConfigureAwait(false);

            case PlaylistSessionAction.SeekItem seek:
                return await RunItemAsync(seek.Command, t => _runtime!.SeekAsync(seek.Position, t))
                    .ConfigureAwait(false);

            case PlaylistSessionAction.RewindItem rewind:
                return await RunItemAsync(rewind.Command, t => _runtime!.RewindToAsync(rewind.Position, rewind.End, t))
                    .ConfigureAwait(false);

            case PlaylistSessionAction.PromotePrepared promote:
                return await PromoteAsync(promote).ConfigureAwait(false);

            case PlaylistSessionAction.DisposeItem:
            {
                var runtime = _runtime;
                _runtime = null;
                if (runtime is not null)
                {
                    try
                    {
                        await runtime.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogItemDisposeFailed(_logger, ex);
                    }
                }
                return new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok);
            }

            default:
                throw new InvalidOperationException($"'{action}' is not an awaited action.");
        }
    }

    // Runs an operation on the runtime with the command's token, if any, linked to the disposal
    // token, and describes how it finished.
    private async ValueTask<PlaylistSessionInput.Outcome> RunItemAsync(
        int? commandId,
        Func<CancellationToken, ValueTask> operation
    )
    {
        var runtime = _runtime!;
        var commandToken =
            commandId is { } id && _commands.TryGetValue(id, out var command)
                ? command.Token
                : CancellationToken.None;

        using var linked = commandToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(commandToken, _disposal.Token)
            : null;
        var token = linked?.Token ?? _disposal.Token;

        try
        {
            await operation(token).ConfigureAwait(false);
            return new PlaylistSessionInput.Outcome(
                PlaylistOutcome.Ok,
                runtime.RunNumber,
                runtime.MediaInfo
            );
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            return new PlaylistSessionInput.Outcome(
                PlaylistOutcome.Cancelled,
                runtime.RunNumber,
                Error: ex
            );
        }
        catch (Exception ex)
        {
            return new PlaylistSessionInput.Outcome(
                PlaylistOutcome.Failed,
                runtime.RunNumber,
                Error: ex
            );
        }
    }

    /// <summary>
    /// Logs a failed item and reports it to the controller, which raises it on <c>ItemFailed</c>
    /// and <c>ErrorOccurred</c> and stays in its current state.
    /// </summary>
    /// <remarks>
    /// It goes through <see cref="SessionCallbacks.OnItemFailed"/> rather than
    /// <see cref="SessionCallbacks.OnRecoverableError"/>, which an item runtime also uses for
    /// errors that have no item — a lateness-recovery fault is one. The controller builds one
    /// <see cref="PlaybackError"/> for both events from what this passes, so they share it by
    /// reference.
    /// </remarks>
    private void ReportItemFailed(PlaylistSessionAction.ReportItemFailed failed)
    {
        var source = failed.Item.Source.DisplayName;
        string what;
        if (failed.What == PlaylistItemFailure.FaultedDuringPlayback)
        {
            LogItemFaulted(_logger, source, failed.Error);
            what = "faulted during playback";
        }
        else
        {
            LogItemSkipped(_logger, source, failed.Error);
            what = "could not be started";
        }

        _controllerCallbacks.OnItemFailed(
            failed.Item,
            failed.What,
            new PlaybackError(
                ErrorCategory.System,
                $"Playlist item '{source}' {what}: {failed.Error?.Message}",
                failed.Error
            )
        );
    }

    private void WriteLog(PlaylistSessionAction.Log log)
    {
        switch (log.Event)
        {
            case PlaylistSessionLog.AdvanceIgnoredAtEnd:
                LogAdvanceIgnoredAtEnd(_logger, log.Faulted);
                break;
            case PlaylistSessionLog.StaleEndOfStream:
                LogStaleEndOfStream(_logger, log.Run, log.CurrentRun);
                break;
            case PlaylistSessionLog.ReplayFellBack:
                LogReplayFellBack(_logger, log.Source ?? "(unknown)", log.Error);
                break;
            case PlaylistSessionLog.KeptItemPauseFailed:
                LogKeptItemPauseFailed(_logger, log.Source ?? "(unknown)", log.Error);
                break;
            case PlaylistSessionLog.PreparedFellBack:
                LogPreparedFellBack(_logger, log.Source ?? "(unknown)", log.Error);
                break;
        }
    }

    // ── Preparing ahead (media-clips decision 8) ────────────────────────────

    /// <summary>
    /// Prepares <paramref name="item"/> ahead, through the protocol, and completes when it is
    /// ready. The coordinator's prepare entry point.
    /// </summary>
    internal async Task<Result> PrepareAsync(PlaylistItem item, CancellationToken cancellationToken)
    {
        try
        {
            await RunCommandAsync(id => new PlaylistSessionInput.PrepareRequested(item, id), cancellationToken)
                .ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(
                ErrorCategory.InvalidOperation,
                $"Preparing '{item}' stopped before it was ready: another item was prepared, or it was discarded.",
                ex
            );
        }
        catch (ObjectDisposedException ex)
        {
            return Result.Fail(ErrorCategory.InvalidOperation, $"Cannot prepare '{item}': the player is disposed.", ex);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fail(ErrorCategory.InvalidOperation, ex.Message, ex);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCategory.Source, $"Preparing '{item}' failed: {ex.Message}", ex);
        }
    }

    // Starts preparing item in the background, detached, replacing any other prepared runtime. A
    // request for the item already being prepared joins it.
    private void Prepare(PlaylistItem item, int? command)
    {
        if (_prepared is { } existing && ReferenceEquals(existing.Item, item))
        {
            if (command is { } joining)
                existing.Await(joining, this);
            return;
        }

        DiscardPrepared();
        var prepared = new PreparedRuntime(item);
        prepared.Runtime = _itemFactory.CreatePreparedItem(_clock, CreatePreparedCallbacks(prepared));
        if (command is { } id)
            prepared.Await(id, this);
        prepared.Ready = Task.Run(() => PrepareRuntimeAsync(prepared));
        _prepared = prepared;
    }

    private async Task<PlaylistSessionInput.Outcome> PrepareRuntimeAsync(PreparedRuntime prepared)
    {
        var token = prepared.Cancellation.Token;
        PlaylistSessionInput.Outcome outcome;
        try
        {
            var item = prepared.Item;
            await prepared.Runtime.InitializeAsync(item.Source, item.Start, item.End, token).ConfigureAwait(false);
            await prepared.Runtime.WarmUpAsync(token).ConfigureAwait(false);
            outcome = new(PlaylistOutcome.Ok, prepared.Runtime.RunNumber, prepared.Runtime.MediaInfo);
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            outcome = new(PlaylistOutcome.Cancelled, Error: ex);
        }
        catch (Exception ex)
        {
            LogPrepareFailed(_logger, prepared.Item.ToString(), ex);
            outcome = new(PlaylistOutcome.Failed, Error: ex);
        }

        prepared.Settle(outcome, this);
        return outcome;
    }

    // Makes the prepared runtime the current one once it is ready, or reports why it cannot be.
    private async ValueTask<PlaylistSessionInput.Outcome> PromoteAsync(PlaylistSessionAction.PromotePrepared promote)
    {
        var prepared = _prepared;
        if (prepared is null || !ReferenceEquals(prepared.Item, promote.Item))
        {
            return new(
                PlaylistOutcome.Failed,
                Error: new InvalidOperationException($"Nothing is prepared for '{promote.Item}'.")
            );
        }
        _prepared = null;

        PlaylistSessionInput.Outcome ready;
        try
        {
            ready = await prepared.Ready.WaitAsync(_disposal.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (_disposal.IsCancellationRequested)
        {
            Retire(prepared);
            return new(PlaylistOutcome.Cancelled, Error: ex);
        }

        if (ready.Kind != PlaylistOutcome.Ok)
        {
            Retire(prepared);
            return ready.Kind == PlaylistOutcome.Cancelled ? ready with { Kind = PlaylistOutcome.Failed } : ready;
        }

        // From here its notifications carry the item's generation, and it acts on the clock and
        // the audio sink it shares with the playlist.
        Volatile.Write(ref prepared.Generation, promote.Generation);
        prepared.Runtime.Attach();
        _runtime = prepared.Runtime;
        prepared.Cancellation.Dispose();
        LogPromoted(_logger, prepared.Item.ToString());
        return new(PlaylistOutcome.Ok, prepared.Runtime.RunNumber, prepared.Runtime.MediaInfo);
    }

    private void DiscardPrepared()
    {
        var prepared = _prepared;
        if (prepared is null)
            return;
        _prepared = null;
        Retire(prepared);
    }

    // Stops a prepared runtime that will not be used, completes anything waiting on it, and
    // disposes it in the background. A detached runtime leaves the shared clock and sinks alone.
    private void Retire(PreparedRuntime prepared)
    {
        try
        {
            prepared.Cancellation.Cancel();
        }
        catch (ObjectDisposedException) { }

        prepared.Settle(
            new PlaylistSessionInput.Outcome(PlaylistOutcome.Cancelled, Error: new OperationCanceledException()),
            this
        );
        _discarding.RemoveAll(t => t.IsCompleted);
        _discarding.Add(DisposePreparedAsync(prepared));
    }

    private async Task DisposePreparedAsync(PreparedRuntime prepared)
    {
        try
        {
            await prepared.Ready.ConfigureAwait(false);
        }
        catch
        {
            // Its outcome was already reported to anything waiting.
        }

        try
        {
            await prepared.Runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogItemDisposeFailed(_logger, ex);
        }
        prepared.Cancellation.Dispose();
    }

    // The prepared runtime's notifications are dropped until it is promoted: until then nothing
    // it raises is about the item the playlist is playing.
    private SessionCallbacks CreatePreparedCallbacks(PreparedRuntime prepared) =>
        new(
            OnEndOfStream: () =>
            {
                if (Volatile.Read(ref prepared.Generation) is var g and >= 0)
                    Deliver(new PlaylistSessionInput.EndOfStream(g, prepared.Runtime.RunNumber));
            },
            OnWorkerFaulted: ex =>
            {
                if (Volatile.Read(ref prepared.Generation) is var g and >= 0)
                    Deliver(new PlaylistSessionInput.Fault(g, ex, _clock.Position));
            },
            OnBufferReady: () =>
            {
                if (Volatile.Read(ref prepared.Generation) is var g and >= 0)
                    Deliver(new PlaylistSessionInput.BufferReady(g));
            },
            OnBufferUnderrun: () =>
            {
                if (Volatile.Read(ref prepared.Generation) is var g and >= 0)
                    Deliver(new PlaylistSessionInput.BufferUnderrun(g));
            },
            OnRecoverableError: error =>
            {
                if (Volatile.Read(ref prepared.Generation) >= 0)
                    _controllerCallbacks.OnRecoverableError(error);
            },
            OnCurrentItemChanged: info =>
            {
                if (Volatile.Read(ref prepared.Generation) >= 0)
                    _controllerCallbacks.OnCurrentItemChanged(info);
            },
            OnLoopRestarted: static (_, _) => { },
            OnItemFailed: static (_, _, _) => { }
        );

    private void CompleteCommand(int command, PlaylistSessionInput.Outcome outcome)
    {
        if (_commands.TryGetValue(command, out var pending))
            pending.Complete(PlaylistCommandResult.From(outcome));
    }

    /// <summary>A runtime prepared ahead for one item, and the commands waiting on it.</summary>
    private sealed class PreparedRuntime(PlaylistItem item)
    {
        private readonly Lock _gate = new();
        private readonly List<int> _waiting = [];
        private PlaylistSessionInput.Outcome? _settled;

        public PlaylistItem Item { get; } = item;

        public IPlaylistItemRuntime Runtime { get; set; } = null!;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<PlaylistSessionInput.Outcome> Ready { get; set; } = Task.FromResult(
            new PlaylistSessionInput.Outcome(PlaylistOutcome.Cancelled)
        );

        // The generation its notifications carry once it is promoted, or -1 before.
        public int Generation = -1;

        // Completes command when the preparation settles, or now if it has.
        public void Await(int command, PlaylistSession session)
        {
            PlaylistSessionInput.Outcome? settled;
            lock (_gate)
            {
                settled = _settled;
                if (settled is null)
                    _waiting.Add(command);
            }
            if (settled is { } outcome)
                session.CompleteCommand(command, outcome);
        }

        // The first outcome wins: a preparation that finished is not undone by a later discard.
        public void Settle(PlaylistSessionInput.Outcome outcome, PlaylistSession session)
        {
            int[] waiting;
            lock (_gate)
            {
                if (_settled is not null)
                    return;
                _settled = outcome;
                waiting = [.. _waiting];
                _waiting.Clear();
            }
            foreach (var command in waiting)
                session.CompleteCommand(command, outcome);
        }
    }

    private sealed class QueueChangedObserver(PlaylistSession session) : IObserver<PlaylistSnapshot>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(PlaylistSnapshot value) => session.Deliver(new PlaylistSessionInput.QueueChanged());
    }

    // ── Commands in flight ──────────────────────────────────────────────────

    private sealed class PendingCommand(CancellationToken token)
    {
        private const int Waiting = 0;
        private const int Claimed = 1;
        private const int Finished = 2;

        private readonly TaskCompletionSource<PlaylistCommandResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _state;

        public CancellationToken Token => token;

        public Task<PlaylistCommandResult> Completion => _completion.Task;

        /// <summary>Takes the command for the reader, unless it was cancelled while it waited.</summary>
        public bool TryClaim() => Interlocked.CompareExchange(ref _state, Claimed, Waiting) == Waiting;

        /// <summary>
        /// Cancels a command the reader has not taken. A command the reader has taken is cancelled
        /// through its item operation's token.
        /// </summary>
        public void CancelWhileWaiting()
        {
            if (Interlocked.CompareExchange(ref _state, Finished, Waiting) == Waiting)
            {
                _completion.TrySetResult(
                    new PlaylistCommandResult(
                        PlaylistOutcome.Cancelled,
                        new OperationCanceledException(token)
                    )
                );
            }
        }

        public void Complete(PlaylistCommandResult result)
        {
            Volatile.Write(ref _state, Finished);
            _completion.TrySetResult(result);
        }
    }

    private sealed class Probe
    {
        public TaskCompletionSource Reached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ── Logging ─────────────────────────────────────────────────────────────

    private static readonly Action<ILogger, string, Exception?> LogItemFaultedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogItemFaulted)),
            "Playlist item '{Source}' faulted during playback; advancing."
        );

    private static readonly Action<ILogger, string, Exception?> LogItemSkippedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogItemSkipped)),
            "Playlist item '{Source}' could not be started; skipping."
        );

    private static readonly Action<ILogger, Exception?> LogItemDisposeFailedMessage =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3, nameof(LogItemDisposeFailed)),
            "Disposing a finished playlist item runtime threw."
        );

    private static readonly Action<ILogger, string, Exception?> LogReplayFellBackMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogReplayFellBack)),
            "In-place rewind of same-source playlist item '{Source}' faulted; "
                + "falling back to a full rebuild of the item."
        );

    private static readonly Action<ILogger, bool, Exception?> LogAdvanceIgnoredAtEndMessage =
        LoggerMessage.Define<bool>(
            LogLevel.Debug,
            new EventId(5, nameof(LogAdvanceIgnoredAtEnd)),
            "Playlist advance ignored: the queue has ended (faulted: {Faulted})."
        );

    private static readonly Action<ILogger, int, int, Exception?> LogStaleEndOfStreamMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Debug,
            new EventId(6, nameof(LogStaleEndOfStream)),
            "Playlist end-of-stream ignored: raised by run {Run}, and a seek or rewind has "
                + "since started run {CurrentRun}."
        );

    private static readonly Action<ILogger, string, Exception?> LogKeptItemPauseFailedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(7, nameof(LogKeptItemPauseFailed)),
            "Pausing skipped playlist item '{Source}' at the end of the queue threw; "
                + "disposing it instead."
        );

    private static readonly Action<ILogger, Exception?> LogReaderStoppedMessage =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(8, nameof(LogReaderStopped)),
            "The playlist session stopped taking input after an unexpected failure."
        );

    private static readonly Action<ILogger, Exception?> LogDisposalCancelFailedMessage =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(9, nameof(LogDisposalCancelFailed)),
            "A callback threw while disposal cancelled the playlist item operation in flight."
        );

    private static void LogReaderStopped(ILogger logger, Exception error) =>
        LogReaderStoppedMessage(logger, error);

    private static void LogDisposalCancelFailed(ILogger logger, Exception error) =>
        LogDisposalCancelFailedMessage(logger, error);

    private static void LogStaleEndOfStream(ILogger logger, int run, int currentRun) =>
        LogStaleEndOfStreamMessage(logger, run, currentRun, null);

    private static void LogKeptItemPauseFailed(ILogger logger, string source, Exception? error) =>
        LogKeptItemPauseFailedMessage(logger, source, error);

    private static void LogItemFaulted(ILogger logger, string source, Exception? error) =>
        LogItemFaultedMessage(logger, source, error);

    private static void LogItemSkipped(ILogger logger, string source, Exception? error) =>
        LogItemSkippedMessage(logger, source, error);

    private static void LogItemDisposeFailed(ILogger logger, Exception? error) =>
        LogItemDisposeFailedMessage(logger, error);

    private static void LogReplayFellBack(ILogger logger, string source, Exception? error) =>
        LogReplayFellBackMessage(logger, source, error);

    private static void LogAdvanceIgnoredAtEnd(ILogger logger, bool faulted) =>
        LogAdvanceIgnoredAtEndMessage(logger, faulted, null);

    private static readonly Action<ILogger, string, Exception?> LogPromotedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(10, nameof(LogPromoted)),
            "Playlist item '{Item}' starts on the runtime prepared ahead for it."
        );

    private static readonly Action<ILogger, string, Exception?> LogPreparedFellBackMessage =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(11, nameof(LogPreparedFellBack)),
            "The runtime prepared for playlist item '{Source}' could not be used; opening it instead."
        );

    private static readonly Action<ILogger, string, Exception?> LogPrepareFailedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(12, nameof(LogPrepareFailed)),
            "Preparing playlist item '{Item}' ahead failed."
        );

    private static void LogPromoted(ILogger logger, string item) => LogPromotedMessage(logger, item, null);

    private static void LogPreparedFellBack(ILogger logger, string source, Exception? error) =>
        LogPreparedFellBackMessage(logger, source, error);

    private static void LogPrepareFailed(ILogger logger, string item, Exception error) =>
        LogPrepareFailedMessage(logger, item, error);
}
