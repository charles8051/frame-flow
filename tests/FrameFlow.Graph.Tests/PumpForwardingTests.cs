using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// How a pump treats a body that returns its input (ADR-0080, decision 3). Each test barriers on
/// <see cref="Graph.RunAsync"/> completing, so every pump has settled before counts are read.
/// </summary>
public sealed class PumpForwardingTests
{
    [Fact]
    public async Task JoinFedTheSameItemOnBothInputs_ForwardingIt_EndsBalanced()
    {
        // A fork gives the join's primary and secondary the same instance. A gate on the
        // primary path waits until the join has retained it, so the primary matches itself.
        // The body forwards the primary, which is also the match: one of the item's two refs
        // goes downstream and the other must be released.
        var item = RefBox.Of(0);
        int pulls = 0;
        var source = new SourceNode<RefBox<int>>(
            "source",
            _ => ValueTask.FromResult<RefBox<int>?>(pulls++ == 0 ? item : null)
        );

        var join = new SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>>(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(10)
        );

        var gate = new OperatorNode<RefBox<int>, RefBox<int>>(
            "gate",
            async (input, ct) =>
            {
                while (join.RetainedCount < 1)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                return input;
            }
        );

        int delivered = 0;
        var graph = new Graph();
        var head = graph.Pipeline(source);
        head.Branch(EdgeOptions.Buffered(4)).ToSecondary(join);
        head.Then(gate).ToPrimary(join);
        graph.Pipeline(join.Output).To(new SinkNode<RefBox<int>>("sink", (_, _) =>
        {
            delivered++;
            return ValueTask.CompletedTask;
        }));

        await graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, delivered);
        Assert.Equal(0, item.RefCount);
    }

    [Fact]
    public async Task OperatorReturningItsInput_EndsBalanced()
    {
        var items = new[] { RefBox.Of(1), RefBox.Of(2) };
        int next = 0;
        var source = new SourceNode<RefBox<int>>(
            "source",
            _ => ValueTask.FromResult<RefBox<int>?>(next < items.Length ? items[next++] : null)
        );
        var passThrough = new OperatorNode<RefBox<int>, RefBox<int>>(
            "pass",
            (input, _) => ValueTask.FromResult<RefBox<int>?>(input)
        );

        var graph = new Graph();
        graph.Pipeline(source).Then(passThrough).To(new SinkNode<RefBox<int>>(
            "sink",
            (_, _) => ValueTask.CompletedTask
        ));

        await graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(items, i => Assert.Equal(0, i.RefCount));
    }
}
