using Xunit;

// `Graph` is both a namespace and a type here, so the runner is aliased the way the other
// suites in this project alias it.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// The graph refuses to run when a node that needs every input is missing one, and says which.
/// </summary>
/// <remarks>
/// <para>
/// The rules are a function of the edge list, so these tests wire a graph and start it; nothing
/// waits on a pump, because a rejected topology throws before the first one is created.
/// </para>
/// <para>
/// A pump does refuse an unconnected input, but by then the producer is already writing into a
/// capacity-1 channel that the exiting pump will never read, and the run hangs. Disabling the
/// pre-run check makes the first test below stop terminating rather than fail. These tests pin
/// that the graph refuses before any pump starts, which is observable: the source's producer is
/// never invoked.
/// </para>
/// </remarks>
public sealed class GraphTopologyValidationTests
{
    [Fact]
    public async Task AJoinWithNoSecondaryEdge_IsRefusedBeforeTheRunStarts()
    {
        var graph = new GraphRunner();
        int produced = 0;
        var source = CountingSource([Item(1), Item(2)], () => produced++);
        var join = Join();
        var sink = new SinkNode<RefBox<int>>("sink", (_, _) => ValueTask.CompletedTask);

        graph.Pipeline(source).ToPrimary(join);
        graph.Pipeline(join.Output).To(sink);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None)
        );

        Assert.Contains("join/secondary", ex.Message, StringComparison.Ordinal);
        Assert.Contains("never connected", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'join'", ex.Message, StringComparison.Ordinal);

        // Nothing pumped. The pump-time check would have run the source first and faulted the
        // join's pump instead.
        Assert.Equal(0, produced);
    }

    [Fact]
    public async Task AJoinWithNoPrimaryEdge_IsRefused()
    {
        var graph = new GraphRunner();
        var source = Source([Item(1)]);
        var join = Join();
        var sink = new SinkNode<RefBox<int>>("sink", (_, _) => ValueTask.CompletedTask);

        graph.Pipeline(source).ToSecondary(join);
        graph.Pipeline(join.Output).To(sink);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None)
        );

        Assert.Contains("join/primary", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullyWiredJoin_Runs()
    {
        var graph = new GraphRunner();
        var primary = Source([Item(1), Item(2)]);
        var secondary = Source([Item(10)]);
        var join = Join();
        var seen = new List<int>();
        var sink = new SinkNode<RefBox<int>>(
            "sink",
            (item, _) =>
            {
                seen.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

        graph.Pipeline(primary).ToPrimary(join);
        graph.Pipeline(secondary).ToSecondary(join, EdgeOptions.Buffered(4));
        graph.Pipeline(join.Output).To(sink);

        await graph.RunAsync(CancellationToken.None);

        // The point is that it ran at all: the same wiring minus one edge is the case above.
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task ALinearGraph_IsUnaffected()
    {
        var graph = new GraphRunner();
        var source = Source([Item(1), Item(2), Item(3)]);
        var seen = new List<int>();
        var sink = new SinkNode<RefBox<int>>(
            "sink",
            (item, _) =>
            {
                seen.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

        graph.Pipeline(source).To(sink);
        await graph.RunAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public async Task AnUnwiredSinkIsStillThePumpsToRefuse()
    {
        // Only a node that declares every input required is checked here. A sink added and never
        // wired is left to the pump's own RequireConnected, which is where that error has always
        // come from, so this rule does not widen what a graph rejects. The pump's refusal is that
        // node's fault.
        var graph = new GraphRunner();
        var source = Source([Item(1)]);
        var sink = new SinkNode<RefBox<int>>("sink", (_, _) => ValueTask.CompletedTask);
        graph.Add(new SinkNode<RefBox<int>>("unused", (_, _) => ValueTask.CompletedTask));

        graph.Pipeline(source).To(sink);

        var fault = await Assert.ThrowsAsync<GraphFaultException>(
            () => graph.RunAsync(CancellationToken.None)
        );
        Assert.Equal("unused", fault.NodeId);
        var ex = Assert.IsType<InvalidOperationException>(fault.InnerException);
        Assert.Contains("has no upstream edge connected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoNodesWithOneId_AreRefusedBeforeTheRunStarts()
    {
        var graph = new GraphRunner();
        int produced = 0;
        var source = CountingSource([Item(1)], () => produced++);
        var step = new OperatorNode<RefBox<int>, RefBox<int>>(
            "step",
            (item, _) => ValueTask.FromResult<RefBox<int>?>(item)
        );
        var sink = new SinkNode<RefBox<int>>("step", (_, _) => ValueTask.CompletedTask);

        graph.Pipeline(source).Then(step).To(sink);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None)
        );

        Assert.Contains("2 nodes have the id 'step'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, produced);
    }

    [Theory]
    [InlineData(new[] { "a", "b" }, new string[0])]
    [InlineData(new[] { "a", "b", "a" }, new[] { "2 nodes have the id 'a'" })]
    [InlineData(new[] { "a", "A" }, new string[0])]
    [InlineData(
        new[] { "b", "a", "b", "a", "b" },
        new[] { "3 nodes have the id 'b'", "2 nodes have the id 'a'" }
    )]
    public void EachIdHeldByMoreThanOneNode_IsReportedOnce(string[] ids, string[] expected)
    {
        var nodes = ids
            .Select(id => (INode)new SinkNode<RefBox<int>>(id, (_, _) => ValueTask.CompletedTask))
            .ToList();

        var errors = GraphTopology.Validate(nodes, []);

        Assert.Equal(expected.Length, errors.Count);
        for (int i = 0; i < expected.Length; i++)
            Assert.StartsWith(expected[i], errors[i], StringComparison.Ordinal);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static RefBox<int> Item(int value) => RefBox.Of(value);

    private static SourceNode<RefBox<int>> CountingSource(
        IReadOnlyList<RefBox<int>> items,
        Action onProduce
    )
    {
        int index = 0;
        return new SourceNode<RefBox<int>>(
            "source" + Guid.NewGuid().ToString("N")[..4],
            _ =>
            {
                onProduce();
                return ValueTask.FromResult(index < items.Count ? items[index++] : null);
            }
        );
    }

    private static SourceNode<RefBox<int>> Source(IReadOnlyList<RefBox<int>> items)
    {
        int index = 0;
        return new SourceNode<RefBox<int>>(
            "source" + Guid.NewGuid().ToString("N")[..4],
            _ => ValueTask.FromResult(index < items.Count ? items[index++] : null)
        );
    }

    /// <summary>A join keyed on the boxed value as both timestamp and interval.</summary>
    private static SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>> Join() =>
        new(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(2)
        );
}
