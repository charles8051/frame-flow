using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type (FrameFlow.Graph.Graph).
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// An operator that keeps state between items is told when the run it is about to see starts
/// over (#217). A graph is re-runnable, and a loop rewinds its item and runs the same graph
/// again, so the operator's closure outlives the loop while its timestamps go back to zero.
/// </summary>
public sealed class OperatorResetTests
{
    private static SourceNode<RefBox<int>> RepeatableEmit(params int[] values)
    {
        var i = 0;
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

    [Fact]
    public async Task OnReset_RunsBeforeEachRun_IncludingTheFirst()
    {
        var resets = 0;
        var src = RepeatableEmit(1);
        var passthrough = new OperatorNode<RefBox<int>, RefBox<int>>(
            "op",
            (item, _) => ValueTask.FromResult<RefBox<int>?>(item)
        )
        {
            OnReset = () => resets++,
        };
        var sink = RecordingSink([]);

        var graph = new GraphRunner();
        graph.Connect(src.Output, passthrough.Input);
        graph.Connect(passthrough.Output, sink.Input);

        await graph.RunAsync();
        Assert.Equal(1, resets);

        await graph.RunAsync();
        Assert.Equal(2, resets);
    }

    [Fact]
    public async Task AnOperatorThatRemembersTheLastValue_StartsOverOnTheSecondRun()
    {
        // The shape the issue describes: a body that carries a value from one item to the next,
        // and would otherwise carry the last item of one run into the first of the next. Here it
        // emits the difference from the previous value, which is the whole value at a start.
        var deltas = new List<int>();
        var src = RepeatableEmit(10, 14);
        var previous = 0;
        var differ = new OperatorNode<RefBox<int>, RefBox<int>>(
            "difference",
            (item, _) =>
            {
                var delta = item.Value - previous;
                previous = item.Value;
                return ValueTask.FromResult<RefBox<int>?>(RefBox.Of(delta));
            }
        )
        {
            OnReset = () => previous = 0,
        };
        var sink = RecordingSink(deltas);

        var graph = new GraphRunner();
        graph.Connect(src.Output, differ.Input);
        graph.Connect(differ.Output, sink.Input);

        await graph.RunAsync();
        await graph.RunAsync();

        // Without the reset the second run's first delta would be -4, the step back to 10.
        Assert.Equal([10, 4, 10, 4], deltas);
    }

    [Fact]
    public async Task AnOperatorWithNoReset_KeepsItsState()
    {
        // The hook is opt-in. An operator that wants its state across runs says nothing.
        var seen = 0;
        var src = RepeatableEmit(1, 2);
        var counter = new OperatorNode<RefBox<int>, RefBox<int>>(
            "count",
            (item, _) =>
            {
                seen++;
                return ValueTask.FromResult<RefBox<int>?>(item);
            }
        );
        var sink = RecordingSink([]);

        var graph = new GraphRunner();
        graph.Connect(src.Output, counter.Input);
        graph.Connect(counter.Output, sink.Input);

        await graph.RunAsync();
        await graph.RunAsync();

        Assert.Equal(4, seen);
    }
}
