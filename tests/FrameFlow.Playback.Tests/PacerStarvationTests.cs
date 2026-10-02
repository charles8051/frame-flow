using FrameFlow.Playback.Core;

namespace FrameFlow.Playback.Tests;

/// <summary>When an empty pacer ring counts as starved, and which edges it reports (#547).</summary>
public sealed class PacerStarvationTests
{
    [Theory]
    [InlineData(0, 1000)] // unknown duration
    [InlineData(33, 1000)] // 30 fps: three intervals are well under the minimum
    [InlineData(500, 1500)] // 2 fps: three intervals
    [InlineData(-10, 1000)] // a nonsense duration falls back to the minimum
    public void Threshold_IsThreeFrameIntervals_AndAtLeastTheMinimum(int frameMs, int expectedMs)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMs),
            PacerStarvation.Threshold(TimeSpan.FromMilliseconds(frameMs)));
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)] // paused: the user stopped the clock
    [InlineData(false, true, true, false)] // input complete: the ring empties because the stream ended
    [InlineData(false, false, false, false)] // no frame presented this run: startup or a seek
    public void Times_OnlyWhilePlayingAfterAFrame_WithMoreInputToCome(
        bool paused, bool inputComplete, bool presentedThisRun, bool expected)
    {
        Assert.Equal(expected, default(PacerStarvation).Times(paused, inputComplete, presentedThisRun));
    }

    [Fact]
    public void AStarvedRing_IsNotTimedAgain()
    {
        Assert.False(new PacerStarvation(Starved: true).Times(false, false, true));
    }

    [Fact]
    public void TimingOut_ReportsAnUnderrunOnce()
    {
        var (starved, first) = default(PacerStarvation).TimedOut();
        var (_, second) = starved.TimedOut();

        Assert.True(starved.Starved);
        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void Resolving_ReportsReadyOnlyAfterAnUnderrun()
    {
        var (fed, afterUnderrun) = new PacerStarvation(Starved: true).Resolved();
        var (_, withoutUnderrun) = default(PacerStarvation).Resolved();

        Assert.False(fed.Starved);
        Assert.True(afterUnderrun);
        Assert.False(withoutUnderrun);
    }

    [Fact]
    public void Forgetting_ClearsTheUnderrun_SoTheNextWaitIsTimed()
    {
        var forgotten = new PacerStarvation(Starved: true).Forgotten();

        Assert.False(forgotten.Starved);
        Assert.True(forgotten.Times(false, false, true));
    }
}
