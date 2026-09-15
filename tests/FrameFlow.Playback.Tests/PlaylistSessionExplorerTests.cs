using System.Collections.Immutable;
using Xunit.Abstractions;
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

/// <summary>
/// Runs <see cref="PlaylistSessionExplorer"/> over scenarios chosen per defect class, starting from the
/// transcripts in <c>PlaylistSessionTranscriptTests</c>. Step 4 of
/// <c>docs/adr/playlist-session-protocol.md</c>.
/// </summary>
/// <remarks>
/// The core must break no invariant in any scenario, apart from the expected failure decision 7
/// names. Each seeded defect removes one rule at the core's boundary, by changing the state or input a
/// step sees, and the explorer must find an ordering that breaks the invariant that rule keeps.
/// </remarks>
public sealed class PlaylistSessionExplorerTests(ITestOutputHelper output)
{
    private static readonly ExplorerEvent Skip = new ExplorerEvent.Skip();
    private static readonly ExplorerEvent EndOfStream = new ExplorerEvent.EndOfStream();
    private static readonly ExplorerEvent Fault = new ExplorerEvent.Fault();
    private static readonly ExplorerEvent Dispose = new ExplorerEvent.Dispose();

    private static ExplorerCommand[] Load(params ExplorerCommand[] then) =>
        [ExplorerCommand.Initialize, ExplorerCommand.WarmUp, .. then];

    private static readonly ExplorerScenario[] Scenarios =
    [
        // A Play, Pause or Seek the controller sends while the end of the queue is on its way.
        new(
            "The end of the queue",
            RepeatMode.Off,
            ["a"],
            Load(ExplorerCommand.Play, ExplorerCommand.Pause, ExplorerCommand.Play, ExplorerCommand.Seek),
            [EndOfStream, Skip]
        ),
        // End-of-stream from a run a seek replaced, with seeks that can be cancelled either side of
        // the run advancing.
        new(
            "Seeks and stale end-of-stream",
            RepeatMode.Off,
            ["a", "b"],
            Load(ExplorerCommand.Play, ExplorerCommand.Seek, ExplorerCommand.Pause, ExplorerCommand.Seek),
            [EndOfStream, EndOfStream]
        )
        {
            CommandsCanBeCancelled = true,
        },
        // A jump or skip arriving before, during and after an advance, with a Pause waiting on it.
        new(
            "Jumps during advances",
            RepeatMode.Off,
            ["a", "b", "c"],
            Load(ExplorerCommand.Play, ExplorerCommand.Pause, ExplorerCommand.Play),
            [EndOfStream, new ExplorerEvent.Jump("c"), Skip]
        ),
        // The in-place replay under One, a rewind that fails, a skip that wraps to the same item, and
        // a fault, which rebuilds the item rather than rewinding it.
        new(
            "Replay under One",
            RepeatMode.One,
            ["a"],
            Load(ExplorerCommand.Play, ExplorerCommand.Pause, ExplorerCommand.Play),
            [EndOfStream, EndOfStream, Skip, Fault]
        )
        {
            Failures = ExplorerFailures.Rewind,
        },
        // Items that fail to open, warm or start, and a worker fault, including one before the first
        // play and one from a runtime an advance has replaced.
        new("Failures", RepeatMode.All, ["a", "b"], Load(ExplorerCommand.Play), [EndOfStream, Fault])
        {
            Failures = ExplorerFailures.Open | ExplorerFailures.WarmUp | ExplorerFailures.Play,
        },
        // Disposal at any point, cancelling whatever item action is in flight.
        new(
            "Disposal",
            RepeatMode.Off,
            ["a", "b"],
            Load(ExplorerCommand.Play, ExplorerCommand.Pause),
            [EndOfStream, Skip, Dispose]
        ),
        // Edits racing the advances: removing the current item, enqueueing and jumping.
        new(
            "Edits",
            RepeatMode.Off,
            ["a", "b"],
            Load(ExplorerCommand.Play),
            [
                new ExplorerEvent.RemoveCurrent(),
                new ExplorerEvent.Enqueue("x"),
                new ExplorerEvent.Jump("b"),
                EndOfStream,
                EndOfStream,
            ]
        ),
        // The gap decision 7 names as the expected failure.
        new(
            "A seek before the first play",
            RepeatMode.Off,
            ["a", "b"],
            Load(ExplorerCommand.Seek, ExplorerCommand.Play),
            [Skip]
        ),
    ];

    public static TheoryData<string> ScenarioNames => new(Scenarios.Select(s => s.Name));

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void TheCore_BreaksNoInvariant_OtherThanTheExpectedFailure(string name)
    {
        var result = PlaylistSessionExplorer.Explore(Scenario(name));
        output.WriteLine($"{name}: {result.States} states");

        var unexpected = result
            .Violations.Where(v => v.Invariant != PlaylistSessionExplorer.ExpectedFailure)
            .ToList();
        Assert.True(unexpected.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, unexpected));
    }

    [Fact]
    public void TheExpectedFailure_IsFound()
    {
        var result = PlaylistSessionExplorer.Explore(Scenario("A seek before the first play"));

        var found = Assert.Single(result.Violations, v => v.Invariant == PlaylistSessionExplorer.ExpectedFailure);
        output.WriteLine(found.ToString());
    }

    private sealed record SeededDefect(string Scenario, string Invariant, StepFunction Step);

    private static readonly Dictionary<string, SeededDefect> Defects = new()
    {
        // The defect decision 8 names: the rule that keeps Ended against a queued Play, removed.
        ["A Play at Ended plays"] = new(
            "The end of the queue",
            ExplorerInvariants.PlaysOrSeeksAfterTheEnd,
            (state, queue, input, context) =>
                input is PlaylistSessionInput.Play && state.Work is null && state.Run == PlaylistRunState.Ended
                    ? PlaylistSessionProtocol.Step(state with { Run = PlaylistRunState.Paused }, queue, input, context)
                    : PlaylistSessionProtocol.Step(state, queue, input, context)
        ),
        ["An end-of-stream's run is not compared"] = new(
            "Seeks and stale end-of-stream",
            ExplorerInvariants.ReplacedRunActs,
            (state, queue, input, context) =>
                input is PlaylistSessionInput.EndOfStream end && state.Work is null && state.Item is { } item
                    ? PlaylistSessionProtocol.Step(state, queue, end with { Run = item.KnownRun }, context)
                    : PlaylistSessionProtocol.Step(state, queue, input, context)
        ),
        ["A fault's generation is not compared"] = new(
            "Failures",
            ExplorerInvariants.ReplacedRuntimeActs,
            (state, queue, input, context) =>
                input is PlaylistSessionInput.Fault fault && state.Work is null
                    ? PlaylistSessionProtocol.Step(state, queue, fault with { Generation = state.Generation }, context)
                    : PlaylistSessionProtocol.Step(state, queue, input, context)
        ),
        ["A jump request starts no advance"] = new(
            "Jumps during advances",
            ExplorerInvariants.JumpLeftPending,
            (state, queue, input, context) =>
                input is PlaylistSessionInput.JumpRequested && state.Work is null
                    ? (state, queue, new PlaylistSessionStep(ImmutableArray<PlaylistSessionAction>.Empty, null, Done: true))
                    : PlaylistSessionProtocol.Step(state, queue, input, context)
        ),
    };

    public static TheoryData<string> SeededDefects => new(Defects.Keys);

    [Theory]
    [MemberData(nameof(SeededDefects))]
    public void ASeededDefect_IsFound(string name)
    {
        var defect = Defects[name];

        var result = PlaylistSessionExplorer.Explore(Scenario(defect.Scenario), defect.Step);

        var found = Assert.Single(result.Violations, v => v.Invariant == defect.Invariant);
        output.WriteLine($"{result.States} states");
        output.WriteLine(found.ToString());
    }

    [Fact]
    public void RepeatedEvents_EachHappen()
    {
        // Each occurrence of an event is its own move. Two equal occurrences lead to the same state,
        // and the one not taken stays to happen later, so both reach the core.
        var generations = new HashSet<int>();
        _ = PlaylistSessionExplorer.Explore(
            Scenario("Seeks and stale end-of-stream"),
            (state, queue, input, context) =>
            {
                if (input is PlaylistSessionInput.EndOfStream end)
                    generations.Add(end.Generation);
                return PlaylistSessionProtocol.Step(state, queue, input, context);
            }
        );

        Assert.Contains(0, generations);
        Assert.Contains(1, generations);
    }

    private static ExplorerScenario Scenario(string name) => Scenarios.Single(s => s.Name == name);
}
