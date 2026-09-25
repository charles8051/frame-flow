using Xunit;

using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// A fork and a rejoin expressed as a chain, and what the chain refuses. Every consumer of a
/// forked port shares the item by <c>AddRef</c> (ADR-0080).
/// </summary>
public sealed class GraphChainForkTests
{
    [Fact]
    public async Task AForkOfBranchesOnly_RunsAndReleasesEveryItem()
    {
        var graph = new GraphRunner();
        var emitted = new List<RefBox<int>>();
        var source = CountedSource(2, emitted);
        var left = new List<int>();
        var right = new List<int>();

        var head = graph.Pipeline(source);
        head.Branch(EdgeOptions.Buffered(4)).To(Collect(left, "left"));
        head.Branch(EdgeOptions.Buffered(4)).To(Collect(right, "right"));

        await graph.RunAsync(CancellationToken.None);

        Assert.Equal([1, 2], left);
        Assert.Equal([1, 2], right);
        Assert.All(emitted, item => Assert.Equal(0, item.RefCount));
    }

    [Fact]
    public async Task AJoinBuiltFromTheChain_PairsTheBranchBackOntoTheTrunk()
    {
        var graph = new GraphRunner();
        var source = CountedSource(3);
        var joined = new List<int>();

        var head = graph.Pipeline(source);
        var detections = head.Branch(EdgeOptions.LatestWins(1)).Then(Double("double"));

        head.Join(detections, PairingJoin(), EdgeOptions.Default, EdgeOptions.Buffered(4))
            .To(
                new SinkNode<RefBox<int>>(
                    "sink",
                    (item, _) =>
                    {
                        joined.Add(item.Value);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        await graph.RunAsync(CancellationToken.None);

        // Every primary fires the join once, which is the shape LiveCaptioning's detection
        // overlay wires by hand today.
        Assert.Equal(3, joined.Count);
    }

    [Fact]
    public void AJoinAcrossTwoGraphs_IsRejectedBeforeEitherEdgeIsWired()
    {
        var graphA = new GraphRunner();
        var graphB = new GraphRunner();
        var primary = graphA.Pipeline(CountedSource(1));
        var secondary = graphB.Pipeline(CountedSource(1));
        var join = PairingJoin();

        var ex = Assert.Throws<ArgumentException>(
            () => primary.Join(secondary, join, EdgeOptions.Default, EdgeOptions.Buffered(4))
        );
        Assert.Contains("different graph", ex.Message, StringComparison.Ordinal);

        // Neither edge was wired, so the join is still free to be wired properly afterwards.
        // A half-wired join would fail this second call with "already connected".
        primary.Join(
            graphA.Pipeline(CountedSource(1)),
            join,
            EdgeOptions.Default,
            EdgeOptions.Buffered(4)
        );
    }

    [Fact]
    public void ConfiguringABranchEdgeTwice_IsRejected()
    {
        // The branch's first edge is configured by Branch. Passing options on that hop as well
        // would have to silently win or silently lose, and the caller reads one at the call site
        // either way.
        var graph = new GraphRunner();
        var head = graph.Pipeline(CountedSource(1));

        var ex = Assert.Throws<ArgumentException>(
            () => head.Branch(EdgeOptions.Buffered(4)).Then(Double("double"), EdgeOptions.LatestWins(1))
        );
        Assert.Contains("configured by Branch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABranchWithoutOptions_IsRejected()
    {
        // Connect would read missing options as EdgeOptions.Default: the capacity-1 blocking
        // edge that makes a slow branch hold the trunk back frame for frame.
        var graph = new GraphRunner();
        var head = graph.Pipeline(CountedSource(1));

        Assert.Throws<ArgumentNullException>(() => head.Branch(null!));
    }

    [Fact]
    public async Task ALinearChain_IsUnchanged()
    {
        var graph = new GraphRunner();
        var source = CountedSource(3);
        var seen = new List<int>();

        graph.Pipeline(source)
            .Then(Double("double"))
            .To(
                new SinkNode<RefBox<int>>(
                    "sink",
                    (item, _) =>
                    {
                        seen.Add(item.Value);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        await graph.RunAsync(CancellationToken.None);

        Assert.Equal([2, 4, 6], seen);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static SourceNode<RefBox<int>> CountedSource(int count, List<RefBox<int>>? emitted = null)
    {
        int next = 0;
        return new SourceNode<RefBox<int>>(
            "source",
            _ =>
            {
                if (next >= count)
                    return ValueTask.FromResult<RefBox<int>?>(null);
                var item = RefBox.Of(++next);
                emitted?.Add(item);
                return ValueTask.FromResult<RefBox<int>?>(item);
            }
        );
    }

    private static SinkNode<RefBox<int>> Collect(List<int> into, string id) =>
        new(
            id,
            (item, _) =>
            {
                into.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

    private static OperatorNode<RefBox<int>, RefBox<int>> Double(string id) =>
        new(id, (item, _) => ValueTask.FromResult<RefBox<int>?>(RefBox.Of(item.Value * 2)));

    private static SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>> PairingJoin() =>
        new(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(5)
        );
}
