using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// A join whose secondary is a frame must set a lead bound (ADR-0080, decision 8): every
/// retained frame keeps its storage alive, and without a lead the join retains every secondary
/// that arrives ahead of the primary (#90).
/// </summary>
public sealed class SyncJoinFrameSecondaryTests
{
    [Fact]
    public void FrameSecondary_WithoutALead_IsRefusedAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(() => NewJoin<FrameItem>(maxLead: null));

        Assert.Equal("maxLead", ex.ParamName);
        Assert.Contains("'join'", ex.Message);
    }

    [Fact]
    public void FrameSecondary_WithALead_IsAccepted()
    {
        var join = NewJoin<FrameItem>(maxLead: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), join.MaxLead);
    }

    [Fact]
    public void SecondaryThatIsNotAFrame_WithoutALead_IsAccepted()
    {
        var join = NewJoin<RefBox<int>>(maxLead: null);

        Assert.Null(join.MaxLead);
    }

    private static SyncJoinNode<RefBox<int>, TSecondary, RefBox<int>> NewJoin<TSecondary>(
        TimeSpan? maxLead
    )
        where TSecondary : class, IRefCounted =>
        new(
            "join",
            (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
            new SyncJoinKeys<RefBox<int>, TSecondary>(
                p => TimeSpan.FromMilliseconds(p.Value),
                _ => (TimeSpan.Zero, TimeSpan.Zero)
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(1),
            maxLead: maxLead
        );

    private sealed class FrameItem : IFrame, IRefCounted
    {
        public int Width => 1;
        public int Height => 1;
        public TimeSpan Timestamp => TimeSpan.Zero;

        public IRefCounted AddRef() => this;

        public void Dispose() { }
    }
}
