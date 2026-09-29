using Xunit;

using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// The <c>cleanup</c> hook on <see cref="OperatorNode{TIn, TOut}"/> and
/// <see cref="MultiOperatorNode{TIn, TOut}"/>: once each time the node's pump exits, after its last
/// item, whatever ended the run (#47).
/// </summary>
public sealed class NodeCleanupTests
{
    [Fact]
    public async Task AnOperatorsCleanup_RunsOnceAfterItsLastItem()
    {
        var events = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Source(1, 2)).Then(Operator(events)).To(Discard());

        await graph.RunAsync();

        Assert.Equal(["body 1", "body 2", "cleanup"], events);
    }

    [Fact]
    public async Task AMultiOperatorsCleanup_RunsOnceAfterItsLastItem()
    {
        var events = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Source(1, 2)).Then(MultiOperator(events)).To(Discard());

        await graph.RunAsync();

        Assert.Equal(["body 1", "body 2", "cleanup"], events);
    }

    [Fact]
    public async Task ACleanup_RunsWhenTheBodyFaults()
    {
        var events = new List<string>();
        var op = new OperatorNode<RefBox<int>, RefBox<int>>(
            "op",
            (item, _) =>
            {
                events.Add($"body {item.Value}");
                return item.Value == 2
                    ? throw new InvalidOperationException("boom")
                    : ValueTask.FromResult<RefBox<int>?>(item);
            },
            cleanup: () =>
            {
                events.Add("cleanup");
                return ValueTask.CompletedTask;
            }
        );
        var graph = new GraphRunner();
        graph.Pipeline(Source(1, 2, 3)).Then(op).To(Discard());

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => graph.RunAsync());

        Assert.Equal(["body 1", "body 2", "cleanup"], events);
    }

    [Fact]
    public async Task ACleanup_RunsOncePerRun()
    {
        var events = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Source(1)).Then(Operator(events)).To(Discard());

        await graph.RunAsync();
        await graph.RunAsync();

        Assert.Equal(["body 1", "cleanup", "body 1", "cleanup"], events);
    }

    [Fact]
    public async Task AThrowingCleanup_DoesNotFailACleanRun()
    {
        var sunk = new List<int>();
        var op = new OperatorNode<RefBox<int>, RefBox<int>>(
            "op",
            (item, _) => ValueTask.FromResult<RefBox<int>?>(item),
            cleanup: () => throw new InvalidOperationException("cleanup failed")
        );
        var graph = new GraphRunner();
        graph.Pipeline(Source(1, 2))
            .Then(op)
            .To(
                new SinkNode<RefBox<int>>(
                    "sink",
                    (item, _) =>
                    {
                        sunk.Add(item.Value);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        await graph.RunAsync();

        Assert.Equal([1, 2], sunk);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    // Emits the values on every run: its own cleanup rewinds it.
    private static SourceNode<RefBox<int>> Source(params int[] values)
    {
        int next = 0;
        return new SourceNode<RefBox<int>>(
            "source",
            _ => ValueTask.FromResult(next < values.Length ? RefBox.Of(values[next++]) : null),
            cleanup: () =>
            {
                next = 0;
                return ValueTask.CompletedTask;
            }
        );
    }

    private static OperatorNode<RefBox<int>, RefBox<int>> Operator(List<string> events) =>
        new(
            "op",
            (item, _) =>
            {
                events.Add($"body {item.Value}");
                return ValueTask.FromResult<RefBox<int>?>(item);
            },
            cleanup: () =>
            {
                events.Add("cleanup");
                return ValueTask.CompletedTask;
            }
        );

    private static MultiOperatorNode<RefBox<int>, RefBox<int>> MultiOperator(List<string> events) =>
        new(
            "multi",
            (item, _) =>
            {
                events.Add($"body {item.Value}");
                return Once(item);
            },
            cleanup: () =>
            {
                events.Add("cleanup");
                return ValueTask.CompletedTask;
            }
        );

    private static async IAsyncEnumerable<RefBox<int>> Once(RefBox<int> item)
    {
        await Task.CompletedTask;
        item.AddRef();
        yield return item;
    }

    private static SinkNode<RefBox<int>> Discard() => new("sink", (_, _) => ValueTask.CompletedTask);
}
