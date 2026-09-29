using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type
// (FrameFlow.Graph.Graph). Alias the type so the tests can sit in the
// conventional FrameFlow.Graph.Tests namespace without the clash.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// Behaviour of <see cref="FailureResponse.Discard"/> on an
/// <see cref="OperatorNode{TIn, TOut}"/>: when the operator body throws,
/// the substrate disposes the offending input and the node continues with
/// the next item rather than faulting the graph. Survivors reach the sink;
/// every flowing <see cref="RefBox{T}"/> settles to a zero refcount.
/// </summary>
/// <remarks>
/// Each pump settles before <see cref="GraphRunner.RunAsync"/> returns, so
/// asserting on the recorded values and <see cref="RefBox{T}.RefCount"/>
/// immediately afterward observes the fully-settled state — no polling.
/// </remarks>
public sealed class FailureResponseDiscardTests
{
    /// <summary>Sentinel thrown by the operator on the poison value; keeps the path unambiguous.</summary>
    private sealed class BoomException : Exception { }

    /// <summary>
    /// A source that emits <paramref name="values"/> in order — one boxed
    /// item per pull, then EOS. Every box is recorded so the test can assert
    /// each one was disposed exactly to zero. The source pump is
    /// single-threaded, so the plain index needs no synchronization.
    /// </summary>
    private static SourceNode<RefBox<int>> Emit(List<RefBox<int>> produced, params int[] values)
    {
        int i = 0;
        return new SourceNode<RefBox<int>>(
            "src",
            _ =>
            {
                if (i >= values.Length)
                    return ValueTask.FromResult<RefBox<int>?>(null);
                var box = RefBox.Of(values[i++]);
                produced.Add(box);
                return ValueTask.FromResult<RefBox<int>?>(box);
            }
        );
    }

    /// <summary>A sink that records the values it consumed, then disposes each item.</summary>
    private static SinkNode<RefBox<int>> RecordingSink(List<int> consumed) =>
        new(
            "sink",
            (item, _) =>
            {
                lock (consumed)
                    consumed.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

    [Fact]
    public async Task OperatorThrows_Discard_DisposesFailingInputAndContinues()
    {
        // The operator throws on the poison value (2) and passes everything
        // else through. With FailureResponse.Discard the graph must NOT fault:
        // the poison input is disposed, the node continues, and the survivors
        // (1 and 3) land at the sink in order.
        var produced = new List<RefBox<int>>();
        var consumed = new List<int>();

        var src = Emit(produced, 1, 2, 3);
        var op = new OperatorNode<RefBox<int>, RefBox<int>>(
            "op",
            (item, _) =>
            {
                if (item.Value == 2)
                    throw new BoomException();
                return ValueTask.FromResult<RefBox<int>?>(item);
            },
            FailureResponse.Discard
        );
        var sink = RecordingSink(consumed);

        var graph = new GraphRunner();
        graph.Pipeline(src.Output).Then(op).To(sink);

        // Discard swallows the operator fault — RunAsync completes normally.
        await graph.RunAsync();

        // Survivors only; the poison item (2) was discarded, not forwarded.
        Assert.Equal(new[] { 1, 3 }, consumed.ToArray());

        // Every produced box settles to zero: the two survivors via the sink,
        // the discarded one via the substrate's dispose-on-failure.
        Assert.Equal(3, produced.Count);
        Assert.All(produced, box => Assert.Equal(0, box.RefCount));
    }

    // ── Counting (#501) ─────────────────────────────────────────────────

    /// <summary>Throws a <see cref="CountedException"/> carrying the value on every even one.</summary>
    private static OperatorNode<RefBox<int>, RefBox<int>> FailingOnEven(
        string id,
        Action<int>? onCall = null
    ) =>
        new(
            id,
            (item, _) =>
            {
                onCall?.Invoke(item.Value);
                if (item.Value % 2 == 0)
                    throw new CountedException(item.Value);
                return ValueTask.FromResult<RefBox<int>?>(item);
            },
            FailureResponse.Discard
        );

    private sealed class CountedException(int value) : Exception($"failed on {value}")
    {
        public int Value { get; } = value;
    }

    [Fact]
    public async Task Discards_AreCountedPerNode_WithTheLastException()
    {
        var graph = new GraphRunner();
        graph.Pipeline(Emit([], 1, 2, 3, 4, 5)).Then(FailingOnEven("op")).To(RecordingSink([]));

        Assert.Empty(graph.Discards);
        await graph.RunAsync();

        var discards = Assert.Single(graph.Discards);
        Assert.Equal("op", discards.NodeId);
        Assert.Equal(2, discards.Count);
        Assert.Equal(4, Assert.IsType<CountedException>(discards.LastException).Value);
    }

    [Fact]
    public async Task Discards_AreReadableWhileTheGraphRuns()
    {
        // The body reads the graph's count on 3, after its own pump discarded 2 and before it
        // discards 4, so the read sees exactly one.
        var graph = new GraphRunner();
        long seenOnThree = -1;
        var op = FailingOnEven(
            "op",
            value =>
            {
                if (value == 3)
                    seenOnThree = graph.Discards.Single().Count;
            }
        );
        graph.Pipeline(Emit([], 1, 2, 3, 4)).Then(op).To(RecordingSink([]));

        await graph.RunAsync();

        Assert.Equal(1, seenOnThree);
        Assert.Equal(2, graph.Discards.Single().Count);
    }

    [Fact]
    public async Task Discards_AccumulateAcrossRuns()
    {
        var graph = new GraphRunner();
        var values = new[] { 1, 2, 3, 4 };
        int next = 0;
        graph.BeforeEachRun(() => next = 0);
        var source = new SourceNode<RefBox<int>>(
            "src",
            _ => ValueTask.FromResult(next < values.Length ? RefBox.Of(values[next++]) : null)
        );
        graph.Pipeline(source).Then(FailingOnEven("op")).To(RecordingSink([]));

        await graph.RunAsync();
        await graph.RunAsync();

        Assert.Equal(4, graph.Discards.Single().Count);
    }

    [Fact]
    public async Task Discards_ArePublishedOnTheGraphMeter_TaggedByNode()
    {
        // Other tests discard in parallel on the same process-wide instrument, so the node id is
        // unique to this test and only its measurements are summed.
        var id = "op-" + Guid.NewGuid().ToString("N");
        long published = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "FrameFlow.Graph" && instrument.Name == "frameflow.graph.discards")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "node" && Equals(tag.Value, id))
                        Interlocked.Add(ref published, measurement);
                }
            }
        );
        listener.Start();

        var graph = new GraphRunner();
        graph.Pipeline(Emit([], 1, 2, 3, 4, 5, 6)).Then(FailingOnEven(id)).To(RecordingSink([]));
        await graph.RunAsync();

        Assert.Equal(3, Interlocked.Read(ref published));
    }

    [Fact]
    public async Task AFaultUnderPropagate_IsNotADiscard()
    {
        var graph = new GraphRunner();
        var op = new OperatorNode<RefBox<int>, RefBox<int>>(
            "op",
            (_, _) => throw new BoomException()
        );
        graph.Pipeline(Emit([], 1)).Then(op).To(RecordingSink([]));

        await Assert.ThrowsAsync<GraphFaultException>(() => graph.RunAsync());

        Assert.Empty(graph.Discards);
    }

    [Fact]
    public async Task AJoinsSecondaryKeySelector_CountsItsDiscards()
    {
        // The secondary loop runs on its own task; RunAsync waits for it, so the count is final
        // once the run returns.
        var join = new SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>>(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => s.Value == 20
                    ? throw new CountedException(s.Value)
                    : (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            TimeSpan.FromSeconds(10),
            onError: FailureResponse.Discard
        );
        var produced = new List<RefBox<int>>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit(produced, "secondary", 10, 20, 30)).ToSecondary(join, EdgeOptions.Buffered(4));
        graph.Pipeline(Emit(produced, "primary", 1)).ToPrimary(join);
        graph.Pipeline(join.Output).To(RecordingSink([]));

        await graph.RunAsync();

        var discards = Assert.Single(graph.Discards);
        Assert.Equal("join", discards.NodeId);
        Assert.Equal(1, discards.Count);
        Assert.Equal(20, Assert.IsType<CountedException>(discards.LastException).Value);
        Assert.All(produced, box => Assert.Equal(0, box.RefCount));
    }

    private static SourceNode<RefBox<int>> Emit(
        List<RefBox<int>> produced,
        string id,
        params int[] values
    )
    {
        int i = 0;
        return new SourceNode<RefBox<int>>(
            id,
            _ =>
            {
                if (i >= values.Length)
                    return ValueTask.FromResult<RefBox<int>?>(null);
                var box = RefBox.Of(values[i++]);
                lock (produced)
                    produced.Add(box);
                return ValueTask.FromResult<RefBox<int>?>(box);
            }
        );
    }
}
