// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Playback.Core;
using Xunit.Abstractions;
using Action = FrameFlow.Playback.Core.ItemRuntimeAction;
using Input = FrameFlow.Playback.Core.ItemRuntimeInput;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Walks every state <see cref="ItemRuntimeProtocol"/> can reach, for each shape of item, playing the
/// shell: it performs each step's actions on a model of what the shell holds and runs, feeds back
/// every outcome an awaited action can have and every way a piece of work can end, and checks the
/// rules ADR-0086 decision 2 sets after every step.
/// </summary>
/// <remarks>
/// <para>
/// From each state the walk sends every command, the caller's cancellation of the command in
/// progress, an attach, a dispose, each outcome of the action under way, the end of each piece of
/// work the shell is running, completed, cancelled or faulted, and the first frame. A command or a
/// completion may arrive while an action is under way, as it may in a shell that does not queue
/// them. A run's work runs from the moment the shell is asked to start it, so a piece can end before
/// the start's outcome. The position clock is the model's: a command reads whether it is paused,
/// and once a run has played to its end the controller may freeze it between commands, as it does
/// at the end of the stream.
/// </para>
/// <para>
/// The model keeps its own account of what the checks read: whether the runtime is attached,
/// opened and has played, and what each command was sent with. The core's view is checked against
/// it, not used in place of it.
/// </para>
/// <para>
/// A state is visited once under its canonical key: the run number and the command's number only
/// label, and the machine compares them only for equality with the current ones, so the key holds
/// them relative to the current run and command. That covers every sequence that reaches a state,
/// of any length. From every state reached, a dispose followed by the end of whatever is still
/// running reaches a disposed runtime that owes nothing.
/// </para>
/// </remarks>
public sealed class ItemRuntimeProtocolWalkTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan End = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Clock = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Target = TimeSpan.FromSeconds(3);

    // Past the item's end when it has one, so the run it starts plays to the end of the source.
    private static readonly TimeSpan OtherTarget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PastTheEnd = TimeSpan.FromSeconds(12);

    private static readonly Exception Boom = new InvalidOperationException("boom");

    private const string Shared =
        "A detached runtime touches neither the position clock nor the audio sink it shares with the item that is playing";
    private static readonly Exception Cancellation = new OperationCanceledException();

    private const ItemResources Media = ItemResources.Demux | ItemResources.Pipeline;

    private static readonly Dictionary<string, Shape> ShapesByName = new Shape[]
    {
        // A wallclock master with a pacer and an audio sink: every step of every path, and the
        // lateness-recovery walk that only a stop ends.
        new("audio and video", ItemRuntimeTranscript.AudioAndVideo, Attached: true, LatenessRecovery: true),
        new("video only", ItemResources.Clock | Media | ItemResources.VideoDecoder | ItemResources.Pacer, true, false),
        // The audio sink masters the clock, so the open frees the wallclock.
        new("audio only", Media | ItemResources.AudioDecoder, true, false),
        // No sink for any stream: a run is only its end of stream.
        new("nothing to play", ItemResources.Clock | Media, true, false),
        new("prepared ahead", ItemRuntimeTranscript.AudioAndVideo, Attached: false, LatenessRecovery: false),
    }.ToDictionary(s => s.Name);

    public static TheoryData<string> Shapes
    {
        get
        {
            var shapes = new TheoryData<string>();
            foreach (var name in ShapesByName.Keys)
                shapes.Add(name);
            return shapes;
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryReachableState_KeepsTheRuntimeRules(string shape)
    {
        var walk = Walk(ShapesByName[shape]);
        output.WriteLine($"{shape}: {walk.Count} states.");

        foreach (var node in walk.Keys)
            Drain(ShapesByName[shape], node, () => PathTo(walk, node));
    }

    [Fact]
    public void TheWalk_ReachesEveryStageAndEveryStatus()
    {
        var walk = Walk(ShapesByName["audio and video"]).Keys;

        Assert.Equal(
            Enum.GetValues<ItemCommandStage>().Order(),
            walk.Select(n => n.State.Command?.Stage).OfType<ItemCommandStage>().Distinct().Order()
        );
        Assert.Equal(
            Enum.GetValues<ItemRunStatus>().Order(),
            walk.Where(n => n.State.Phase == ItemRuntimePhase.Open).Select(n => n.State.Status).Distinct().Order()
        );
        Assert.Equal(
            Enum.GetValues<ItemRuntimePhase>().Order(),
            walk.Select(n => n.State.Phase).Distinct().Order()
        );
        Assert.Contains(walk, n => n.State.Command?.Abandoned == true);
        Assert.Contains(walk, n => n.State.Command?.SettleHeld == true);
        Assert.Contains(walk, n => n.State.EndedBeforeStart != ItemWork.None);
        Assert.Contains(walk, n => n.State.DisposeRequested);
        Assert.Contains(walk, n => n.State.Phase == ItemRuntimePhase.Disposing && n.State.Status == ItemRunStatus.Stopping);
        Assert.Contains(walk, n => n.Model.Gates && n.Model.Clock == ClockModel.Paused); // frozen at the end, not paused
    }

    // ── The walk ────────────────────────────────────────────────────────────

    private sealed record Shape(string Name, ItemResources Resources, bool Attached, bool LatenessRecovery)
    {
        public bool HasAudio => (Resources & ItemResources.AudioDecoder) != 0;

        public bool HasVideo => (Resources & ItemResources.VideoDecoder) != 0;
    }

    private enum ClockModel
    {
        Stopped,
        Running,
        Paused,
    }

    private enum AudioModel
    {
        Inactive,

        /// <summary>Activated: it plays what reaches it through the gates, and nothing has yet.</summary>
        Primed,

        /// <summary>Playing what it holds.</summary>
        Playing,

        /// <summary>Paused, holding what it was given.</summary>
        Paused,
    }

    /// <summary>What the walk does next: step an input, or freeze the position clock.</summary>
    private abstract record Event;

    private sealed record Feed(Input Input) : Event
    {
        public override string ToString() => Input.ToString();
    }

    /// <summary>The controller pauses the position clock itself, as it does at the end of the stream.</summary>
    private sealed record FreezeClock : Event;

    /// <summary>A command the shell owes an answer, and what it was sent with.</summary>
    /// <param name="Id">Its number.</param>
    /// <param name="Kind">Which command.</param>
    /// <param name="Target">Where it positions, if it does: a seek's target, a rewind's position, an open's start, and where the clock stood for a play or a warm-up.</param>
    /// <param name="PlaysOn">Whether it was sent to a runtime that had played, with the clock running.</param>
    /// <param name="Cancelled">Whether its caller has cancelled it.</param>
    private sealed record Owed(int Id, ItemCommandKind Kind, TimeSpan? Target, bool PlaysOn, bool Cancelled);

    /// <summary>What the shell holds and runs once it has done the work so far.</summary>
    private sealed record Model
    {
        /// <summary>The pieces of work started and not ended.</summary>
        public ItemWork Working { get; init; }

        /// <summary>The run they belong to.</summary>
        public int WorkRun { get; init; }

        /// <summary>The run started and not cancelled.</summary>
        public int? Live { get; init; }

        /// <summary>What is allocated and not freed: the wallclock from construction on.</summary>
        public ItemResources Alive { get; init; } = ItemResources.Clock;

        /// <summary>The command issued and not completed.</summary>
        public Owed? Outstanding { get; init; }

        /// <summary>The action under way, whose outcome the walk feeds back.</summary>
        public Action? Awaiting { get; init; }

        public bool Gates { get; init; }

        public ClockModel Clock { get; init; }

        public AudioModel Audio { get; init; }

        /// <summary>No run has started on the decoders since the open, or since they were reset.</summary>
        public bool Clean { get; init; } = true;

        /// <summary>The live run has raised its end of stream.</summary>
        public bool EndRaised { get; init; }

        /// <summary>The live run holds its destination frame for a settle.</summary>
        public bool SettleOwed { get; init; }

        public bool DisposeCompleted { get; init; }

        /// <summary>The runtime acts on the shared clock and sink: from construction, or since its attach.</summary>
        public bool Attached { get; init; }

        /// <summary>An open has succeeded.</summary>
        public bool Opened { get; init; }

        /// <summary>A play of the opened runtime has completed.</summary>
        public bool Played { get; init; }

        /// <summary>The decoder has signalled its first frame, which it does once.</summary>
        public bool FirstFrame { get; init; }
    }

    private sealed record Node(ItemRuntimeState State, Model Model);

    // Breadth first, each canonical state once, keeping the event that first reached it.
    private static Dictionary<Node, (Node? Parent, Event? Event)> Walk(Shape shape)
    {
        var root = new Node(
            ItemRuntimeState.Initial(shape.Attached, shape.LatenessRecovery),
            new Model { Attached = shape.Attached }
        );
        var seen = new Dictionary<Node, (Node? Parent, Event? Event)> { [root] = (null, null) };
        var queue = new Queue<Node>([root]);
        while (queue.TryDequeue(out var node))
        {
            foreach (var e in Events(shape, node))
            {
                var next = Canonical(Apply(shape, node, e, () => PathTo(seen, node)));
                if (seen.TryAdd(next, (node, e)))
                    queue.Enqueue(next);
            }
        }

        return seen;
    }

    private static string PathTo(Dictionary<Node, (Node? Parent, Event? Event)> seen, Node node)
    {
        var events = new List<string>();
        for (var at = node; seen[at] is (Node parent, Event e); at = parent)
            events.Add(e.ToString());
        events.Reverse();
        return $"After [{string.Join(", ", events)}]";
    }

    // Holds the run numbers relative to the current run and the command's number as 1. Each check
    // compares them only with the current ones, so the renumbered state checks the same.
    private static Node Canonical(Node node)
    {
        var (state, model) = node;
        var shift = state.Run;
        return new Node(
            state with { Run = 0, Command = state.Command is { } c ? c with { Id = 1 } : null },
            model with
            {
                WorkRun = model.Working == ItemWork.None ? 0 : model.WorkRun - shift,
                Live = model.Live - shift,
                Outstanding = model.Outstanding is { } owed ? owed with { Id = owed.Id == state.Command?.Id ? 1 : -1 } : null,
                Awaiting = model.Awaiting is Action.StartRun start ? start with { Run = start.Run - shift } : model.Awaiting,
            }
        );
    }

    private static IEnumerable<Event> Events(Shape shape, Node node)
    {
        var (state, model) = node;

        // The in-progress command is 1 in a canonical state, so a new one is 2. The shell reads
        // whether the clock is paused when a command arrives.
        const int Fresh = 2;
        var paused = model.Clock == ClockModel.Paused;
        yield return new Feed(new Input.Initialize(Fresh, TimeSpan.Zero, null));
        yield return new Feed(new Input.Initialize(Fresh, Start, End));
        yield return new Feed(new Input.WarmUp(Fresh, Clock));
        yield return new Feed(new Input.Play(Fresh, Clock, paused));
        yield return new Feed(new Input.Pause(Fresh));
        yield return new Feed(new Input.Seek(Fresh, Target, paused));
        yield return new Feed(new Input.Seek(Fresh, OtherTarget, paused));
        yield return new Feed(new Input.Rewind(Fresh, TimeSpan.Zero, End, paused));
        yield return new Feed(new Input.Rewind(Fresh, TimeSpan.Zero, null, paused));
        yield return new Feed(new Input.Rewind(Fresh, PastTheEnd, null, paused));

        if (model.Outstanding is { } owed)
            yield return new Feed(new Input.CallerCancelled(owed.Id));
        yield return new Feed(new Input.Attach());
        yield return new Feed(new Input.Dispose());

        // At the end of the stream, between commands.
        if (model.Outstanding is null && model.Clock == ClockModel.Running && model.EndRaised)
            yield return new FreezeClock();

        switch (model.Awaiting)
        {
            case Action.Open:
                yield return new Feed(new Input.Opened(shape.Resources, Duration));
                break;
            case not null:
                yield return new Feed(new Input.Performed(ItemActionOutcome.Ok));
                break;
        }

        // A start that fails starts nothing, so it cannot fail once a piece of its run has ended.
        if (model.Awaiting is not null && !(model.Awaiting is Action.StartRun start && model.Working != start.Work))
        {
            yield return new Feed(new Input.Performed(ItemActionOutcome.Failed, Boom));
            yield return new Feed(new Input.Performed(ItemActionOutcome.Cancelled, Cancellation));
        }

        foreach (var work in Pieces(model.Working))
        {
            yield return new Feed(new Input.WorkEnded(model.WorkRun, work, ItemWorkEnding.Completed));
            yield return new Feed(new Input.WorkEnded(model.WorkRun, work, ItemWorkEnding.Cancelled));
            yield return new Feed(new Input.WorkEnded(model.WorkRun, work, ItemWorkEnding.Faulted, Boom));
        }

        if (!model.FirstFrame && shape.HasVideo && (model.Working & ItemWork.Graph) != 0)
            yield return new Feed(new Input.FirstFrameDecoded());
    }

    private static IEnumerable<ItemWork> Pieces(ItemWork work) =>
        new[] { ItemWork.Pump, ItemWork.Graph, ItemWork.Recovery, ItemWork.EmptyEnd }.Where(w => (work & w) != 0);

    // From any state, a dispose and the end of whatever runs reaches a disposed runtime that owes
    // nothing: no state is stuck, and every command and the dispose complete.
    private static void Drain(Shape shape, Node node, Func<string> path)
    {
        var at = Apply(shape, node, new Feed(new Input.Dispose()), path);
        for (var step = 0; step < 32 && at.State.Phase != ItemRuntimePhase.Disposed; step++)
        {
            Input? next = at.Model switch
            {
                { Awaiting: Action.Open } => new Input.Opened(shape.Resources, Duration),
                { Awaiting: not null } => new Input.Performed(ItemActionOutcome.Ok),
                { Working: not ItemWork.None } m => new Input.WorkEnded(m.WorkRun, Pieces(m.Working).First(), ItemWorkEnding.Cancelled),
                _ => null,
            };
            if (next is null)
                break;
            at = Apply(shape, at, new Feed(next), path);
        }

        if (at.State.Phase != ItemRuntimePhase.Disposed)
            Assert.Fail($"A dispose never finished. {path()}, then a dispose: now {at.State}, shell {at.Model}.");
    }

    // ── The shell, and the checks ───────────────────────────────────────────

    // Performs one event on the model, checking each action as it goes and the state both sides
    // arrive at. Returns the state and the model after it.
    private static Node Apply(Shape shape, Node node, Event e, Func<string> path)
    {
        if (e is FreezeClock)
            return node with { Model = node.Model with { Clock = ClockModel.Paused } };

        var input = ((Feed)e).Input;
        var (before, model) = node;
        var (after, step) = ItemRuntimeProtocol.Step(before, input);
        var m = model;

        // Built only on a failure.
        void Ensure(bool ok, string what)
        {
            if (!ok)
            {
                Assert.Fail(
                    $"{what}. {path()} ({shape.Name}): {input} from {before} gave "
                        + $"[{string.Join(", ", step.Actions)}] then {step.Awaited?.ToString() ?? "nothing"}, "
                        + $"now {after}; shell was {model}."
                );
            }
        }

        // The commands the shell owes an answer: the one in progress, and a new one.
        var owed = new Dictionary<int, Owed>();
        if (m.Outstanding is { } outstanding)
            owed[outstanding.Id] = outstanding;
        var newCommand = input as Input.CommandInput;
        if (newCommand is not null)
        {
            Ensure(!owed.ContainsKey(newCommand.Command), "The walk numbers each command afresh");
            owed[newCommand.Command] = Owe(newCommand, m);
        }
        var completed = new List<(Owed Command, ItemCommandResult Result)>();

        // What the input itself tells the shell.
        var faultFed = false;
        var endOfStreamDue = false;
        var attachDue = false;
        switch (input)
        {
            case Input.Opened opened:
                m = m with { Awaiting = null, Alive = opened.Resources, Opened = true };
                break;
            case Input.Performed performed:
                var awaitedBefore = m.Awaiting!;
                m = m with { Awaiting = null };
                m = performed.Outcome == ItemActionOutcome.Ok ? Succeeded(m, awaitedBefore) : Failed(m, awaitedBefore);
                break;
            case Input.WorkEnded ended:
                // The end of stream of a live run that played to its end, which nothing has stopped.
                endOfStreamDue =
                    ended.Ending == ItemWorkEnding.Completed
                    && ended.Work is ItemWork.Graph or ItemWork.EmptyEnd
                    && m.Live == ended.Run;
                m = m with { Working = m.Working & ~ended.Work };
                faultFed = ended.Ending == ItemWorkEnding.Faulted;
                break;
            case Input.CallerCancelled cancelled when owed.TryGetValue(cancelled.Command, out var target):
                owed[cancelled.Command] = target with { Cancelled = true };
                break;
            case Input.FirstFrameDecoded:
                m = m with { FirstFrame = true };
                break;
            case Input.Attach:
                attachDue = !m.Attached && before.Phase is not (ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed);
                break;
        }

        var teardown = after.Phase == ItemRuntimePhase.Disposed;
        var faults = 0;
        var endsOfStream = 0;
        var attached = false;
        var released = false;
        foreach (var action in step.Actions)
        {
            switch (action)
            {
                case Action.CancelRun cancel:
                    Ensure(m.Live == cancel.Run, "Cancels only the live run, and each run once");
                    Ensure(!m.SettleOwed, "Cancels no run whose settle is owed");
                    m = m with { Live = null, EndRaised = false };
                    break;
                case Action.CloseGates:
                    m = m with { Gates = false };
                    break;
                case Action.OpenGates:
                    Ensure(
                        m.Live is not null || after.Phase is ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed,
                        "Opens the gates only over a live run, or to unblock a teardown"
                    );
                    Ensure(m.Attached, "A detached runtime opens no gate: what waits there would reach the sinks it shares");
                    m = m with
                    {
                        Gates = true,
                        Audio = m.Audio == AudioModel.Primed && m.Live is not null ? AudioModel.Playing : m.Audio,
                    };
                    break;
                case Action.FlushPacer:
                    Ensure(m.Working == ItemWork.None, "Flushes the pacer only once no work is pending");
                    break;
                case Action.ReleaseSettle:
                    Ensure(m.SettleOwed, "Releases only a hold a run took for its settle");
                    released = true;
                    m = m with { SettleOwed = false };
                    break;
                case Action.ResetDecoders:
                    Ensure(m.Working == ItemWork.None && m.Live is null, "Resets the decoders only from a state with no work of any run pending");
                    m = m with { Clean = true };
                    break;
                case Action.SeatClocks seat:
                    Ensure(m.Working == ItemWork.None, "Seats the clocks for a reposition only once no work is pending");
                    Ensure(seat.Shared == m.Attached, "Seats the shared clocks exactly when attached");
                    break;
                case Action.SeatBaseline:
                    Ensure(m.Attached, Shared);
                    break;
                case Action.StartClocks:
                    Ensure(m.Attached, Shared);
                    m = m with { Clock = ClockModel.Running };
                    break;
                case Action.PauseClocks:
                    Ensure(m.Attached, Shared);
                    m = m with { Clock = m.Clock == ClockModel.Running ? ClockModel.Paused : m.Clock };
                    break;
                case Action.ResumeClocks:
                    Ensure(m.Attached, Shared);
                    m = m with { Clock = m.Clock == ClockModel.Paused ? ClockModel.Running : m.Clock };
                    break;
                case Action.StopClock:
                    Ensure(teardown && m.Attached, "Stops the position clock only in an attached runtime's teardown");
                    m = m with { Clock = ClockModel.Stopped };
                    break;
                case Action.AttachClocks:
                    Ensure(attachDue && !attached, "Seats the shared clocks once, at the attach");
                    attached = true;
                    m = m with { Attached = true };
                    break;
                case Action.DeactivateAudio:
                    Ensure(teardown, "Deactivates the audio sink unawaited only in the teardown");
                    Ensure(m.Attached || m.Audio != AudioModel.Inactive, "Leaves the shared audio sink alone in a detached runtime that never activated it");
                    m = m with { Audio = AudioModel.Inactive };
                    break;
                case Action.Free free:
                    Ensure(free.Resource is not ItemResources.None && (free.Resource & (free.Resource - 1)) == 0, "Frees one resource at a time");
                    Ensure((m.Alive & free.Resource) != 0, "Frees each resource exactly once");
                    Ensure((int)m.Alive < 2 * (int)free.Resource, "Frees in reverse allocation order");
                    Ensure(m.Working == ItemWork.None && m.Live is null, "Frees only once no work of any run is pending");
                    Ensure(teardown, "Frees only in the teardown");
                    m = m with { Alive = m.Alive & ~free.Resource };
                    break;
                case Action.ReportFault report:
                    faults++;
                    Ensure(
                        input is Input.WorkEnded { Ending: ItemWorkEnding.Faulted } ended
                            && ended.Run == report.Run
                            && ended.Work == report.Work
                            && ReferenceEquals(ended.Error, report.Error),
                        "Reports only the fault the work ended with"
                    );
                    break;
                case Action.RaiseEndOfStream end:
                    endsOfStream++;
                    Ensure(
                        input is Input.WorkEnded { Ending: ItemWorkEnding.Completed, Work: ItemWork.Graph or ItemWork.EmptyEnd },
                        "Raises the end of stream only when a run's graph, or its empty end, completes"
                    );
                    Ensure(end.Run == after.Run && m.Live == end.Run, "Raises the end of stream only of the current run, while it is live");
                    Ensure(!m.EndRaised, "Raises a run's end of stream once");
                    m = m with { EndRaised = true };
                    break;
                case Action.CompleteCommand complete:
                    Ensure(owed.Remove(complete.Command, out var command), "Completes each command exactly once");
                    Ensure(
                        !command!.Cancelled || complete.Result.Outcome != ItemCommandOutcome.Ok,
                        "A command its caller cancelled never completes Ok"
                    );
                    Ensure(!m.SettleOwed || m.Awaiting is not null, "A command that started a run to settle settles it, or releases it, before it ends");
                    Ensure(!released || complete.Result.Outcome != ItemCommandOutcome.Ok, "A command that completes Ok settled the run it started to settle, rather than releasing it");
                    completed.Add((command, complete.Result));
                    break;
                case Action.CompleteDispose:
                    Ensure(!m.DisposeCompleted, "Completes the dispose once");
                    Ensure(
                        m.Alive == ItemResources.None && m.Working == ItemWork.None && m.Live is null,
                        "Completes the dispose once everything is freed and nothing runs"
                    );
                    Ensure(m.Audio == AudioModel.Inactive, "A dispose leaves no audio sink it activated active");
                    Ensure(!m.Attached || m.Clock == ClockModel.Stopped, "An attached runtime's dispose stops the position clock");
                    m = m with { DisposeCompleted = true };
                    break;
                default:
                    Ensure(false, $"{action} is not an immediate action");
                    break;
            }
        }
        Ensure(faults == (faultFed ? 1 : 0), "Reports every fault once");
        Ensure(endsOfStream == (endOfStreamDue ? 1 : 0), "Raises the end of stream of a live run that played to its end, once");
        Ensure(attached == attachDue, "An attach seats the shared clocks of a detached runtime");

        if (step.Awaited is { } awaited)
        {
            Ensure(m.Awaiting is null, "Awaits one action at a time");
            var carrying = owed.Values.SingleOrDefault();
            Ensure(carrying is { Cancelled: false }, "A command its caller cancelled goes no further");
            switch (awaited)
            {
                case Action.Open:
                    Ensure(before.Phase == ItemRuntimePhase.New && !m.Opened, "Opens only a new runtime");
                    break;
                case Action.StartRun start:
                    Ensure(m.Working == ItemWork.None && m.Live is null, "Starts a run only from a state with no work of any run pending");
                    Ensure(m.Clean, "Starts a run only over decoders the open or a reset left clean (#714)");
                    Ensure(start.Run == after.Run, "Starts the run the state numbers");
                    Ensure(start.Work == ItemRuntimeProtocol.WorkOf(after), "Starts the work the item has");
                    Ensure(start.End is null || start.End > start.Floor, "A run stops at the item's end only when it starts before it");

                    // The shell starts the work now: a piece of it may end before the outcome.
                    m = m with
                    {
                        Working = start.Work,
                        WorkRun = start.Run,
                        Live = start.Run,
                        Clean = false,
                        EndRaised = false,
                        SettleOwed = start.HoldForSettle,
                    };
                    break;
                case Action.SeekDemux seek:
                    Ensure(m.Working == ItemWork.None && m.Live is null, "Seeks the demuxer only from a state with no work of any run pending");
                    Ensure(seek.Target == carrying?.Target, "Seeks the demuxer to the target of the command carrying the reposition");
                    break;
                case Action.Settle:
                    Ensure(m.Live == after.Run && m.SettleOwed, "Settles the clocks only on the run it started to settle");
                    m = m with { SettleOwed = false };
                    break;
                case Action.ActivateAudio or Action.PauseAudio or Action.ResumeAudio or Action.DeactivateAudio:
                    Ensure(m.Attached, Shared);
                    break;
                default:
                    Ensure(false, $"{awaited} is not an awaited action");
                    break;
            }
            m = m with { Awaiting = awaited };
        }

        // The commands.
        Ensure(owed.Count <= 1, "Holds at most one command");
        var held = owed.Values.SingleOrDefault();
        Ensure(held?.Id == after.Command?.Id, "Owes an answer exactly to the command it is carrying out");
        m = m with { Outstanding = held };

        if (
            newCommand is Input.Play or Input.Pause
            && !model.Attached
            && !(before.Phase is ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed || before.DisposeRequested)
        )
        {
            Ensure(
                step.Actions.SequenceEqual([new Action.CompleteCommand(newCommand.Command, ItemCommandResult.Refused(ItemRefusal.Detached))])
                    && step.Awaited is null
                    && after == before,
                "A detached runtime refuses a play and a pause, and changes nothing"
            );
        }

        if (newCommand is not null && (before.Phase is ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed || before.DisposeRequested))
        {
            Ensure(
                step.Actions.SequenceEqual([new Action.CompleteCommand(newCommand.Command, ItemCommandResult.Refused(ItemRefusal.Disposed))])
                    && step.Awaited is null
                    && after == before,
                "A disposing or disposed runtime refuses every command and changes nothing"
            );
        }

        // What a command leaves when it completes Ok: a play, or a reposition that plays on, leaves a
        // run going.
        foreach (var (command, result) in completed)
        {
            if (result.Outcome != ItemCommandOutcome.Ok)
                continue;
            var playing =
                after.Status == ItemRunStatus.Running
                && m.Live == after.Run
                && m.Gates
                && m.Clock == ClockModel.Running
                && (!shape.HasAudio || m.Audio == AudioModel.Playing)
                && !m.SettleOwed;
            switch (command.Kind)
            {
                case ItemCommandKind.Play when m.Opened:
                    Ensure(playing, "A play leaves a run going, the gates open, the clock running and the audio playing");
                    m = m with { Played = true };
                    break;
                case ItemCommandKind.Pause:
                    Ensure(
                        !m.Gates && m.Clock != ClockModel.Running && m.Audio != AudioModel.Playing,
                        "A pause leaves the gates shut, the clock still and the audio silent"
                    );
                    break;
                case ItemCommandKind.Seek or ItemCommandKind.Rewind when m.Opened:
                    Ensure(
                        command.PlaysOn ? playing : after.Status == ItemRunStatus.Idle && !m.Gates,
                        "A reposition of an item that plays plays on from a run it started and settled, with the clock running and the audio playing; any other holds positioned with the gates shut"
                    );
                    break;
                case ItemCommandKind.WarmUp when m.Opened && shape.HasVideo && !m.Played:
                    Ensure(after.Status == ItemRunStatus.Running, "A warm-up leaves a run going");
                    break;
                case ItemCommandKind.Initialize:
                    Ensure(m.Opened && after.Status == ItemRunStatus.Idle, "An open leaves a runtime that may start a run");
                    break;
            }
        }

        // The state and the shell agree.
        var starting = after.IsStartingRun ? ItemRuntimeProtocol.WorkOf(after) & ~after.EndedBeforeStart : ItemWork.None;
        Ensure(after.Resources == m.Alive, "The state names what the shell holds");
        Ensure(m.Working == ItemWork.None || m.WorkRun == after.Run, "Only the current run has work pending");
        Ensure((after.Pending | starting) == m.Working, "The state names the work the shell runs");
        Ensure(m.Live is null || m.Live == after.Run, "Only the current run is live");
        Ensure(
            after.Run == before.Run || (after.Run == before.Run + 1 && m.Working == ItemWork.None && m.Live is null),
            "The run number advances by one, and only once no work of any run is pending"
        );
        Ensure(
            after.Status switch
            {
                ItemRunStatus.Running => m.Live == after.Run,
                _ when after.IsStartingRun => m.Live == after.Run,
                ItemRunStatus.Stopping => m.Live is null && m.Working != ItemWork.None,
                _ => m.Live is null && m.Working == ItemWork.None,
            },
            "The run status says what the shell runs"
        );
        Ensure(
            after.Status != ItemRunStatus.Idle || after.IsStartingRun || m.Clean,
            "A runtime that may start a run has clean decoders"
        );
        Ensure(after.IsBusy == (m.Awaiting is not null), "Busy exactly while an awaited action is under way");
        Ensure(after.AudioActive == (m.Audio != AudioModel.Inactive), "The state knows whether the audio sink is active");
        Ensure(after.Attached == m.Attached, "The state knows whether the runtime is attached");
        Ensure(after.Played == m.Played, "The state knows whether the runtime has played");
        Ensure(after.GatesOpen == m.Gates, "The state knows whether the gates are open");
        Ensure(after.FirstFrameSeen == m.FirstFrame, "The state knows whether the first frame was decoded");
        Ensure(after.Floor == TimeSpan.Zero || after.Status == ItemRunStatus.Idle, "A run's start is pending only while a run may start");
        Ensure(after.Phase != ItemRuntimePhase.Disposing || after.Command is null, "No command runs while the runtime disposes");
        Ensure(after.Command is not { Stage: ItemCommandStage.Stopping } || after.Status == ItemRunStatus.Stopping, "A command waits for a stop only while one is under way");
        Ensure(
            after.Command is not { Stage: ItemCommandStage.WaitingForFirstFrame }
                || (after.Status == ItemRunStatus.Running && !m.FirstFrame && (m.Working & (ItemWork.Pump | ItemWork.Graph)) != 0),
            "A warm-up waits only on a run that can still decode its first frame"
        );
        Ensure((after.Phase == ItemRuntimePhase.Opening) == (m.Awaiting is Action.Open), "Opening exactly while the open is under way");
        Ensure(!m.Played || after.Phase != ItemRuntimePhase.Open || m.Clock != ClockModel.Stopped, "A runtime that has played has started its clock");
        Ensure(
            !(after.Phase == ItemRuntimePhase.Open && m.Gates && m.Live is not null && (m.Working & ItemWork.Graph) != 0)
                || (m.Clock == ClockModel.Running && (!shape.HasAudio || m.Audio == AudioModel.Playing)),
            "Frames pass the gates of a run that is still delivering only while the clock runs and the audio plays"
        );
        Ensure(
            !(after.Phase == ItemRuntimePhase.Open && m.Audio == AudioModel.Playing) || m.Gates,
            "The audio sink plays only while frames pass the gates"
        );
        Ensure(
            !(after.Phase == ItemRuntimePhase.Open && after.Command is null && m.Live is not null && (m.Working & ItemWork.Graph) != 0 && !m.Gates)
                || m.Clock != ClockModel.Running,
            "At rest, a run held behind shut gates is held with its clock"
        );

        if (after.Phase == ItemRuntimePhase.Disposed)
        {
            Ensure(
                m is { Alive: ItemResources.None, Working: ItemWork.None, Live: null, DisposeCompleted: true, Awaiting: null, Outstanding: null },
                "A disposed runtime holds nothing, runs nothing and owes nothing"
            );
        }
        else
        {
            Ensure(!m.DisposeCompleted, "Only a disposed runtime has completed its dispose");
        }

        return new Node(after, m);
    }

    private static Owed Owe(Input.CommandInput command, Model m)
    {
        // What the shell sent: whether the clock was paused, against what the shell knows.
        var playsOn = m.Played && m.Clock != ClockModel.Paused;
        return command switch
        {
            Input.Initialize i => new(i.Command, ItemCommandKind.Initialize, i.Start, false, false),
            Input.WarmUp w => new(w.Command, ItemCommandKind.WarmUp, w.ClockPosition, false, false),
            Input.Play p => new(p.Command, ItemCommandKind.Play, p.ClockPosition, playsOn, false),
            Input.Pause p => new(p.Command, ItemCommandKind.Pause, null, false, false),
            Input.Seek s => new(s.Command, ItemCommandKind.Seek, s.Target, playsOn, false),
            Input.Rewind r => new(r.Command, ItemCommandKind.Rewind, r.Position, playsOn, false),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }

    // What an awaited action that succeeded did to what the shell holds. A run's work started when
    // the start was asked for.
    private static Model Succeeded(Model m, Action awaited) =>
        awaited switch
        {
            Action.ActivateAudio => m with { Audio = m.Gates && m.Live is not null ? AudioModel.Playing : AudioModel.Primed },
            Action.PauseAudio => m with { Audio = m.Audio == AudioModel.Inactive ? AudioModel.Inactive : AudioModel.Paused },
            Action.ResumeAudio => m with { Audio = m.Audio == AudioModel.Paused ? AudioModel.Playing : m.Audio },
            Action.DeactivateAudio => m with { Audio = AudioModel.Inactive },
            _ => m,
        };

    // What an awaited action that failed or was cancelled left: a start that fails started nothing.
    private static Model Failed(Model m, Action awaited) =>
        awaited is Action.StartRun
            ? m with { Working = ItemWork.None, Live = null, Clean = true, SettleOwed = false }
            : m;
}
