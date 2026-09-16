using Xunit;

using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// A fork and a rejoin expressed as a chain: who takes the incoming ref, and what the graph
/// refuses.
/// </summary>
/// <remarks>
/// <para>
/// The ownership question is the point. <c>ForwardAsync</c> hands the incoming ref to one edge
/// and gives the rest a clone or an <c>AddRef</c>, and before <c>Branch</c> the recipient was
/// whichever cloner-less edge happened to be wired first. <c>Branch</c> wires the branch before
/// the trunk, so the scan alone would hand a one-shot item's ref to the branch and throw on its
/// <c>AddRef</c>. These tests pin that the trunk keeps it.
/// </para>
/// <para>
/// <see cref="OneShot"/> is the type that makes the difference observable: its <c>AddRef</c>
/// throws, exactly as <c>Media.CpuVideoFrame</c>'s does.
/// </para>
/// </remarks>
public sealed class GraphChainForkTests
{
    [Fact]
    public async Task TheTrunkTakesTheIncomingRef_EvenThoughTheBranchIsWiredFirst()
    {
        var graph = new GraphRunner();
        var source = OneShotSource(3);
        var trunkSeen = new List<int>();
        var branchSeen = new List<int>();

        var head = graph.Pipeline(source);
        var branch = head.Branch(
            EdgeOptions.Buffered(4).WithCloner<OneShot>(item => new OneShot(item.Value))
        );
        branch.To(Collect(branchSeen, "branch"));
        head.To(Collect(trunkSeen, "trunk"));

        await graph.RunAsync(CancellationToken.None);

        // Both consumers saw every item, and nothing threw: the trunk inherited, so no AddRef
        // was ever attempted on a one-shot item.
        Assert.Equal([1, 2, 3], trunkSeen);
        Assert.Equal([1, 2, 3], branchSeen);
    }

    [Fact]
    public async Task ACloserlessBranch_StillLeavesTheOriginalItemWithTheTrunk()
    {
        // The case the marker exists for. An AddRef-able item lets a branch be wired without a
        // cloner, and Branch wires it before the trunk, so the first-cloner-less scan alone would
        // give the branch the incoming item and hand the trunk a fresh ref. Nothing throws either
        // way; what changes is which consumer holds the original.
        var graph = new GraphRunner();
        var source = TaggedSource(3);
        var trunkOriginals = 0;
        var branchOriginals = 0;

        var head = graph.Pipeline(source);
        head.Branch(EdgeOptions.Buffered(4))
            .To(
                new SinkNode<Tagged>(
                    "branch",
                    (item, _) =>
                    {
                        if (item.IsOriginal)
                            branchOriginals++;
                        return ValueTask.CompletedTask;
                    }
                )
            );
        head.To(
            new SinkNode<Tagged>(
                "trunk",
                (item, _) =>
                {
                    if (item.IsOriginal)
                        trunkOriginals++;
                    return ValueTask.CompletedTask;
                }
            )
        );

        await graph.RunAsync(CancellationToken.None);

        Assert.Equal(3, trunkOriginals);
        Assert.Equal(0, branchOriginals);
    }

    [Fact]
    public async Task WithoutBranch_TheSameWiringHandsTheRefToTheFirstEdge()
    {
        // The control for the test above. Wired through Connect, the first cloner-less edge
        // inherits, which is ADR-0054's rule and is unchanged by this feature.
        var graph = new GraphRunner();
        var source = OneShotSource(2);
        var first = new List<int>();
        var second = new List<int>();

        var trunk = Collect(first, "first");
        var other = Collect(second, "second");
        graph.Connect(source.Output, trunk.Input);
        graph.Connect(
            source.Output,
            other.Input,
            EdgeOptions.Buffered(4).WithCloner<OneShot>(item => new OneShot(item.Value))
        );

        await graph.RunAsync(CancellationToken.None);

        Assert.Equal([1, 2], first);
        Assert.Equal([1, 2], second);
    }

    [Fact]
    public async Task AForkOfBranchesOnly_HasNoTrunkAndStillRuns()
    {
        var graph = new GraphRunner();
        var source = OneShotSource(2);
        var left = new List<int>();
        var right = new List<int>();

        var head = graph.Pipeline(source);
        head.Branch(EdgeOptions.Buffered(4).WithCloner<OneShot>(i => new OneShot(i.Value)))
            .To(Collect(left, "left"));
        head.Branch(EdgeOptions.Buffered(4).WithCloner<OneShot>(i => new OneShot(i.Value)))
            .To(Collect(right, "right"));

        await graph.RunAsync(CancellationToken.None);

        Assert.Equal([1, 2], left);
        Assert.Equal([1, 2], right);
    }

    [Fact]
    public async Task TwoTrunksOnOnePort_AreRefused()
    {
        var graph = new GraphRunner();
        var source = OneShotSource(1);
        var head = graph.Pipeline(source);

        head.Branch(EdgeOptions.Buffered(4).WithCloner<OneShot>(i => new OneShot(i.Value)))
            .To(Collect([], "branch"));
        head.To(Collect([], "trunk-one"));
        head.To(Collect([], "trunk-two"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None)
        );

        Assert.Contains("2 trunk edges", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJoinBuiltFromTheChain_PairsTheBranchBackOntoTheTrunk()
    {
        var graph = new GraphRunner();
        var source = CountedSource(3);
        var joined = new List<int>();

        var head = graph.Pipeline(source);
        var detections = head.Branch(
                EdgeOptions.LatestWins(1).WithCloner<RefBox<int>>(i => (RefBox<int>)i.AddRef())
            )
            .Then(Double("double"));

        head.Join(detections, PairingJoin(), EdgeOptions.Default, EdgeOptions.Buffered(4))
            .To(
                new SinkNode<RefBox<int>>(
                    "sink",
                    (item, _) =>
                    {
                        joined.Add(item.Value);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        await graph.RunAsync(CancellationToken.None);

        // Every primary fires the join once, which is the shape LiveCaptioning's detection
        // overlay wires by hand today.
        Assert.Equal(3, joined.Count);
    }

    [Fact]
    public void AJoinAcrossTwoGraphs_IsRejectedBeforeEitherEdgeIsWired()
    {
        var graphA = new GraphRunner();
        var graphB = new GraphRunner();
        var primary = graphA.Pipeline(CountedSource(1));
        var secondary = graphB.Pipeline(CountedSource(1));
        var join = PairingJoin();

        var ex = Assert.Throws<ArgumentException>(
            () => primary.Join(secondary, join, EdgeOptions.Default, EdgeOptions.Buffered(4))
        );
        Assert.Contains("different graph", ex.Message, StringComparison.Ordinal);

        // Neither edge was wired, so the join is still free to be wired properly afterwards.
        // A half-wired join would fail this second call with "already connected".
        primary.Join(
            graphA.Pipeline(CountedSource(1)),
            join,
            EdgeOptions.Default,
            EdgeOptions.Buffered(4)
        );
    }

    [Fact]
    public void ABranchWithADefaultConfig_IsRejected()
    {
        // A default EdgeConfig carries no options, and Connect reads that as EdgeOptions.Default:
        // the capacity-1 blocking edge this overload exists to stop a caller getting by accident.
        var graph = new GraphRunner();
        var head = graph.Pipeline(CountedSource(1));

        var ex = Assert.Throws<ArgumentException>(() => head.Branch(default(EdgeConfig<RefBox<int>>)));
        Assert.Contains("explicit edge options", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinearChain_IsUnchanged()
    {
        var graph = new GraphRunner();
        var source = CountedSource(3);
        var seen = new List<int>();

        graph.Pipeline(source)
            .Then(Double("double"))
            .To(
                new SinkNode<RefBox<int>>(
                    "sink",
                    (item, _) =>
                    {
                        seen.Add(item.Value);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        await graph.RunAsync(CancellationToken.None);

        Assert.Equal([2, 4, 6], seen);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// An AddRef-able item that says whether it is the one the producer made. Its <c>AddRef</c>
    /// returns a fresh wrapper, the way <c>VideoFrameRef</c> does, so a consumer can tell an
    /// inherited item from a ref of one.
    /// </summary>
    private sealed class Tagged(int value, bool isOriginal) : IRefCounted
    {
        public int Value => value;
        public bool IsOriginal => isOriginal;

        public IRefCounted AddRef() => new Tagged(value, isOriginal: false);

        public void Dispose() { }
    }

    private static SourceNode<Tagged> TaggedSource(int count)
    {
        int next = 0;
        return new SourceNode<Tagged>(
            "source",
            _ => ValueTask.FromResult(next < count ? new Tagged(++next, isOriginal: true) : null)
        );
    }

    /// <summary>An item whose <c>AddRef</c> throws, like a converter's one-shot frame.</summary>
    private sealed class OneShot(int value) : IRefCounted
    {
        public int Value => value;

        public IRefCounted AddRef() =>
            throw new InvalidOperationException("one-shot item: AddRef is not supported");

        public void Dispose() { }
    }

    private static SourceNode<OneShot> OneShotSource(int count)
    {
        int next = 0;
        return new SourceNode<OneShot>(
            "source",
            _ => ValueTask.FromResult(next < count ? new OneShot(++next) : null)
        );
    }

    private static SourceNode<RefBox<int>> CountedSource(int count)
    {
        int next = 0;
        return new SourceNode<RefBox<int>>(
            "source",
            _ => ValueTask.FromResult(next < count ? RefBox.Of(++next) : null)
        );
    }

    private static SinkNode<OneShot> Collect(List<int> into, string id) =>
        new(
            id,
            (item, _) =>
            {
                into.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

    private static OperatorNode<RefBox<int>, RefBox<int>> Double(string id) =>
        new(id, (item, _) => ValueTask.FromResult<RefBox<int>?>(RefBox.Of(item.Value * 2)));

    private static SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>> PairingJoin() =>
        new(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(5)
        );
}
