using Xunit;

using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// A source's frame budget (ADR-0081, decision 3): the item its pump writes, each edge's
/// capacity and each node's declaration, up to the first storage boundary on each path. No graph
/// runs except in the hook tests.
/// </summary>
public sealed class FrameBudgetTests
{
    [Fact]
    public void ALinearPath_SumsThePumpTheEdgesAndTheNodes()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).Then(Op("op", FrameHolding.InFlight)).To(Sink("sink", FrameHolding.AtMost(3)));

        // Pump 1, edge 1, op 1, edge 1, sink 3.
        Assert.Equal(7, graph.FrameBudgetFor(source.Output).Frames);
    }

    [Fact]
    public void AStorageBoundary_EndsThePath()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).Then(Op("convert", FrameHolding.Boundary)).To(Sink("sink", holding: null));

        // Past the boundary the frames are new ones, so even an unbounded sink does not count.
        Assert.Equal(3, graph.FrameBudgetFor(source.Output).Frames);
    }

    [Fact]
    public void AnUndeclaredHolder_LeavesTheBudgetUnbounded_AndIsNamed()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).Then(Op("mystery", holding: null)).To(Sink("sink", FrameHolding.AtMost(1)));

        var budget = graph.FrameBudgetFor(source.Output);

        Assert.Null(budget.Frames);
        Assert.Equal("mystery", budget.UnboundedHolder);
    }

    [Fact]
    public void EachBranchOfAFanOut_AddsItsOwn()
    {
        var graph = new GraphRunner();
        var source = Source();
        var head = graph.Pipeline(source).Then(Op("op", FrameHolding.InFlight));
        head.Branch(EdgeOptions.LatestWins(2)).To(Sink("preview", FrameHolding.AtMost(2)));
        head.To(Sink("record", FrameHolding.InFlight));

        // Pump 1, edge 1, op 1; preview: edge 2, sink 2; record: edge 1, sink 1.
        Assert.Equal(9, graph.FrameBudgetFor(source.Output).Frames);
    }

    [Fact]
    public void AfterANodeThatGathersFrames_EachItemCountsItsFrames()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph
            .Pipeline(source)
            .Then(Op("gate", FrameHolding.AtMost(5, forwardsStorage: true, framesPerOutputItem: 10)))
            .To(Sink("encoder", FrameHolding.AtMost(3)), EdgeOptions.Buffered(1));

        // Pump 1, edge 1, gate 5; then the edge's one clip of 10 and the encoder's 3 clips of 10.
        Assert.Equal(47, graph.FrameBudgetFor(source.Output).Frames);
    }

    [Fact]
    public void AJoinFedFromBothSidesOfAFork_CountsEachInput()
    {
        var graph = new GraphRunner();
        var source = Source();
        var head = graph.Pipeline(source);
        var join = Join(maxRetained: 4);
        var branch = head.Branch(EdgeOptions.LatestWins(1)).Then(Op("detect", FrameHolding.InFlight));
        head.Join(branch, join, EdgeOptions.Default, EdgeOptions.LatestWins(1)).To(Sink("sink", FrameHolding.InFlight));

        // Pump 1. Trunk: edge 1, primary 1. Branch: edge 1, detect 1, edge 1, secondary 5.
        // Output: edge 1, sink 1.
        Assert.Equal(13, graph.FrameBudgetFor(source.Output).Frames);
    }

    /// <summary>
    /// A join whose primary carries gathered items of 10 frames and whose secondary carries
    /// single frames. Its output can be either, so everything after it counts 10 per item,
    /// whichever input was wired first.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AJoinReachedWithDifferentFramesPerItem_CountsTheLargest(bool secondaryWiredFirst)
    {
        var graph = new GraphRunner();
        var source = Source();
        var gather = Op("gather", FrameHolding.AtMost(2, forwardsStorage: true, framesPerOutputItem: 10));
        var join = Join(maxRetained: 4);
        if (secondaryWiredFirst)
        {
            graph.Connect(source.Output, join.Secondary);
            graph.Connect(source.Output, gather.Input);
        }
        else
        {
            graph.Connect(source.Output, gather.Input);
            graph.Connect(source.Output, join.Secondary);
        }
        graph.Connect(gather.Output, join.Primary);
        graph.Pipeline(join.Output).To(Sink("sink", FrameHolding.AtMost(3)));

        // Pump 1. Gather: edge 1, holds 2. Primary: edge 10, holds 10. Secondary: edge 1, holds
        // 5. Output: edge 10, sink 3 items of 10.
        Assert.Equal(70, graph.FrameBudgetFor(source.Output).Frames);
    }

    [Fact]
    public async Task ASourceWithAHook_IsToldItsBudgetBeforeAnyPump()
    {
        FrameBudget? told = null;
        int pulls = 0;
        var source = new SourceNode<RefBox<int>>(
            "source",
            _ =>
            {
                pulls++;
                return ValueTask.FromResult<RefBox<int>?>(null);
            },
            onBudget: budget =>
            {
                Assert.Equal(0, pulls);
                told = budget;
            }
        );
        var graph = new GraphRunner();
        graph.Pipeline(source).To(Sink("sink", FrameHolding.AtMost(2)));

        await graph.RunAsync(CancellationToken.None);

        Assert.Equal(4, told?.Frames);
        Assert.Equal(1, pulls);
    }

    [Fact]
    public async Task AHookThatThrows_RefusesTheRunBeforeAnyPump()
    {
        int pulls = 0;
        var source = new SourceNode<RefBox<int>>(
            "source",
            _ =>
            {
                pulls++;
                return ValueTask.FromResult<RefBox<int>?>(null);
            },
            onBudget: budget =>
                throw new InvalidOperationException($"unbounded at {budget.UnboundedHolder}")
        );
        var graph = new GraphRunner();
        graph.Pipeline(source).To(Sink("sink", holding: null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None)
        );

        Assert.Contains("unbounded at sink", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, pulls);
    }

    private static SourceNode<RefBox<int>> Source() =>
        new("source", _ => ValueTask.FromResult<RefBox<int>?>(null));

    private static OperatorNode<RefBox<int>, RefBox<int>> Op(string id, FrameHolding? holding) =>
        new(id, (item, _) => ValueTask.FromResult<RefBox<int>?>(item), holding: holding);

    private static SinkNode<RefBox<int>> Sink(string id, FrameHolding? holding) =>
        new(id, (_, _) => ValueTask.CompletedTask, holding: holding);

    private static SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>> Join(int maxRetained) =>
        new(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(1),
            maxRetained: maxRetained
        );
}
