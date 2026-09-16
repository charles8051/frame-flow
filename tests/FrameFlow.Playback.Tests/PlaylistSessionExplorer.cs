using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using StepFunction = System.Func<
    FrameFlow.Playback.PlaylistSessionState,
    FrameFlow.Playback.PlaylistQueue,
    FrameFlow.Playback.PlaylistSessionInput,
    FrameFlow.Playback.PlaylistStepContext,
    (
        FrameFlow.Playback.PlaylistSessionState State,
        FrameFlow.Playback.PlaylistQueue Queue,
        FrameFlow.Playback.PlaylistSessionStep Step
    )
>;

namespace FrameFlow.Playback.Tests;

/// <summary>A controller call in an explorer scenario. The controller makes them in order.</summary>
internal enum ExplorerCommand
{
    Initialize,
    WarmUp,
    Play,
    Pause,
    Seek,
}

/// <summary>
/// Something outside the controller that can happen at any point in an explorer scenario, once per
/// occurrence listed.
/// </summary>
internal abstract record ExplorerEvent
{
    private ExplorerEvent() { }

    /// <summary>The caller skips.</summary>
    public sealed record Skip : ExplorerEvent;

    /// <summary>The caller jumps to the item named <paramref name="Target"/>.</summary>
    public sealed record Jump(string Target) : ExplorerEvent;

    /// <summary>The caller removes the queue's current item.</summary>
    public sealed record RemoveCurrent : ExplorerEvent;

    /// <summary>The caller enqueues a one-shot item.</summary>
    public sealed record Enqueue(string Source) : ExplorerEvent;

    /// <summary>The runtime's launched run raises its end-of-stream.</summary>
    public sealed record EndOfStream : ExplorerEvent;

    /// <summary>The runtime's worker faults.</summary>
    public sealed record Fault : ExplorerEvent;

    /// <summary>The controller disposes the session.</summary>
    public sealed record Dispose : ExplorerEvent;
}

/// <summary>The item actions an explorer scenario lets fail.</summary>
[Flags]
internal enum ExplorerFailures
{
    None = 0,
    Open = 1,
    WarmUp = 2,
    Play = 4,
    Pause = 8,
    Seek = 16,
    Rewind = 32,
}

/// <summary>
/// A starting playlist, the controller's calls, the events that can happen in between, and the
/// outcomes item actions may take.
/// </summary>
internal sealed record ExplorerScenario(
    string Name,
    RepeatMode Repeat,
    string[] Playlist,
    ExplorerCommand[] Commands,
    ExplorerEvent[] Events
)
{
    /// <summary>The item actions that may fail.</summary>
    public ExplorerFailures Failures { get; init; }

    /// <summary>Whether the controller may cancel the item action serving one of its commands.</summary>
    public bool CommandsCanBeCancelled { get; init; }

    public override string ToString() => Name;
}

/// <summary>The invariants the explorer checks, by the names its violations carry.</summary>
internal static class ExplorerInvariants
{
    public const string CompletesTwice =
        "Commands: a command completes more than once";

    public const string CompletesEarly =
        "Commands: a command completes before the input that serves it is handled";

    public const string NeverCompletes =
        "Commands: a command never completes";

    public const string AdvancesBeforeTheFirstPlay =
        "Presenting: an advance opens an item before the first play";

    public const string RewindsAFaultedItem =
        "Presenting: an item whose worker faulted is rewound in place";

    public const string PlaysWhileNotPlaying =
        "Presenting: a play while not playing";

    public const string PlaysOrSeeksAfterTheEnd =
        "The end: a play or seek after the end of the queue was reported, before a warm-up out of Ended";

    public const string OpensWhileLive =
        "Items: a runtime is opened while another is live";

    public const string DisposesNothing =
        "Items: a runtime is disposed that is not live";

    public const string CallsNoRuntime =
        "Items: an item call has no opened runtime";

    public const string LeavesARuntime =
        "Items: a runtime is left undisposed after disposal";

    public const string ReplacedRuntimeActs =
        "Notifications: a notification from a replaced runtime has an effect";

    public const string ReplacedRunActs =
        "Notifications: an end-of-stream from a run that was replaced has an effect";

    public const string ReportsALoopThatIsNotOne =
        "Reports: a loop is reported by an input that did not begin with the end of a played item's current run";

    public const string ChangeWithoutTransition =
        "Reports: a changed item is reported without its transition straight after";

    public const string TwoTransitions =
        "Reports: one step raises two transitions";

    public const string ReportsAfterFatal =
        "Reports: a controller callback after a fatal report";

    public const string ReportsWhileDisposing =
        "Reports: a controller callback during disposal";

    public const string StartsAfterGivingUp =
        "Giving up: an item starts after a fatal report";

    public const string CursorOutside =
        "The queue: the cursor is outside the playlist";

    public const string ItemTwice =
        "The queue: an item is in more than one place";

    public const string RemovedTakenAgain =
        "The queue: a removed current item is taken again";

    public const string PlayLeavesLatch =
        "Jumps and latches: a Play leaves an advance latched";

    public const string JumpLeftPending =
        "Jumps and latches: a jump is left pending while the session could take it";

    public const string PlayingNothing =
        "At quiescence: playing with no item";

    public const string PlayingNotCurrent =
        "At quiescence: the item playing is not the queue's started current item";

    public const string PlayingUnstarted =
        "At quiescence: playing an item that has not started";

    public const string LatchedWhilePlaying =
        "At quiescence: an advance is latched while playing";
}

/// <summary>An invariant the explorer found broken, with the ordering that broke it.</summary>
internal sealed record ExplorerViolation(string Invariant, string Detail, IReadOnlyList<string> Trace)
{
    public override string ToString() =>
        $"{Invariant}: {Detail}{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Trace);
}

/// <summary>What an exploration visited and found.</summary>
internal sealed record ExplorerResult(int States, IReadOnlyList<ExplorerViolation> Violations)
{
    public IEnumerable<string> Invariants => Violations.Select(v => v.Invariant).Distinct();
}

/// <summary>
/// Enumerates the orderings of a scenario over <see cref="PlaylistSessionProtocol.Step"/>, and checks
/// the invariants of decision 7 of <c>docs/adr/ADR-0076-playlist-session-protocol.md</c> along each one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The world.</b> A state of the exploration is the core's state and queue, the shell's channel,
/// the input the reader is handling and what it waits for, the item runtime as
/// <see cref="PlaylistItemModel"/> describes it, the controller's progress through its calls, the
/// events still to happen, and what has been reported.
/// </para>
/// <para>
/// <b>The moves.</b> From each state, every move that could happen next is explored: the reader takes
/// its next step (the next input, a continue, or an outcome of the action it awaits, one move per
/// outcome the model allows); the controller makes its next call once its previous one has completed;
/// or one of the scenario's events happens. The shell's loop is run over the core exactly as
/// <see cref="PlaylistSession"/> runs it, one step per move, so events interleave between steps.
/// </para>
/// <para>
/// <b>Pruning.</b> A visited set holds a hash of each state, including the inputs still to arrive, so
/// each state is explored once. Every state where the reader is idle and the channel is empty is a
/// point of quiescence: nothing new has to arrive, so the liveness invariants are checked there.
/// </para>
/// </remarks>
internal sealed class PlaylistSessionExplorer
{
    /// <summary>
    /// The invariant decision 7 names as an expected failure: the end-of-queue record leaves open a
    /// seek or rewind before an item's first play, which starts it while the controller is not playing.
    /// </summary>
    public const string ExpectedFailure =
        "Presenting: a seek or rewind of an item that has not played, while not playing";

    private const int StateBound = 400_000;

    private static readonly MediaInfo Info = new("model", TimeSpan.FromSeconds(3), [], []);
    private static readonly Exception ModelFailure = new InvalidOperationException("model failure");
    private static readonly Exception ModelCancellation = new OperationCanceledException("model cancellation");
    private static readonly TimeSpan SeekPosition = TimeSpan.FromSeconds(1);

    private readonly ExplorerScenario _scenario;
    private readonly StepFunction _step;
    private readonly HashSet<UInt128> _visited = [];
    private readonly Dictionary<string, ExplorerViolation> _violations = [];
    private readonly List<string> _trace = [];

    private PlaylistSessionExplorer(ExplorerScenario scenario, StepFunction step)
    {
        _scenario = scenario;
        _step = step;
    }

    /// <summary>
    /// Explores <paramref name="scenario"/> over <paramref name="step"/>, which defaults to the core.
    /// </summary>
    public static ExplorerResult Explore(ExplorerScenario scenario, StepFunction? step = null)
    {
        var explorer = new PlaylistSessionExplorer(scenario, step ?? PlaylistSessionProtocol.Step);
        var items = scenario.Playlist.Select(n => new PlaylistItem(new NamedSource(n)));
        explorer.Visit(
            new World
            {
                State = PlaylistSessionState.Initial,
                Queue = PlaylistQueue.Create(items, scenario.Repeat),
                Remaining = [.. scenario.Events],
            }
        );
        return new ExplorerResult(explorer._visited.Count, [.. explorer._violations.Values]);
    }

    // ── The search ──────────────────────────────────────────────────────────

    private void Visit(World world)
    {
        if (!_visited.Add(Hash(world)))
            return;
        if (_visited.Count > StateBound)
        {
            throw new InvalidOperationException(
                $"Scenario '{_scenario.Name}' passed {StateBound} states. Make it smaller."
            );
        }

        if (world.Handling is null && world.Channel.IsEmpty)
            CheckQuiescence(world);

        foreach (var (label, next) in Moves(world))
        {
            _trace.Add(label);
            if (next is not null)
                Visit(next);
            _trace.RemoveAt(_trace.Count - 1);
        }
    }

    private IEnumerable<(string Label, World? Next)> Moves(World world)
    {
        // The reader.
        if (world.Awaiting is { } awaited)
        {
            foreach (var (label, prepared, outcome) in Outcomes(world, awaited))
            {
                _trace.Add(label);
                var next = prepared is null ? null : RunStep(prepared, outcome!);
                _trace.RemoveAt(_trace.Count - 1);
                yield return (label, next);
            }
        }
        else if (world.ContinueDue)
        {
            yield return ("continue", RunStep(world, new PlaylistSessionInput.Continue()));
        }
        else if (!world.Channel.IsEmpty)
        {
            var input = world.Channel[0];
            var label = $"take {Describe(input)}";
            _trace.Add(label);
            var next = RunStep(
                world with
                {
                    Channel = world.Channel.RemoveAt(0),
                    Handling = input,
                    RunAtHandling = world.State.Run,
                    HandlingCanLoop =
                        input is PlaylistSessionInput.EndOfStream end
                        && world.Runtime is { Opened: true } runtime
                        && runtime.Generation == end.Generation
                        && runtime.Model.Run == end.Run
                        && world.State.Item is { Played: true },
                },
                input
            );
            _trace.RemoveAt(_trace.Count - 1);
            yield return (label, next);
        }

        // The controller's next call.
        if (
            !world.Disposing
            && !world.ControllerStopped
            && world.AwaitingCommand is null
            && world.NextCall < _scenario.Commands.Length
        )
        {
            var call = _scenario.Commands[world.NextCall];
            var id = world.NextCall + 1;
            PlaylistSessionInput input = call switch
            {
                ExplorerCommand.Initialize => new PlaylistSessionInput.Initialize(id),
                ExplorerCommand.WarmUp => new PlaylistSessionInput.WarmUp(id),
                ExplorerCommand.Play => new PlaylistSessionInput.Play(id),
                ExplorerCommand.Pause => new PlaylistSessionInput.Pause(id),
                _ => new PlaylistSessionInput.Seek(id, SeekPosition),
            };
            yield return (
                $"controller calls {call}",
                world with
                {
                    Channel = world.Channel.Add(input),
                    NextCall = world.NextCall + 1,
                    AwaitingCommand = id,
                }
            );
        }

        // The events, one move per occurrence. Two equal occurrences lead to the same state, which the
        // visited set explores once.
        for (var i = 0; i < world.Remaining.Count; i++)
        {
            var happening = world.Remaining[i];
            if (Happen(world, happening) is { } next)
            {
                yield return (
                    $"{Describe(happening)} happens",
                    next with
                    {
                        Remaining = world.Remaining.RemoveAt(i),
                    }
                );
            }
        }
    }

    private World? Happen(World world, ExplorerEvent happening)
    {
        switch (happening)
        {
            case ExplorerEvent.Dispose when !world.Disposing && world.NextCall > 0:
                // DisposeAsync detaches, marks the session disposing and posts Dispose. From then on
                // nothing more is posted.
                return world with
                {
                    Disposing = true,
                    Attached = false,
                    Channel = world.Channel.Add(new PlaylistSessionInput.Dispose()),
                };

            case ExplorerEvent.Skip when !world.Disposing:
                return world.Attached
                    ? world with
                    {
                        Channel = world.Channel.Add(
                            new PlaylistSessionInput.SkipRequested(world.PublishedGeneration, world.PublishedRun)
                        ),
                    }
                    : world with { Queue = world.Queue.LatchAdvance(PlaylistAdvance.Skip) };

            case ExplorerEvent.Jump jump when !world.Disposing:
            {
                var target = world
                    .Queue.Playlist.Concat(world.Queue.Next)
                    .Concat(world.Queue.Queued)
                    .FirstOrDefault(i => i.Source.DisplayName == jump.Target);
                if (target is null)
                    return world;
                var (queue, result) = world.Queue.RequestJump(target);
                return world with
                {
                    Queue = queue,
                    Channel =
                        result == JumpRequest.Pending && world.Attached
                            ? world.Channel.Add(new PlaylistSessionInput.JumpRequested())
                            : world.Channel,
                };
            }

            case ExplorerEvent.RemoveCurrent when !world.Disposing && world.Queue.Current is { } current:
                return world with { Queue = world.Queue.Remove(current).Queue };

            case ExplorerEvent.Enqueue enqueue when !world.Disposing:
                return world with
                {
                    Queue = world.Queue.Enqueue(new PlaylistItem(new NamedSource(enqueue.Source))),
                };

            case ExplorerEvent.EndOfStream
                when !world.Disposing && world.Runtime is { Opened: true } runtime && runtime.Model.CanRaiseEndOfStream:
                return world with
                {
                    Runtime = runtime with { Model = runtime.Model.RaisedEndOfStream() },
                    Channel = world.Channel.Add(
                        new PlaylistSessionInput.EndOfStream(runtime.Generation, runtime.Model.Run)
                    ),
                };

            case ExplorerEvent.Fault
                when !world.Disposing && world.Runtime is { Opened: true } runtime && !runtime.Model.Disposed:
                return world with
                {
                    Channel = world.Channel.Add(
                        new PlaylistSessionInput.Fault(runtime.Generation, ModelFailure, TimeSpan.Zero)
                    ),
                };

            default:
                return null;
        }
    }

    // ── Item actions and their outcomes ─────────────────────────────────────

    private IEnumerable<(string Label, World? Prepared, PlaylistSessionInput.Outcome? Outcome)> Outcomes(
        World world,
        PlaylistSessionAction action
    )
    {
        var command = CommandOf(action);
        var cancellable = world.Disposing || (command is not null && _scenario.CommandsCanBeCancelled);

        if (action is PlaylistSessionAction.OpenItem open)
        {
            if (world.Runtime is not null)
            {
                Violate(ExplorerInvariants.OpensWhileLive, $"{Describe(action)} with {world.Runtime}");
                yield break;
            }

            var opened = new ModelRuntime(open.Generation, Opened: true, PlaylistItemModel.Opened);
            yield return ("open succeeds", world with { Runtime = opened }, Ok(0, Info));
            var failedOpen = opened with { Opened = false };
            if (_scenario.Failures.HasFlag(ExplorerFailures.Open))
                yield return ("open fails", world with { Runtime = failedOpen }, Failed(0));
            if (cancellable)
                yield return ("open is cancelled", world with { Runtime = failedOpen, AnyCancellation = true }, Cancelled(0));
            yield break;
        }

        if (action is PlaylistSessionAction.DisposeItem)
        {
            if (world.Runtime is null)
            {
                Violate(ExplorerInvariants.DisposesNothing, "DisposeItem with no runtime");
                yield break;
            }
            yield return ("dispose completes", world with { Runtime = null }, Ok(0));
            yield break;
        }

        if (world.Runtime is not { Opened: true } runtime)
        {
            Violate(ExplorerInvariants.CallsNoRuntime, $"{Describe(action)} with {world.Runtime}");
            yield break;
        }

        var model = runtime.Model;
        var (name, flag, succeeded) = action switch
        {
            PlaylistSessionAction.WarmUpItem => ("warm-up", ExplorerFailures.WarmUp, model),
            PlaylistSessionAction.PlayItem => ("play", ExplorerFailures.Play, model.Played()),
            PlaylistSessionAction.PauseItem => ("pause", ExplorerFailures.Pause, model.PausedNow()),
            PlaylistSessionAction.SeekItem => ("seek", ExplorerFailures.Seek, model.Repositioned()),
            _ => ("rewind", ExplorerFailures.Rewind, model.Repositioned()),
        };
        var repositions = action is PlaylistSessionAction.SeekItem or PlaylistSessionAction.RewindItem;

        yield return ($"{name} succeeds", With(world, runtime, succeeded), Ok(succeeded.Run));
        if (_scenario.Failures.HasFlag(flag))
            yield return ($"{name} fails", world, Failed(model.Run));
        if (cancellable)
        {
            var cancelled = world with { AnyCancellation = true };
            yield return ($"{name} is cancelled", cancelled, Cancelled(model.Run));
            if (repositions)
            {
                var advanced = model.CancelledAfterTheRunAdvanced();
                yield return (
                    $"{name} is cancelled after the run advanced",
                    With(cancelled, runtime, advanced),
                    Cancelled(advanced.Run)
                );
            }
        }
    }

    private static World With(World world, ModelRuntime runtime, PlaylistItemModel model) =>
        world with
        {
            Runtime = runtime with { Model = model },
        };

    private static PlaylistSessionInput.Outcome Ok(int run, MediaInfo? info = null) =>
        new(PlaylistOutcome.Ok, run, info);

    private static PlaylistSessionInput.Outcome Failed(int run) =>
        new(PlaylistOutcome.Failed, run, Error: ModelFailure);

    private static PlaylistSessionInput.Outcome Cancelled(int run) =>
        new(PlaylistOutcome.Cancelled, run, Error: ModelCancellation);

    // ── One step of the shell's loop ────────────────────────────────────────

    private World RunStep(World world, PlaylistSessionInput input)
    {
        var handling = world.Handling ?? input;
        var before = world.State;
        var beforeQueue = world.Queue;
        var (state, queue, step) = _step(before, beforeQueue, input, new PlaylistStepContext(world.Disposing));

        CheckStep(world, input, handling, state, queue, step);

        var next = world with { State = state, Queue = queue };
        if (handling is PlaylistSessionInput.WarmUp && before.Run == PlaylistRunState.Ended && state.Run != PlaylistRunState.Ended)
            next = next with { EndReported = false };

        foreach (var action in step.Actions)
            next = Perform(next, action);

        next = next with { PublishedGeneration = state.Generation, PublishedRun = state.Run };

        if (step.Awaited is { } awaited)
            return next with { Handling = handling, Awaiting = awaited, ContinueDue = false };
        if (!step.Done)
            return next with { Handling = handling, Awaiting = null, ContinueDue = true };

        next = next with { Handling = null, Awaiting = null, ContinueDue = false };
        CheckHandled(world, next, handling);
        return next;
    }

    private World Perform(World world, PlaylistSessionAction action)
    {
        switch (action)
        {
            case PlaylistSessionAction.AttachToCoordinator when !world.Disposing:
                return world with { Attached = true };
            case PlaylistSessionAction.ReportEndOfStream:
                return world with { EndReported = true };
            case PlaylistSessionAction.ReportFatal:
                return world with { FatalReported = true };
            case PlaylistSessionAction.CompleteCommand complete:
            {
                var count = world.Completions.GetValueOrDefault(complete.Command) + 1;
                if (count > 1)
                    Violate(ExplorerInvariants.CompletesTwice, $"command {complete.Command}");
                return world with
                {
                    Completions = world.Completions.SetItem(complete.Command, count),
                    AwaitingCommand = world.AwaitingCommand == complete.Command ? null : world.AwaitingCommand,
                    ControllerStopped = world.ControllerStopped || complete.Result.Kind != PlaylistOutcome.Ok,
                };
            }
            default:
                return world;
        }
    }

    // ── Invariants ──────────────────────────────────────────────────────────

    private void CheckStep(
        World world,
        PlaylistSessionInput input,
        PlaylistSessionInput handling,
        PlaylistSessionState after,
        PlaylistQueue afterQueue,
        PlaylistSessionStep step
    )
    {
        var before = world.State;
        var beforeQueue = world.Queue;
        var actions = step.Actions;
        var awaited = step.Awaited;

        // Commands.
        if (actions.Any(a => a is PlaylistSessionAction.CompleteCommand) && (awaited is not null || !step.Done))
        {
            Violate(
                ExplorerInvariants.CompletesEarly,
                Describe(handling)
            );
        }

        // Presenting.
        if (awaited is PlaylistSessionAction.OpenItem { Command: null } && after.Run == PlaylistRunState.NotStarted)
            Violate(ExplorerInvariants.AdvancesBeforeTheFirstPlay, Describe(handling));
        if (handling is PlaylistSessionInput.Fault && awaited is PlaylistSessionAction.RewindItem)
            Violate(ExplorerInvariants.RewindsAFaultedItem, Describe(handling));
        if (awaited is PlaylistSessionAction.PlayItem && after.Run != PlaylistRunState.Playing)
            Violate(ExplorerInvariants.PlaysWhileNotPlaying, $"run {after.Run}");
        if (
            awaited is PlaylistSessionAction.SeekItem or PlaylistSessionAction.RewindItem
            && after.Item is { Played: false }
            && after.Run != PlaylistRunState.Playing
        )
        {
            Violate(ExpectedFailure, $"{Describe(awaited)} at {after.Run}");
        }

        // The end.
        var endReported =
            world.EndReported
            && !(
                handling is PlaylistSessionInput.WarmUp
                && before.Run == PlaylistRunState.Ended
                && after.Run != PlaylistRunState.Ended
            );
        if (endReported && awaited is PlaylistSessionAction.PlayItem or PlaylistSessionAction.SeekItem)
        {
            Violate(
                ExplorerInvariants.PlaysOrSeeksAfterTheEnd,
                $"{Describe(awaited)} handling {Describe(handling)}"
            );
        }

        // Notifications. Judged on the step that takes the input.
        if (ReferenceEquals(input, handling))
        {
            var generation = input switch
            {
                PlaylistSessionInput.EndOfStream e => e.Generation,
                PlaylistSessionInput.Fault f => f.Generation,
                _ => (int?)null,
            };

            if (generation is { } g && g != before.Generation)
            {
                RequireNoEffect(
                    ExplorerInvariants.ReplacedRuntimeActs,
                    input,
                    before,
                    after,
                    beforeQueue,
                    afterQueue,
                    step
                );
            }
            else if (
                input is PlaylistSessionInput.EndOfStream end
                && (
                    world.Runtime is not { Opened: true } runtime
                    || runtime.Generation != end.Generation
                    || runtime.Model.Run != end.Run
                )
            )
            {
                RequireNoEffect(
                    ExplorerInvariants.ReplacedRunActs,
                    input,
                    before,
                    after,
                    beforeQueue,
                    afterQueue,
                    step
                );
            }
        }

        // Reports.
        for (var i = 0; i < actions.Length; i++)
        {
            if (
                actions[i] is PlaylistSessionAction.ReportCurrentItemChanged
                && (i + 1 >= actions.Length || actions[i + 1] is not PlaylistSessionAction.RaiseTransition)
            )
            {
                Violate(ExplorerInvariants.ChangeWithoutTransition, Describe(handling));
            }
        }
        if (actions.Count(a => a is PlaylistSessionAction.RaiseTransition) > 1)
            Violate(ExplorerInvariants.TwoTransitions, Describe(handling));

        if (actions.Any(a => a is PlaylistSessionAction.ReportLoopRestarted) && !world.HandlingCanLoop)
            Violate(ExplorerInvariants.ReportsALoopThatIsNotOne, Describe(handling));

        var reports = actions.Any(a =>
            a
                is PlaylistSessionAction.ReportCurrentItemChanged
                    or PlaylistSessionAction.ReportItemFailed
                    or PlaylistSessionAction.ReportEndOfStream
                    or PlaylistSessionAction.ReportFatal
                    or PlaylistSessionAction.ReportLoopRestarted
        );
        if (reports && world.FatalReported)
            Violate(ExplorerInvariants.ReportsAfterFatal, Describe(handling));
        if (reports && world.Disposing)
            Violate(ExplorerInvariants.ReportsWhileDisposing, Describe(handling));

        // Giving up.
        if (world.FatalReported && awaited is PlaylistSessionAction.OpenItem or PlaylistSessionAction.PlayItem)
            Violate(ExplorerInvariants.StartsAfterGivingUp, $"{Describe(awaited)} handling {Describe(handling)}");

        // The queue.
        if (afterQueue.Cursor < 0 || afterQueue.Cursor > afterQueue.Playlist.Count)
            Violate(ExplorerInvariants.CursorOutside, $"{afterQueue.Cursor} of {afterQueue.Playlist.Count}");
        var held = afterQueue.Playlist.Concat(afterQueue.Next).Concat(afterQueue.Queued).ToList();
        if (held.Distinct(ReferenceEqualityComparer.Instance).Count() != held.Count)
            Violate(ExplorerInvariants.ItemTwice, string.Join(",", held));
        if (
            beforeQueue is { CurrentRemoved: true, Current: { } removed }
            && ReferenceEquals(afterQueue.Current, removed)
            && !afterQueue.CurrentRemoved
        )
        {
            Violate(ExplorerInvariants.RemovedTakenAgain, removed.ToString());
        }
    }

    private void CheckHandled(World before, World after, PlaylistSessionInput handled)
    {
        // A latched skip is consumed by the first Play the session acts on. During disposal, or once it
        // has given up, a Play is a no-op and the latch waits on the queue for a later session.
        if (
            handled is PlaylistSessionInput.Play
            && !before.Disposing
            && !after.State.GaveUp
            && after.RunAtHandling != PlaylistRunState.Ended
            && after.Queue.LatchedAdvance is not null
        )
        {
            Violate(ExplorerInvariants.PlayLeavesLatch, Describe(handled));
        }
    }

    private void CheckQuiescence(World world)
    {
        for (var id = 1; id <= world.NextCall; id++)
        {
            if (!world.Completions.ContainsKey(id))
                Violate(ExplorerInvariants.NeverCompletes, $"command {id} ({_scenario.Commands[id - 1]})");
        }

        if (world.Disposing && world.Runtime is not null)
            Violate(ExplorerInvariants.LeavesARuntime, world.Runtime.ToString());

        // A command that failed or was cancelled puts the controller in Error or leaves it where it
        // was, so what the session holds after it is not what a playing controller expects.
        var live = !world.Disposing && !world.State.GaveUp && !world.ControllerStopped;

        if (
            live
            && world.Attached
            && world.Queue.HasPendingJump
            && world.State.Run is PlaylistRunState.Playing or PlaylistRunState.Paused
        )
        {
            Violate(ExplorerInvariants.JumpLeftPending, world.State.Run.ToString());
        }

        if (live && world.State.Run == PlaylistRunState.Playing)
        {
            if (world.State.Item is not { } item)
            {
                Violate(ExplorerInvariants.PlayingNothing, "");
                return;
            }
            if (!ReferenceEquals(item.Item, world.Queue.Current) || !world.Queue.CurrentStarted)
                Violate(ExplorerInvariants.PlayingNotCurrent, $"{item.Item} vs {world.Queue.Current}");
            if (!item.Played)
                Violate(ExplorerInvariants.PlayingUnstarted, item.Item.ToString());
            if (world.Queue.LatchedAdvance is not null)
                Violate(ExplorerInvariants.LatchedWhilePlaying, world.Queue.LatchedAdvance.ToString()!);
        }
    }

    private void RequireNoEffect(
        string invariant,
        PlaylistSessionInput input,
        PlaylistSessionState before,
        PlaylistSessionState after,
        PlaylistQueue beforeQueue,
        PlaylistQueue afterQueue,
        PlaylistSessionStep step
    )
    {
        if (
            !step.Done
            || step.Awaited is not null
            || step.Actions.Any(a => a is not PlaylistSessionAction.Log)
            || !Equals(before, after)
            || !Equals(beforeQueue, afterQueue)
        )
        {
            Violate(invariant, Describe(input));
        }
    }

    private void Violate(string invariant, string detail)
    {
        if (!_violations.ContainsKey(invariant))
            _violations[invariant] = new ExplorerViolation(invariant, detail, [.. _trace]);
    }

    // ── The world ───────────────────────────────────────────────────────────

    private sealed record ModelRuntime(int Generation, bool Opened, PlaylistItemModel Model);

    private sealed record World
    {
        public required PlaylistSessionState State { get; init; }
        public required PlaylistQueue Queue { get; init; }
        public ImmutableList<PlaylistSessionInput> Channel { get; init; } = [];
        public PlaylistSessionInput? Handling { get; init; }
        public PlaylistRunState RunAtHandling { get; init; }

        /// <summary>
        /// Whether the input being handled is an end-of-stream from the current run of the item
        /// runtime, while that item has played: the only input that can perform a loop.
        /// </summary>
        public bool HandlingCanLoop { get; init; }
        public PlaylistSessionAction? Awaiting { get; init; }
        public bool ContinueDue { get; init; }
        public ModelRuntime? Runtime { get; init; }
        public int NextCall { get; init; }
        public int? AwaitingCommand { get; init; }
        public bool ControllerStopped { get; init; }
        public ImmutableList<ExplorerEvent> Remaining { get; init; } = [];
        public ImmutableDictionary<int, int> Completions { get; init; } = ImmutableDictionary<int, int>.Empty;
        public bool Attached { get; init; }
        public bool Disposing { get; init; }
        public bool EndReported { get; init; }
        public bool FatalReported { get; init; }
        public bool AnyCancellation { get; init; }
        public int PublishedGeneration { get; init; }
        public PlaylistRunState PublishedRun { get; init; }
    }

    private static UInt128 Hash(World world)
    {
        var key = new StringBuilder();
        key.Append(world.State).Append('|');
        var q = world.Queue;
        key.Append(string.Join(",", q.Playlist)).Append('/').Append(q.Cursor)
            .Append('/').Append(string.Join(",", q.Next))
            .Append('/').Append(string.Join(",", q.Queued))
            .Append('/').Append(q.PendingJump).Append('/').Append(q.Current)
            .Append('/').Append(q.CurrentStarted).Append(q.CurrentRemoved)
            .Append('/').Append(q.ReservedStart).Append('/').Append(q.Reported)
            .Append('/').Append(q.Repeat).Append('/').Append(q.ConsecutiveFailures)
            .Append('/').Append(q.LatchedAdvance).Append('/').Append(q.Revision)
            .Append('/').Append(q.TransitionCount).Append('|');
        key.Append(string.Join(";", world.Channel.Select(Describe))).Append('|');
        key.Append(world.Handling is null ? "-" : Describe(world.Handling)).Append(world.RunAtHandling)
            .Append(world.HandlingCanLoop).Append('|');
        key.Append(world.Awaiting is null ? "-" : Describe(world.Awaiting)).Append(world.ContinueDue).Append('|');
        key.Append(world.Runtime).Append('|');
        key.Append(world.NextCall).Append(world.AwaitingCommand).Append(world.ControllerStopped).Append('|');
        key.Append(string.Join(";", world.Remaining.Select(Describe).Order())).Append('|');
        key.Append(string.Join(";", world.Completions.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"))).Append('|');
        key.Append(world.Attached).Append(world.Disposing).Append(world.EndReported)
            .Append(world.FatalReported).Append(world.AnyCancellation)
            .Append(world.PublishedGeneration).Append(world.PublishedRun);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString()));
        return new UInt128(BitConverter.ToUInt64(digest, 0), BitConverter.ToUInt64(digest, 8));
    }

    private static int? CommandOf(PlaylistSessionAction action) =>
        action switch
        {
            PlaylistSessionAction.OpenItem a => a.Command,
            PlaylistSessionAction.WarmUpItem a => a.Command,
            PlaylistSessionAction.PlayItem a => a.Command,
            PlaylistSessionAction.PauseItem a => a.Command,
            PlaylistSessionAction.SeekItem a => a.Command,
            PlaylistSessionAction.RewindItem a => a.Command,
            _ => null,
        };

    private static string Describe(object value) =>
        value switch
        {
            PlaylistSessionInput.Fault f => $"Fault(generation {f.Generation})",
            PlaylistSessionInput.Outcome o => $"Outcome({o.Kind}, run {o.Run})",
            PlaylistSessionAction.OpenItem o => $"OpenItem(generation {o.Generation}, {o.Source.DisplayName}, command {o.Command})",
            _ => value.ToString()!.Replace("PlaylistSessionInput+", "").Replace("PlaylistSessionAction+", ""),
        };

    private sealed record NamedSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;

        // Each item is its own source object, compared by reference, as a real source is.
        public bool Equals(NamedSource? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
}
