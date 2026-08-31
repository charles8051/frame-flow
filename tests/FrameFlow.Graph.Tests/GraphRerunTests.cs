using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type
// (FrameFlow.Graph.Graph). Alias the type so the tests can sit in the
// conventional FrameFlow.Graph.Tests namespace without the clash.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// A graph instance must be re-runnable: calling <see cref="GraphRunner.RunAsync"/>
/// a second time replays the wire-ups against fresh channels without accumulating
/// the prior run's edge state. This is what the cheap <c>RepeatMode.One</c> loop
/// rewind depends on (SubstrateSession re-runs its retained graph instead of
/// rebuilding it). Before the reset step landed, a second run appended a second
/// writer to every output port — a source then fanned each item into a now-orphaned
/// channel and the topology was corrupted.
/// </summary>
public sealed class GraphRerunTests
{
    /// <summary>
    /// A re-enumerable source: each graph run re-reads <paramref name="values"/>
    /// from the start. The source pump is single-threaded, and RunAsync settles all
    /// pumps before returning, so the per-run index needs no synchronization.
    /// </summary>
    private static SourceNode<RefBox<int>> RepeatableEmit(params int[] values)
    {
        int i = 0;
        return new SourceNode<RefBox<int>>(
            "src",
            _ =>
            {
                if (i >= values.Length)
                {
                    i = 0; // re-arm for the next run
                    return ValueTask.FromResult<RefBox<int>?>(null);
                }
                return ValueTask.FromResult<RefBox<int>?>(RefBox.Of(values[i++]));
            }
        );
    }

    /// <summary>A sink that records the values it consumed, then disposes each item.</summary>
    private static SinkNode<RefBox<int>> RecordingSink(List<int> sink) =>
        new(
            "sink",
            (item, _) =>
            {
                lock (sink)
                    sink.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

    /// <summary>An identity passthrough operator (the shape of PaceUntil / the gate).</summary>
    private static OperatorNode<RefBox<int>, RefBox<int>> Passthrough(string id) =>
        new(id, (item, _) => ValueTask.FromResult<RefBox<int>?>(item));

    [Fact]
    public async Task RunAsync_CalledTwice_ReplaysTheFullSequenceEachRun()
    {
        var consumed = new List<int>();
        var src = RepeatableEmit(1, 2, 3);
        var sink = RecordingSink(consumed);

        var graph = new GraphRunner();
        graph.Connect(src.Output, sink.Input);

        await graph.RunAsync();
        var firstRun = consumed.ToArray();

        consumed.Clear();
        await graph.RunAsync();
        var secondRun = consumed.ToArray();

        Assert.Equal(new[] { 1, 2, 3 }, firstRun);
        // The second run must produce the SAME full sequence — not a doubled or
        // truncated one (the symptom of accumulated writers / stale readers).
        Assert.Equal(new[] { 1, 2, 3 }, secondRun);
    }

    [Fact]
    public async Task RunAsync_CalledTwice_WithIntermediateOperators_ReplaysCleanly()
    {
        // The real session topology is source -> operator -> operator -> sink (decode
        // source -> PaceUntil -> gate -> sink). An intermediate operator both reads an
        // input port AND writes an output port, so without the reset its output port
        // accumulates a stale writer across runs and the second run misroutes / hangs.
        // This is the case that actually faulted the RepeatMode.One integration tests;
        // a bounded timeout turns a regression into a failure rather than a hang.
        var consumed = new List<int>();
        var src = RepeatableEmit(5, 6, 7, 8);
        var op1 = Passthrough("op1");
        var op2 = Passthrough("op2");
        var sink = RecordingSink(consumed);

        var graph = new GraphRunner();
        graph.Pipeline(src.Output).Then(op1).Then(op2).To(sink);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await graph.RunAsync(cts.Token);
        Assert.Equal(new[] { 5, 6, 7, 8 }, consumed.ToArray());

        consumed.Clear();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await graph.RunAsync(cts.Token);
        // Same full sequence on the re-run through the intermediate operators.
        Assert.Equal(new[] { 5, 6, 7, 8 }, consumed.ToArray());
    }

    [Fact]
    public async Task RunAsync_CalledTwice_DoesNotAccumulateWriters_ForFanOut()
    {
        // Fan-out is the case the per-edge reset has to get right: two edges share
        // one output port. After a re-run each downstream must receive exactly the
        // sequence once — if writers accumulated, a branch would see duplicates or
        // the source would write into a dead channel and the run would fault.
        var a = new List<int>();
        var b = new List<int>();
        var src = RepeatableEmit(10, 20);
        var sinkA = RecordingSink(a);
        var sinkB = new SinkNode<RefBox<int>>(
            "b",
            (item, _) =>
            {
                lock (b)
                    b.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

        var graph = new GraphRunner();
        graph.Connect(src.Output, sinkA.Input); // inherits
        graph.Connect(src.Output, sinkB.Input); // AddRef sibling

        await graph.RunAsync();
        a.Clear();
        b.Clear();
        await graph.RunAsync();

        // Each branch sees the full sequence exactly once on the second run.
        Assert.Equal(new[] { 10, 20 }, a.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { 10, 20 }, b.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task RunAsync_CalledTwice_BalancesRefcountsOnBothRuns()
    {
        // Every produced RefBox must settle to RefCount 0 on each run — a re-run
        // that leaked a stale writer would strand refs.
        var produced = new List<RefBox<int>>();
        int i = 0;
        var src = new SourceNode<RefBox<int>>(
            "src",
            _ =>
            {
                if (i >= 2)
                {
                    i = 0;
                    return ValueTask.FromResult<RefBox<int>?>(null);
                }
                i++;
                var box = RefBox.Of(i);
                lock (produced)
                    produced.Add(box);
                return ValueTask.FromResult<RefBox<int>?>(box);
            }
        );
        var sink = new SinkNode<RefBox<int>>("sink", (_, _) => ValueTask.CompletedTask);

        var graph = new GraphRunner();
        graph.Connect(src.Output, sink.Input);

        await graph.RunAsync();
        await graph.RunAsync();

        // 2 produced on each of the 2 runs = 4 boxes, all disposed to zero.
        Assert.Equal(4, produced.Count);
        Assert.All(produced, box => Assert.Equal(0, box.RefCount));
    }
}
