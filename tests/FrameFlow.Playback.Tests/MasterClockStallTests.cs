using FrameFlow.Playback;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Tests for <see cref="MasterClockStall"/>, the pure detector for a master clock that has
/// stopped rather than merely slowed (#359).
/// </summary>
/// <remarks>
/// The distinction is the whole point: <see cref="ClockSelectVideoSink"/>'s end-of-content
/// hold must not cut off a slow master mid-display (#249), and must not wait out its cap for
/// one that will never arrive. The shell coverage that the hold acts on this is in
/// <c>ClockSelectVideoSinkTests</c>.
/// </remarks>
public sealed class MasterClockStallTests
{
    private static MasterClockProbe Seed(double seconds) =>
        MasterClockProbe.From(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void From_SeedsTheHighWaterAndNoStalls()
    {
        var probe = Seed(1.5);

        Assert.Equal(TimeSpan.FromSeconds(1.5).Ticks, probe.HighWaterTicks);
        Assert.Equal(0, probe.ConsecutiveStalls);
    }

    /// <summary>
    /// One non-advancing reading is not a verdict. A master publishes on its own cadence, so
    /// a single observation can land inside one publish interval on a healthy clock.
    /// </summary>
    [Fact]
    public void Observe_OneStall_IsNotStopped()
    {
        var outcome = MasterClockStall.Observe(Seed(2), TimeSpan.FromSeconds(2));

        Assert.False(outcome.Stopped);
        Assert.Equal(1, outcome.Next.ConsecutiveStalls);
    }

    [Fact]
    public void Observe_TwoConsecutiveStalls_IsStopped()
    {
        var first = MasterClockStall.Observe(Seed(2), TimeSpan.FromSeconds(2));
        var second = MasterClockStall.Observe(first.Next, TimeSpan.FromSeconds(2));

        Assert.True(second.Stopped);
        Assert.Equal(2, second.Next.ConsecutiveStalls);
    }

    /// <summary>
    /// The case the hold must never call stopped: a master that is running behind but still
    /// moving. Any advance resets the count, so a slow clock never accumulates a verdict.
    /// </summary>
    [Fact]
    public void Observe_AnAdvance_ResetsTheCount()
    {
        var stalled = MasterClockStall.Observe(Seed(2), TimeSpan.FromSeconds(2));
        Assert.Equal(1, stalled.Next.ConsecutiveStalls);

        var moved = MasterClockStall.Observe(stalled.Next, TimeSpan.FromSeconds(2.001));

        Assert.False(moved.Stopped);
        Assert.Equal(0, moved.Next.ConsecutiveStalls);
        Assert.Equal(TimeSpan.FromSeconds(2.001).Ticks, moved.Next.HighWaterTicks);
    }

    [Fact]
    public void Observe_ASlowMasterNeverStops()
    {
        var probe = Seed(0);

        // Ten observations, each advancing by a hair. Nothing here is a stall.
        for (int i = 1; i <= 10; i++)
        {
            var outcome = MasterClockStall.Observe(probe, TimeSpan.FromMilliseconds(i));
            Assert.False(outcome.Stopped);
            probe = outcome.Next;
        }

        Assert.Equal(0, probe.ConsecutiveStalls);
    }

    /// <summary>
    /// A reading below the high water is a reseat, not progress. Treating it as an advance
    /// would let a clock that jumps backwards and stays there look alive forever.
    /// </summary>
    [Fact]
    public void Observe_AReadingThatWentBackwards_IsNotProgress()
    {
        var first = MasterClockStall.Observe(Seed(5), TimeSpan.FromSeconds(1));
        var second = MasterClockStall.Observe(first.Next, TimeSpan.FromSeconds(1));

        Assert.True(second.Stopped);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, second.Next.HighWaterTicks);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Observe_HonoursTheStallThreshold(int threshold)
    {
        var probe = Seed(2);

        for (int i = 1; i < threshold; i++)
        {
            var below = MasterClockStall.Observe(probe, TimeSpan.FromSeconds(2), threshold);
            Assert.False(below.Stopped);
            probe = below.Next;
        }

        Assert.True(MasterClockStall.Observe(probe, TimeSpan.FromSeconds(2), threshold).Stopped);
    }
}
