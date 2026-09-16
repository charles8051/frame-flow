using Xunit;

using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// The graph refuses a fork that rejoins a lead-setting join along a path that can only wait.
/// </summary>
/// <remarks>
/// <para>
/// The deadlock this prevents is the one <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}"/>
/// warns about in prose: a producer made to wait must not also feed the primary. The join stops
/// reading the secondary once it leads, the full secondary edge blocks the branch, the blocked
/// branch stalls the fork, and the primary that would release the lead never arrives.
/// </para>
/// <para>
/// <c>Branch</c> makes that shape easy to build, which is why the check lands with it. Every
/// test here is a wiring assertion: nothing runs a graph to the deadlock, because a refused
/// topology throws before any pump starts.
/// </para>
/// </remarks>
public sealed class GraphCycleValidationTests
{
    [Fact]
    public async Task ABlockingForkRejoinUnderALead_IsRefused()
    {
        var graph = new GraphRunner();
        var head = graph.Pipeline(Source(3));
        var join = JoinWithLead(TimeSpan.FromMilliseconds(50));

        var branch = head.Branch(EdgeOptions.Buffered(4)).Then(Passthrough("detect"));
        head.Join(branch, join, EdgeOptions.Default, EdgeOptions.Buffered(4)).To(Sink());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None)
        );

        Assert.Contains("sets a lead", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dropping policy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATrunkThatPassesThroughAnOperator_IsStillTheSharedProducer()
    {
        // The port feeding the join's primary is the operator's output, not the fork. Searching
        // from there finds nothing, but the fork above it still feeds both sides, and it is the
        // one that stalls: its write to the blocked branch never completes, so the operator
        // never sees the item it would turn into the primary.
        var graph = new GraphRunner();
        var head = graph.Pipeline(Source(3));
        var join = JoinWithLead(TimeSpan.FromMilliseconds(50));

        var branch = head.Branch(EdgeOptions.Buffered(4)).Then(Passthrough("detect"));
        head.Then(Passthrough("convert"))
            .Join(branch, join, EdgeOptions.Default, EdgeOptions.Buffered(4))
            .To(Sink());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None)
        );

        Assert.Contains("feeds both of its inputs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADroppingBranchEdge_BreaksTheCycle()
    {
        // This is LiveCaptioning's shape: the inference branch drops rather than waits, so the
        // fork can always finish writing even while the join is holding the secondary back.
        var graph = new GraphRunner();
        var head = graph.Pipeline(Source(3));
        var join = JoinWithLead(TimeSpan.FromMilliseconds(50));

        var branch = head.Branch(EdgeOptions.LatestWins(1)).Then(Passthrough("detect"));
        head.Join(branch, join, EdgeOptions.Default, EdgeOptions.Buffered(4)).To(Sink());

        await graph.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ADroppingEdgeLaterOnTheBranch_AlsoBreaksIt()
    {
        // The rule is about the whole path, not just the first hop: a drop anywhere on it means
        // the fork's write can complete.
        var graph = new GraphRunner();
        var head = graph.Pipeline(Source(3));
        var join = JoinWithLead(TimeSpan.FromMilliseconds(50));

        var branch = head.Branch(EdgeOptions.Buffered(4))
            .Then(Passthrough("pre"))
            .Then(Passthrough("detect"), EdgeOptions.LatestWins(1));
        head.Join(branch, join, EdgeOptions.Default, EdgeOptions.Buffered(4)).To(Sink());

        await graph.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WithoutALead_TheSameBlockingForkRejoinIsFine()
    {
        // No lead means the join never stops reading the secondary, so the branch is never held
        // and the cycle has no fourth ingredient.
        var graph = new GraphRunner();
        var head = graph.Pipeline(Source(3));
        var join = JoinWithLead(null);

        var branch = head.Branch(EdgeOptions.Buffered(4)).Then(Passthrough("detect"));
        head.Join(branch, join, EdgeOptions.Default, EdgeOptions.Buffered(4)).To(Sink());

        await graph.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TwoSeparateProducers_AreNotACycle()
    {
        // A lead is only dangerous when one producer feeds both sides. Here the secondary has a
        // source of its own, so the join holding it back cannot stall the primary.
        var graph = new GraphRunner();
        var join = JoinWithLead(TimeSpan.FromMilliseconds(50));

        graph.Pipeline(Source(3)).ToPrimary(join);
        graph.Pipeline(Source(3)).ToSecondary(join, EdgeOptions.Buffered(4));
        graph.Pipeline(join.Output).To(Sink());

        await graph.RunAsync(CancellationToken.None);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static SourceNode<RefBox<int>> Source(int count)
    {
        int next = 0;
        return new SourceNode<RefBox<int>>(
            "source" + Guid.NewGuid().ToString("N")[..4],
            _ => ValueTask.FromResult(next < count ? RefBox.Of(++next) : null)
        );
    }

    private static OperatorNode<RefBox<int>, RefBox<int>> Passthrough(string id) =>
        new(id, (item, _) => ValueTask.FromResult<RefBox<int>?>(item));

    private static SinkNode<RefBox<int>> Sink() =>
        new("sink", (_, _) => ValueTask.CompletedTask);

    private static SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>> JoinWithLead(
        TimeSpan? maxLead
    ) =>
        new(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(5),
            maxLead: maxLead
        );
}
