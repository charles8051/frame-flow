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
/// them.
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
    private static readonly TimeSpan PastTheEnd = TimeSpan.FromSeconds(12);

    private static readonly Exception Boom = new InvalidOperationException("boom");
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
        Assert.Contains(walk, n => n.State.DisposeRequested);
        Assert.Contains(walk, n => n.State.Phase == ItemRuntimePhase.Disposing && n.State.Status == ItemRunStatus.Stopping);
    }

    // ── The walk ────────────────────────────────────────────────────────────

    private sealed record Shape(string Name, ItemResources Resources, bool Attached, bool LatenessRecovery);

    private enum ClockModel
    {
        Stopped,
        Running,
        Paused,
    }

    private enum AudioModel
    {
        Inactive,
        Playing,
        Paused,
    }

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

        /// <summary>The command issued and not completed, and its kind.</summary>
        public int? Outstanding { get; init; }

        public ItemCommandKind? OutstandingKind { get; init; }

        /// <summary>The action under way, whose outcome the walk feeds back.</summary>
        public Action? Awaiting { get; init; }

        public bool Gates { get; init; }

        public ClockModel Clock { get; init; }

        public AudioModel Audio { get; init; }

        /// <summary>No run has started on the decoders since the open, or since they were reset.</summary>
        public bool Clean { get; init; } = true;

        /// <summary>The live run has raised its end of stream.</summary>
        public bool EndRaised { get; init; }

        public bool DisposeCompleted { get; init; }
    }

    private sealed record Node(ItemRuntimeState State, Model Model);

    // Breadth first, each canonical state once, keeping the input that first reached it.
    private static Dictionary<Node, (Node? Parent, Input? Input)> Walk(Shape shape)
    {
        var root = new Node(ItemRuntimeState.Initial(shape.Attached, shape.LatenessRecovery), new Model());
        var seen = new Dictionary<Node, (Node? Parent, Input? Input)> { [root] = (null, null) };
        var queue = new Queue<Node>([root]);
        while (queue.TryDequeue(out var node))
        {
            foreach (var input in Inputs(shape, node))
            {
                var next = Canonical(Apply(shape, node, input, () => PathTo(seen, node)));
                if (seen.TryAdd(next, (node, input)))
                    queue.Enqueue(next);
            }
        }

        return seen;
    }

    private static string PathTo(Dictionary<Node, (Node? Parent, Input? Input)> seen, Node node)
    {
        var inputs = new List<string>();
        for (var at = node; seen[at] is (Node parent, Input input); at = parent)
            inputs.Add(input.ToString());
        inputs.Reverse();
        return $"After [{string.Join(", ", inputs)}]";
    }

    // Holds the run numbers relative to the current run and the command's number as 1. Each check
    // compares them only with the current ones, so the renumbered state checks the same.
    private static Node Canonical(Node node)
    {
        var (state, model) = node;
        var shift = state.Run;
        var command = state.Command?.Id;
        return new Node(
            state with { Run = 0, Command = state.Command is { } c ? c with { Id = 1 } : null },
            model with
            {
                WorkRun = model.Working == ItemWork.None ? 0 : model.WorkRun - shift,
                Live = model.Live - shift,
                Outstanding = model.Outstanding is null ? null : model.Outstanding == command ? 1 : -1,
                Awaiting = model.Awaiting is Action.StartRun start ? start with { Run = start.Run - shift } : model.Awaiting,
            }
        );
    }

    private static IEnumerable<Input> Inputs(Shape shape, Node node)
    {
        var (state, model) = node;

        // The in-progress command is 1 in a canonical state, so a new one is 2.
        const int Fresh = 2;
        yield return new Input.Initialize(Fresh, TimeSpan.Zero, null);
        yield return new Input.Initialize(Fresh, Start, End);
        yield return new Input.WarmUp(Fresh, Clock);
        yield return new Input.Play(Fresh, Clock, ClockPaused: false);
        yield return new Input.Play(Fresh, Clock, ClockPaused: true);
        yield return new Input.Pause(Fresh);
        yield return new Input.Seek(Fresh, Target, ClockPaused: false);
        yield return new Input.Seek(Fresh, Target, ClockPaused: true);
        yield return new Input.Rewind(Fresh, TimeSpan.Zero, End, ClockPaused: false);
        yield return new Input.Rewind(Fresh, TimeSpan.Zero, null, ClockPaused: true);
        yield return new Input.Rewind(Fresh, PastTheEnd, null, ClockPaused: false);

        if (state.Command is { } command)
            yield return new Input.CallerCancelled(command.Id);
        yield return new Input.Attach();
        yield return new Input.Dispose();

        if (model.Awaiting is Action.Open)
            yield return new Input.Opened(shape.Resources, Duration);
        else if (model.Awaiting is not null)
            yield return new Input.Performed(ItemActionOutcome.Ok);
        if (model.Awaiting is not null)
        {
            yield return new Input.Performed(ItemActionOutcome.Failed, Boom);
            yield return new Input.Performed(ItemActionOutcome.Cancelled, Cancellation);
        }

        foreach (var work in Pieces(model.Working))
        {
            yield return new Input.WorkEnded(model.WorkRun, work, ItemWorkEnding.Completed);
            yield return new Input.WorkEnded(model.WorkRun, work, ItemWorkEnding.Cancelled);
            yield return new Input.WorkEnded(model.WorkRun, work, ItemWorkEnding.Faulted, Boom);
        }

        if (!state.FirstFrameSeen && state.HasVideo && (model.Working & ItemWork.Graph) != 0)
            yield return new Input.FirstFrameDecoded();
    }

    private static IEnumerable<ItemWork> Pieces(ItemWork work) =>
        new[] { ItemWork.Pump, ItemWork.Graph, ItemWork.Recovery, ItemWork.EmptyEnd }.Where(w => (work & w) != 0);

    // From any state, a dispose and the end of whatever runs reaches a disposed runtime that owes
    // nothing: no state is stuck, and every command and the dispose complete.
    private static void Drain(Shape shape, Node node, Func<string> path)
    {
        var at = Apply(shape, node, new Input.Dispose(), path);
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
            at = Apply(shape, at, next, path);
        }

        if (at.State.Phase != ItemRuntimePhase.Disposed)
            Assert.Fail($"A dispose never finished. {path()}, then a dispose: now {at.State}, shell {at.Model}.");
    }

    // ── The shell, and the checks ───────────────────────────────────────────

    // Performs one step on the model, checking each action as it goes and the state both sides
    // arrive at. Returns the model after the step.
    private static Node Apply(Shape shape, Node node, Input input, Func<string> path)
    {
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
        var owed = new Dictionary<int, ItemCommandKind>();
        if (m.Outstanding is { } outstanding)
            owed[outstanding] = m.OutstandingKind!.Value;
        var newCommand = input as Input.CommandInput;
        if (newCommand is not null)
        {
            Ensure(!owed.ContainsKey(newCommand.Command), "The walk numbers each command afresh");
            owed[newCommand.Command] = KindOf(newCommand);
        }
        var completed = new List<(ItemCommandKind Kind, ItemCommandResult Result)>();

        // What the input itself tells the shell.
        var faultFed = false;
        switch (input)
        {
            case Input.Opened opened:
                m = m with { Awaiting = null, Alive = opened.Resources };
                break;
            case Input.Performed performed:
                var awaitedBefore = m.Awaiting!;
                m = m with { Awaiting = null };
                if (performed.Outcome == ItemActionOutcome.Ok)
                    m = Performed(m, awaitedBefore);
                break;
            case Input.WorkEnded ended:
                m = m with { Working = m.Working & ~ended.Work };
                faultFed = ended.Ending == ItemWorkEnding.Faulted;
                break;
        }

        var teardown = after.Phase == ItemRuntimePhase.Disposed;
        var faults = 0;
        foreach (var action in step.Actions)
        {
            switch (action)
            {
                case Action.CancelRun cancel:
                    Ensure(m.Live == cancel.Run, "Cancels only the live run, and each run once");
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
                    m = m with { Gates = true };
                    break;
                case Action.FlushPacer:
                    Ensure(m.Working == ItemWork.None, "Flushes the pacer only once no work is pending");
                    break;
                case Action.ResetDecoders:
                    Ensure(m.Working == ItemWork.None && m.Live is null, "Resets the decoders only from a state with no work of any run pending");
                    m = m with { Clean = true };
                    break;
                case Action.SeatClocks seat:
                    Ensure(m.Working == ItemWork.None, "Seats the clocks for a reposition only once no work is pending");
                    Ensure(seat.Shared == before.Attached, "Seats the shared clocks exactly when attached");
                    break;
                case Action.SeatBaseline:
                    break;
                case Action.StartClocks:
                    m = m with { Clock = ClockModel.Running };
                    break;
                case Action.PauseClocks:
                    m = m with { Clock = m.Clock == ClockModel.Running ? ClockModel.Paused : m.Clock };
                    break;
                case Action.ResumeClocks:
                    m = m with { Clock = m.Clock == ClockModel.Paused ? ClockModel.Running : m.Clock };
                    break;
                case Action.StopClock:
                    Ensure(teardown && before.Attached, "Stops the position clock only in an attached runtime's teardown");
                    m = m with { Clock = ClockModel.Stopped };
                    break;
                case Action.AttachClocks:
                    Ensure(input is Input.Attach && !before.Attached, "Seats the shared clocks once, at the attach");
                    break;
                case Action.DeactivateAudio:
                    Ensure(teardown, "Deactivates the audio sink unawaited only in the teardown");
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
                    Ensure(
                        input is Input.WorkEnded { Ending: ItemWorkEnding.Completed, Work: ItemWork.Graph or ItemWork.EmptyEnd },
                        "Raises the end of stream only when a run's graph, or its empty end, completes"
                    );
                    Ensure(end.Run == after.Run && m.Live == end.Run, "Raises the end of stream only of the current run, while it is live");
                    Ensure(!m.EndRaised, "Raises a run's end of stream once");
                    m = m with { EndRaised = true };
                    break;
                case Action.CompleteCommand complete:
                    Ensure(owed.Remove(complete.Command, out var kind), "Completes each command exactly once");
                    completed.Add((kind, complete.Result));
                    break;
                case Action.CompleteDispose:
                    Ensure(!m.DisposeCompleted, "Completes the dispose once");
                    Ensure(
                        m.Alive == ItemResources.None && m.Working == ItemWork.None && m.Live is null,
                        "Completes the dispose once everything is freed and nothing runs"
                    );
                    m = m with { DisposeCompleted = true };
                    break;
                default:
                    Ensure(false, $"{action} is not an immediate action");
                    break;
            }
        }
        Ensure(faults == (faultFed ? 1 : 0), "Reports every fault once");

        if (step.Awaited is { } awaited)
        {
            Ensure(m.Awaiting is null, "Awaits one action at a time");
            switch (awaited)
            {
                case Action.Open:
                    Ensure(before.Phase == ItemRuntimePhase.New, "Opens only a new runtime");
                    break;
                case Action.StartRun start:
                    Ensure(m.Working == ItemWork.None && m.Live is null, "Starts a run only from a state with no work of any run pending");
                    Ensure(m.Clean, "Starts a run only over decoders the open or a reset left clean (#714)");
                    Ensure(start.Run == after.Run, "Starts the run the state numbers");
                    Ensure(start.Work == ItemRuntimeProtocol.WorkOf(after), "Starts the work the item has");
                    break;
                case Action.SeekDemux:
                    Ensure(m.Working == ItemWork.None && m.Live is null, "Seeks the demuxer only from a state with no work of any run pending");
                    break;
                case Action.Settle:
                    Ensure(m.Live == after.Run, "Settles the clocks only on the run it started");
                    break;
                case Action.ActivateAudio or Action.PauseAudio or Action.ResumeAudio or Action.DeactivateAudio:
                    break;
                default:
                    Ensure(false, $"{awaited} is not an awaited action");
                    break;
            }
            m = m with { Awaiting = awaited };
        }

        // The commands.
        Ensure(owed.Count <= 1, "Holds at most one command");
        var held = owed.Count == 1 ? owed.First() : (KeyValuePair<int, ItemCommandKind>?)null;
        Ensure(held?.Key == after.Command?.Id, "Owes an answer exactly to the command it is carrying out");
        m = m with { Outstanding = held?.Key, OutstandingKind = held?.Value };

        if (newCommand is not null && (before.Phase is ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed || before.DisposeRequested))
        {
            Ensure(
                step.Actions.SequenceEqual([new Action.CompleteCommand(newCommand.Command, ItemCommandResult.Refused(ItemRefusal.Disposed))])
                    && step.Awaited is null
                    && after == before,
                "A disposing or disposed runtime refuses every command and changes nothing"
            );
        }

        // The state and the shell agree.
        Ensure(after.Resources == m.Alive, "The state names what the shell holds");
        Ensure(m.Working == ItemWork.None || m.WorkRun == after.Run, "Only the current run has work pending");
        Ensure(after.Pending == m.Working, "The state names the work the shell runs");
        Ensure(m.Live is null || m.Live == after.Run, "Only the current run is live");
        Ensure(
            after.Run == before.Run || (after.Run == before.Run + 1 && m.Working == ItemWork.None && m.Live is null),
            "The run number advances by one, and only once no work of any run is pending"
        );
        Ensure(
            after.Status switch
            {
                ItemRunStatus.Running => m.Live == after.Run,
                ItemRunStatus.Stopping => m.Live is null && m.Working != ItemWork.None,
                _ => m.Live is null && m.Working == ItemWork.None,
            },
            "The run status says what the shell runs"
        );
        Ensure(
            after.Status != ItemRunStatus.Idle || m.Clean,
            "A runtime that may start a run has clean decoders"
        );
        Ensure(after.IsBusy == (m.Awaiting is not null), "Busy exactly while an awaited action is under way");
        Ensure(after.AudioActive == (m.Audio != AudioModel.Inactive), "The state knows whether the audio sink is active");
        Ensure(after.Floor == TimeSpan.Zero || after.Status == ItemRunStatus.Idle, "A run's start is pending only while a run may start");
        Ensure(after.Phase != ItemRuntimePhase.Disposing || after.Command is null, "No command runs while the runtime disposes");
        Ensure(after.Command is not { Stage: ItemCommandStage.Stopping } || after.Status == ItemRunStatus.Stopping, "A command waits for a stop only while one is under way");
        Ensure(
            after.Command is not { Stage: ItemCommandStage.WaitingForFirstFrame }
                || (after.Status == ItemRunStatus.Running && !after.FirstFrameSeen && (after.Pending & (ItemWork.Pump | ItemWork.Graph)) != 0),
            "A warm-up waits only on a run that can still decode its first frame"
        );
        Ensure((after.Phase == ItemRuntimePhase.Opening) == (after.Command is { Stage: ItemCommandStage.Opening }), "Opening exactly while the open is under way");
        Ensure(!after.Played || after.Phase != ItemRuntimePhase.Open || m.Clock != ClockModel.Stopped, "A runtime that has played has started its clock");

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

        // What a command leaves when it completes Ok: a play or a resume leaves a run going.
        foreach (var (kind, result) in completed)
        {
            if (result.Outcome != ItemCommandOutcome.Ok)
                continue;
            switch (kind)
            {
                case ItemCommandKind.Play when after.Phase == ItemRuntimePhase.Open:
                    Ensure(
                        after.Status == ItemRunStatus.Running
                            && m.Live == after.Run
                            && m.Gates
                            && m.Clock == ClockModel.Running
                            && (!after.HasAudio || m.Audio == AudioModel.Playing),
                        "A play leaves a run going, the gates open, the clock running and the audio playing"
                    );
                    break;
                case ItemCommandKind.Pause:
                    Ensure(
                        !m.Gates && m.Clock != ClockModel.Running && m.Audio != AudioModel.Playing,
                        "A pause leaves the gates shut, the clock still and the audio silent"
                    );
                    break;
                case ItemCommandKind.Seek or ItemCommandKind.Rewind when after.Phase == ItemRuntimePhase.Open:
                    Ensure(
                        after.Status == ItemRunStatus.Running ? m.Gates && m.Live == after.Run : after.Status == ItemRunStatus.Idle && !m.Gates,
                        "A reposition plays on from the run it started, or holds positioned with the gates shut"
                    );
                    break;
                case ItemCommandKind.WarmUp when after.HasVideo && !after.Played:
                    Ensure(after.Status == ItemRunStatus.Running, "A warm-up leaves a run going");
                    break;
                case ItemCommandKind.Initialize:
                    Ensure(after.Phase == ItemRuntimePhase.Open && after.Status == ItemRunStatus.Idle, "An open leaves a runtime that may start a run");
                    break;
            }
        }

        return new Node(after, m);
    }

    // What an awaited action that succeeded did to what the shell holds.
    private static Model Performed(Model m, Action awaited) =>
        awaited switch
        {
            Action.StartRun start => m with
            {
                Working = start.Work,
                WorkRun = start.Run,
                Live = start.Run,
                Clean = false,
                EndRaised = false,
            },
            Action.ActivateAudio => m with { Audio = AudioModel.Playing },
            Action.PauseAudio => m with { Audio = m.Audio == AudioModel.Inactive ? AudioModel.Inactive : AudioModel.Paused },
            Action.ResumeAudio => m with { Audio = m.Audio == AudioModel.Inactive ? AudioModel.Inactive : AudioModel.Playing },
            Action.DeactivateAudio => m with { Audio = AudioModel.Inactive },
            _ => m,
        };

    private static ItemCommandKind KindOf(Input.CommandInput command) =>
        command switch
        {
            Input.Initialize => ItemCommandKind.Initialize,
            Input.WarmUp => ItemCommandKind.WarmUp,
            Input.Play => ItemCommandKind.Play,
            Input.Pause => ItemCommandKind.Pause,
            Input.Seek => ItemCommandKind.Seek,
            Input.Rewind => ItemCommandKind.Rewind,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
}
