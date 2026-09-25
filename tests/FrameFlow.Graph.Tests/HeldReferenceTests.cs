using Xunit;

using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// Where the substrate takes another reference, it keeps using the item it already holds
/// (ADR-0080, decision 1): a fork's extra branches and a join's match call <c>AddRef</c> and
/// hand on the same instance.
/// </summary>
/// <remarks>
/// <see cref="Tagged"/> breaks the rule on purpose: its <c>AddRef</c> returns a different
/// object, marked as not the original. A consumer that sees one knows the substrate used the
/// return value.
/// </remarks>
public sealed class HeldReferenceTests
{
    [Fact]
    public async Task AFork_GivesEveryBranchTheItemItHolds()
    {
        int next = 0;
        var source = new SourceNode<Tagged>(
            "source",
            _ => ValueTask.FromResult(next < 3 ? new Tagged(++next, isOriginal: true) : null)
        );
        int trunkOriginals = 0;
        int branchOriginals = 0;

        var graph = new GraphRunner();
        var head = graph.Pipeline(source);
        head.Branch(EdgeOptions.Buffered(4)).To(Count("branch", () => branchOriginals++));
        head.To(Count("trunk", () => trunkOriginals++));

        await graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(3, trunkOriginals);
        Assert.Equal(3, branchOriginals);
    }

    [Fact]
    public async Task AJoinsMatch_IsTheSecondaryItRetained()
    {
        var join = new SyncJoinNode<RefBox<int>, Tagged, RefBox<bool>>(
            "join",
            (primary, secondary, _) =>
                ValueTask.FromResult<RefBox<bool>?>(RefBox.Of(secondary?.IsOriginal == true)),
            new SyncJoinKeys<RefBox<int>, Tagged>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(10)
        );

        int secondaryPulls = 0;
        var secondary = new SourceNode<Tagged>(
            "secondary",
            _ => ValueTask.FromResult(secondaryPulls++ == 0 ? new Tagged(5, isOriginal: true) : null)
        );

        // The first pull waits until the secondary is retained, so the primary cannot miss it.
        int primaryPulls = 0;
        var primary = new SourceNode<RefBox<int>>(
            "primary",
            async ct =>
            {
                if (primaryPulls == 0)
                    await SpinUntil(() => join.RetainedCount >= 1, ct).ConfigureAwait(false);
                return primaryPulls++ == 0 ? RefBox.Of(10) : null;
            }
        );

        var matchedOriginal = new List<bool>();
        var graph = new GraphRunner();
        graph.Pipeline(secondary).ToSecondary(join, EdgeOptions.Buffered(4));
        graph.Pipeline(primary).ToPrimary(join);
        graph
            .Pipeline(join.Output)
            .To(
                new SinkNode<RefBox<bool>>(
                    "sink",
                    (item, _) =>
                    {
                        matchedOriginal.Add(item.Value);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        await graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(new[] { true }, matchedOriginal);
    }

    private static SinkNode<Tagged> Count(string id, Action onOriginal) =>
        new(
            id,
            (item, _) =>
            {
                if (item.IsOriginal)
                    onOriginal();
                return ValueTask.CompletedTask;
            }
        );

    /// <summary>
    /// Waits for a state the join exposes. It yields between checks and involves no duration
    /// (ADR-0072); the join raises no event when it retains an item.
    /// </summary>
    private static async Task SpinUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    /// <summary>An item whose <c>AddRef</c> returns a different object, which the rule forbids.</summary>
    private sealed class Tagged(int value, bool isOriginal) : IRefCounted
    {
        public int Value => value;
        public bool IsOriginal => isOriginal;

        public IRefCounted AddRef() => new Tagged(value, isOriginal: false);

        public void Dispose() { }
    }
}
