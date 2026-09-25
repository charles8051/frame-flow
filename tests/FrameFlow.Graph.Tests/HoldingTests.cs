using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// What a node declares about the items it holds (ADR-0081, decisions 1 and 2), and the
/// default for a node that declares nothing.
/// </summary>
public sealed class HoldingTests
{
    [Fact]
    public void ThePresets_SayWhatTheyHold()
    {
        Assert.Null(FrameHolding.Unbounded.MaxHeld);
        Assert.True(FrameHolding.Unbounded.ForwardsStorage);

        Assert.Equal(1, FrameHolding.InFlight.MaxHeld);
        Assert.True(FrameHolding.InFlight.ForwardsStorage);

        Assert.Equal(1, FrameHolding.Boundary.MaxHeld);
        Assert.False(FrameHolding.Boundary.ForwardsStorage);
    }

    [Fact]
    public void AtMost_CarriesItsCountsAndRefusesLessThanOne()
    {
        var clip = FrameHolding.AtMost(31, forwardsStorage: true, framesPerOutputItem: 30);

        Assert.Equal(31, clip.MaxHeld);
        Assert.Equal(30, clip.FramesPerOutputItem);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameHolding.AtMost(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameHolding.AtMost(1, framesPerOutputItem: 0));
    }

    [Fact]
    public void ANodeThatDeclaresNothing_IsUnbounded()
    {
        Assert.Same(FrameHolding.Unbounded, Passthrough(holding: null).Holding);
        Assert.Same(FrameHolding.Unbounded, new SinkNode<RefBox<int>>("sink", (_, _) => ValueTask.CompletedTask).Holding);
        Assert.Same(
            FrameHolding.Unbounded,
            new MultiOperatorNode<RefBox<int>, RefBox<int>>("multi", (_, _) => Empty()).Holding
        );
    }

    [Fact]
    public void ANodeKeepsWhatItDeclares()
    {
        var node = Passthrough(FrameHolding.Boundary);

        Assert.Same(FrameHolding.Boundary, node.Holding);
        Assert.Same(FrameHolding.Boundary, ((IDeclaresHolding)node).HoldingAt(node.Input));
    }

    [Fact]
    public void AJoin_HoldsItsPrimaryForTheCall_AndItsSecondaryUpToItsCountLimit()
    {
        var limited = Join(maxRetained: 4);
        var unlimited = Join(maxRetained: null);

        Assert.Same(FrameHolding.InFlight, ((IDeclaresHolding)limited).HoldingAt(limited.Primary));
        // The window's 4 and the one the reader holds while the window is full.
        Assert.Equal(5, ((IDeclaresHolding)limited).HoldingAt(limited.Secondary).MaxHeld);
        // A lead alone is a duration, which bounds no count.
        Assert.Same(FrameHolding.Unbounded, ((IDeclaresHolding)unlimited).HoldingAt(unlimited.Secondary));
    }

    private static OperatorNode<RefBox<int>, RefBox<int>> Passthrough(FrameHolding? holding) =>
        new("op", (item, _) => ValueTask.FromResult<RefBox<int>?>(item), holding: holding);

    private static async IAsyncEnumerable<RefBox<int>> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>> Join(int? maxRetained) =>
        new(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (TimeSpan.FromMilliseconds(s.Value), TimeSpan.FromMilliseconds(s.Value))
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(1),
            maxLead: TimeSpan.FromMilliseconds(100),
            maxRetained: maxRetained
        );
}
