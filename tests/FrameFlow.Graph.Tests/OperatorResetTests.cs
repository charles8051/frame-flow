using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type (FrameFlow.Graph.Graph).
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// State that outlives a run is dropped before the next one (#217). A graph is re-runnable, and a
/// loop rewinds its item and runs the same graph again, so an operator's closure outlives the loop
/// while its timestamps go back to zero. <see cref="GraphRunner.BeforeEachRun"/> is where a caller
/// clears it.
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
    public async Task BeforeEachRun_RunsBeforeEachRun_IncludingTheFirst()
    {
        var resets = 0;
        var src = RepeatableEmit(1);
        var passthrough = new OperatorNode<RefBox<int>, RefBox<int>>(
            "op",
            (item, _) => ValueTask.FromResult<RefBox<int>?>(item)
        );
        var sink = RecordingSink([]);

        var graph = new GraphRunner();
        graph.BeforeEachRun(() => resets++);
        graph.Connect(src.Output, passthrough.Input);
        graph.Connect(passthrough.Output, sink.Input);

        await graph.RunAsync();
        Assert.Equal(1, resets);

        await graph.RunAsync();
        Assert.Equal(2, resets);
    }

    [Fact]
    public async Task BeforeEachRun_ActionsRunInTheOrderTheyWereRegistered()
    {
        var order = new List<string>();
        var src = RepeatableEmit(1);
        var sink = RecordingSink([]);

        var graph = new GraphRunner();
        graph.BeforeEachRun(() => order.Add("first")).BeforeEachRun(() => order.Add("second"));
        graph.Connect(src.Output, sink.Input);

        await graph.RunAsync();

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task RunAsync_WhileARunIsInFlight_IsRefused()
    {
        // A second run would drop the state the first is using and rewire its ports underneath it.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = new GraphRunner();
        var src = new SourceNode<RefBox<int>>(
            "src",
            async _ =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return null;
            }
        );
        graph.Connect(src.Output, RecordingSink([]).Input);

        var first = graph.RunAsync();
        await started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => graph.RunAsync());

        release.SetResult();
        await first;

        // And the graph runs again once the first has settled.
        await graph.RunAsync();
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
        );
        var sink = RecordingSink(deltas);

        var graph = new GraphRunner();
        graph.BeforeEachRun(() => previous = 0);
        graph.Connect(src.Output, differ.Input);
        graph.Connect(differ.Output, sink.Input);

        await graph.RunAsync();
        await graph.RunAsync();

        // Without the reset the second run's first delta would be -4, the step back to 10.
        Assert.Equal([10, 4, 10, 4], deltas);
    }

    [Fact]
    public async Task AnOperatorWithNothingRegistered_KeepsItsState()
    {
        // Opt-in: an operator that wants its state across runs registers nothing.
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
