using System.Runtime.CompilerServices;
using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type
// (FrameFlow.Graph.Graph). Alias the type so the tests can sit in the
// conventional FrameFlow.Graph.Tests namespace without the clash.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// An <see cref="OperationCanceledException"/> from a node body is a cancellation only when the
/// graph was cancelled. Any other one is the body failing, and takes the node's
/// <see cref="FailureResponse"/> like any other exception (#489).
/// </summary>
/// <remarks>
/// Every case emits 1, 2, 3 and has one body throw a <see cref="TaskCanceledException"/> of its own
/// on 2, the way an <c>HttpClient</c> timeout does, with no caller token. Each pump settles before
/// <see cref="GraphRunner.RunAsync"/> returns, so the assertions after it see the settled state.
/// </remarks>
public sealed class BodyCancellationTests
{
    /// <summary>The kind of node whose body throws.</summary>
    public enum Site
    {
        Source,
        Operator,
        MultiOperator,
        Sink,
        Join,
    }

    public static TheoryData<Site> Sites =>
        [Site.Source, Site.Operator, Site.MultiOperator, Site.Sink, Site.Join];

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task AStrayCancellation_UnderPropagate_FaultsTheRun(Site site)
    {
        var run = new Run(site, FailureResponse.Propagate);

        await Assert.ThrowsAsync<TaskCanceledException>(() => run.Graph.RunAsync());

        Assert.All(run.Produced, box => Assert.Equal(0, box.RefCount));
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task AStrayCancellation_UnderDiscard_DropsOnlyThatItem(Site site)
    {
        var run = new Run(site, FailureResponse.Discard);

        await run.Graph.RunAsync();

        Assert.Equal([1, 3], run.Consumed);
        Assert.All(run.Produced, box => Assert.Equal(0, box.RefCount));
    }

    [Fact]
    public async Task ACallerCancellation_UnderDiscard_StillEndsTheRun()
    {
        // The body is waiting on the graph token when the caller cancels. Its exception is the
        // graph's cancellation, so Discard must not swallow it and read the next item.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var produced = new List<RefBox<int>>();
        var op = new OperatorNode<RefBox<int>, RefBox<int>>(
            "op",
            async (item, ct) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await new TaskCompletionSource().Task.WaitAsync(ct).ConfigureAwait(false);
                return item;
            },
            FailureResponse.Discard
        );
        var graph = new GraphRunner();
        graph.Pipeline(Emit(produced, 1, 2, 3)).Then(op).To(Record(new List<int>()));

        using var cts = new CancellationTokenSource();
        var running = graph.RunAsync(cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(15))
        );
        Assert.Equal(1, calls);
        Assert.All(produced, box => Assert.Equal(0, box.RefCount));
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException), true, true)]
    [InlineData(typeof(OperationCanceledException), false, false)]
    [InlineData(typeof(TaskCanceledException), true, true)]
    [InlineData(typeof(TaskCanceledException), false, false)]
    [InlineData(typeof(InvalidOperationException), true, false)]
    [InlineData(typeof(InvalidOperationException), false, false)]
    public void IsCancellation_OnlyForACancellationWhileTheRunIsCancelled(
        Type exceptionType,
        bool runCancelled,
        bool expected
    )
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(expected, FaultRules.IsCancellation(ex, runCancelled));
    }

    // ── Graphs ──────────────────────────────────────────────────────────

    /// <summary>One graph per <see cref="Site"/>, with the throwing body at that site.</summary>
    private sealed class Run
    {
        public GraphRunner Graph { get; } = new();

        /// <summary>Every box the graph created, so each can be checked for release.</summary>
        public List<RefBox<int>> Produced { get; } = [];

        /// <summary>The values the recording sink took, in order.</summary>
        public List<int> Consumed { get; } = [];

        public Run(Site site, FailureResponse policy)
        {
            switch (site)
            {
                case Site.Source:
                    Graph.Pipeline(ThrowingSource(policy)).To(Record(Consumed));
                    break;

                case Site.Operator:
                    Graph
                        .Pipeline(Emit(Produced, 1, 2, 3))
                        .Then(
                            new OperatorNode<RefBox<int>, RefBox<int>>(
                                "op",
                                (item, _) =>
                                    item.Value == 2
                                        ? throw new TaskCanceledException()
                                        : ValueTask.FromResult<RefBox<int>?>(item),
                                policy
                            )
                        )
                        .To(Record(Consumed));
                    break;

                case Site.MultiOperator:
                    Graph
                        .Pipeline(Emit(Produced, 1, 2, 3))
                        .Then(
                            new MultiOperatorNode<RefBox<int>, RefBox<int>>(
                                "multi",
                                (item, ct) => Expand(item.Value, ct),
                                policy
                            )
                        )
                        .To(Record(Consumed));
                    break;

                case Site.Sink:
                    Graph
                        .Pipeline(Emit(Produced, 1, 2, 3))
                        .To(
                            new SinkNode<RefBox<int>>(
                                "sink",
                                (item, _) =>
                                {
                                    if (item.Value == 2)
                                        throw new TaskCanceledException();
                                    lock (Consumed)
                                        Consumed.Add(item.Value);
                                    return ValueTask.CompletedTask;
                                },
                                policy
                            )
                        );
                    break;

                case Site.Join:
                    var join = new SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>>(
                        "join",
                        (primary, _, _) =>
                            primary.Value == 2
                                ? throw new TaskCanceledException()
                                : ValueTask.FromResult<RefBox<int>?>(primary),
                        new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                            p => TimeSpan.FromMilliseconds(p.Value),
                            s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
                        ),
                        SyncMatch.MostRecentAtOrBefore,
                        TimeSpan.FromSeconds(10),
                        onError: policy
                    );
                    Graph.Pipeline(Emit(Produced, id: "secondary")).ToSecondary(join);
                    Graph.Pipeline(Emit(Produced, 1, 2, 3)).ToPrimary(join);
                    Graph.Pipeline(join.Output).To(Record(Consumed));
                    break;
            }
        }

        // Emits 1, throws on the second pull, emits 3 on the third, then ends.
        private SourceNode<RefBox<int>> ThrowingSource(FailureResponse policy)
        {
            int pull = 0;
            return new SourceNode<RefBox<int>>(
                "source",
                _ =>
                {
                    switch (++pull)
                    {
                        case 1:
                        case 3:
                            var box = RefBox.Of(pull);
                            Produced.Add(box);
                            return ValueTask.FromResult<RefBox<int>?>(box);
                        case 2:
                            throw new TaskCanceledException();
                        default:
                            return ValueTask.FromResult<RefBox<int>?>(null);
                    }
                },
                policy
            );
        }

        private async IAsyncEnumerable<RefBox<int>> Expand(
            int value,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            await Task.Yield();
            if (value == 2)
                throw new TaskCanceledException();
            var box = RefBox.Of(value);
            lock (Produced)
                Produced.Add(box);
            yield return box;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>A source that emits <paramref name="values"/> in order, recording each box.</summary>
    private static SourceNode<RefBox<int>> Emit(
        List<RefBox<int>> produced,
        params int[] values
    ) => Emit(produced, "source", values);

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

    /// <summary>A sink that records the values it takes.</summary>
    private static SinkNode<RefBox<int>> Record(List<int> consumed) =>
        new(
            "sink",
            (item, _) =>
            {
                lock (consumed)
                    consumed.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );
}
