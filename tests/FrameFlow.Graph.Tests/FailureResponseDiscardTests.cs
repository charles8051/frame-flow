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
}
