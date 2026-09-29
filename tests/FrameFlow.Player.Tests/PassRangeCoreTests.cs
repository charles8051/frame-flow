using FrameFlow.Player.Core;

namespace FrameFlow.Player.Tests;

/// <summary>What a pass bounded to a range delivers and where it seeks (#483). Pure.</summary>
public sealed class PassRangeCoreTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData(0.99, false)]
    [InlineData(1.0, true)] // the start is in the range
    [InlineData(1.5, true)]
    [InlineData(1.99, true)]
    [InlineData(2.0, false)] // the end is not
    [InlineData(2.5, false)]
    public void ARange_DeliversFromItsStart_ToBeforeItsEnd(double timestamp, bool delivered)
    {
        Assert.Equal(delivered, new PassRange(S(1), S(2)).Delivers(S(timestamp)));
    }

    [Fact]
    public void ARangeWithNoEnd_DeliversEverythingFromItsStart()
    {
        var range = new PassRange(S(1), null);

        Assert.False(range.Delivers(S(0.5)));
        Assert.True(range.Delivers(S(1)));
        Assert.True(range.Delivers(TimeSpan.MaxValue));
    }

    [Fact]
    public void ARangeFromZero_DeliversWhatTheSourceStampsBeforeZero()
    {
        Assert.True(new PassRange(TimeSpan.Zero, S(1)).Delivers(S(-0.02)));
        Assert.True(PassRange.Whole.Delivers(S(-0.02)));
    }

    [Fact]
    public void RangesThatShareAnEndpoint_DeliverEachItemOnce()
    {
        var first = new PassRange(S(1), S(2));
        var second = new PassRange(S(2), S(3));

        foreach (var t in new[] { 1.5, 2.0, 2.5 })
            Assert.True(first.Delivers(S(t)) ^ second.Delivers(S(t)), $"{t} s");
    }

    [Fact]
    public void OnlyARangeFromAfterZero_Seeks()
    {
        Assert.Null(PassRange.Whole.SeekTo);
        Assert.Null(new PassRange(TimeSpan.Zero, S(1)).SeekTo);
        Assert.Equal(S(1), new PassRange(S(1), null).SeekTo);
    }

    [Fact]
    public void OnlyTheWholeSource_IsWhole()
    {
        Assert.True(PassRange.Whole.IsWhole);
        Assert.True(default(PassRange).IsWhole);
        Assert.False(new PassRange(TimeSpan.Zero, S(1)).IsWhole);
        Assert.False(new PassRange(S(1), null).IsWhole);
    }
}
