using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type
// (FrameFlow.Graph.Graph). Alias the type so the tests can sit in the
// conventional FrameFlow.Graph.Tests namespace without the clash.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// A run that ends on a node's fault throws <see cref="GraphFaultException"/>, which names the node
/// whose fault was recorded first and lists every node that faulted (#499).
/// </summary>
public sealed class GraphFaultTests
{
    private sealed class BoomException(string message) : Exception(message);

    [Fact]
    public async Task ANodeFault_NamesTheNode_AndCarriesWhatItThrew()
    {
        var boom = new BoomException("model failed");
        var graph = new GraphRunner();
        graph.Pipeline(Emit("source", 1, 2, 3)).Then(ThrowingOn(2, "op", boom)).To(Discard("sink"));

        var fault = await Assert.ThrowsAsync<GraphFaultException>(() => graph.RunAsync());

        Assert.Equal("op", fault.NodeId);
        Assert.Same(boom, fault.InnerException);
        var only = Assert.Single(fault.Faults);
        Assert.Equal("op", only.NodeId);
        Assert.Same(boom, only.Exception);
        Assert.Equal("Node 'op' faulted: model failed", fault.Message);
    }

    [Fact]
    public async Task TwoFaults_TheFirstRecordedIsTheCause_AndBothAreListed()
    {
        // 'second' is added first, so its pump starts first and parks its body on the graph token.
        // 'first' faults, which cancels the run, and 'second' fails on that cancellation with an
        // exception of its own. The first recorded leads, ahead of node order.
        var boom = new BoomException("boom");
        var graph = new GraphRunner();
        var second = new SourceNode<RefBox<int>>(
            "second",
            async ct =>
            {
                try
                {
                    await new TaskCompletionSource().Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("torn down");
                }
                return null;
            }
        );
        graph.Pipeline(second).To(Discard("second-sink"));
        graph.Pipeline(Emit("source", 1)).Then(ThrowingOn(1, "first", boom)).To(Discard("sink"));

        var fault = await Assert.ThrowsAsync<GraphFaultException>(
            () => graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15))
        );

        Assert.Equal("first", fault.NodeId);
        Assert.Same(boom, fault.InnerException);
        Assert.Equal(["first", "second"], fault.Faults.Select(f => f.NodeId));
        Assert.IsType<InvalidOperationException>(fault.Faults[1].Exception);
        Assert.Equal("Node 'first' faulted, as did 1 other node: boom", fault.Message);
    }

    [Fact]
    public async Task ACallersCancellation_IsNotWrapped()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = new GraphRunner();
        graph
            .Pipeline(Emit("source", 1))
            .Then(
                new OperatorNode<RefBox<int>, RefBox<int>>(
                    "op",
                    async (item, ct) =>
                    {
                        entered.TrySetResult();
                        await new TaskCompletionSource().Task.WaitAsync(ct).ConfigureAwait(false);
                        return item;
                    }
                )
            )
            .To(Discard("sink"));

        using var cts = new CancellationTokenSource();
        var running = graph.RunAsync(cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => running.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Theory]
    [InlineData(1, "Node 'a' faulted: boom")]
    [InlineData(2, "Node 'a' faulted, as did 1 other node: boom")]
    [InlineData(3, "Node 'a' faulted, as did 2 other nodes: boom")]
    public void TheMessage_NamesTheFirstNode_CountsTheOthers_AndCarriesTheFirstMessage(
        int faulted,
        string expected
    )
    {
        var faults = Enumerable
            .Range(0, faulted)
            .Select(i => new NodeFault(((char)('a' + i)).ToString(), new Exception(i == 0 ? "boom" : "later")))
            .ToList();

        Assert.Equal(expected, new GraphFaultException(faults).Message);
    }

    [Fact]
    public void TheFaults_AreACopy()
    {
        var faults = new List<NodeFault> { new("a", new Exception("boom")) };
        var fault = new GraphFaultException(faults);

        faults.Add(new NodeFault("b", new Exception("later")));

        Assert.Single(fault.Faults);
    }

    [Fact]
    public void NoFaults_OrANullOne_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new GraphFaultException([]));
        Assert.Throws<ArgumentException>(() => new GraphFaultException([null!]));
        Assert.Throws<ArgumentNullException>(() => new GraphFaultException(null!));
    }

    [Fact]
    public void Ordered_PutsTheFirstRecordedFirst_ThenTheRestInNodeOrder()
    {
        var a = new NodeFault("a", new Exception());
        var b = new NodeFault("b", new Exception());
        var c = new NodeFault("c", new Exception());

        Assert.Empty(FaultRules.Ordered(null, [null, null]));
        Assert.Equal([b, a, c], FaultRules.Ordered(b, [a, b, c]));
        Assert.Equal([a, c], FaultRules.Ordered(a, [a, null, c]));
        Assert.Equal([c], FaultRules.Ordered(c, [null, null, c]));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static SourceNode<RefBox<int>> Emit(string id, params int[] values)
    {
        int i = 0;
        return new SourceNode<RefBox<int>>(
            id,
            _ => ValueTask.FromResult(i < values.Length ? RefBox.Of(values[i++]) : null)
        );
    }

    private static OperatorNode<RefBox<int>, RefBox<int>> ThrowingOn(int value, string id, Exception ex) =>
        new(
            id,
            (item, _) =>
                item.Value == value ? throw ex : ValueTask.FromResult<RefBox<int>?>(item)
        );

    private static SinkNode<RefBox<int>> Discard(string id) => new(id, (_, _) => ValueTask.CompletedTask);
}
