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

    /// <summary>
    /// An observation taken after the last frame has had its display interval, which is the
    /// half of the decision these cases are not about. The gate itself is
    /// <see cref="Observe_WhileTheFrameIsStillDisplaying_IsNeverStopped"/>.
    /// </summary>
    private static MasterClockProbeOutcome Observe(
        MasterClockProbe prior,
        TimeSpan reading,
        int? stallsBeforeStopped = null
    ) =>
        MasterClockStall.Observe(
            prior,
            reading,
            elapsedInHold: TimeSpan.FromHours(1),
            displayRemainingAtStart: TimeSpan.Zero,
            stallsBeforeStopped
                ?? MasterClockStall.DefaultStallsBeforeStopped
        );

    /// <summary>
    /// The verdict cannot end the hold while the last frame still owes display time, however
    /// certainly the clock has stopped. A liveness read that is wrong then would cut the frame
    /// short, which is the failure the hold exists to prevent (#249); gated this way the worst
    /// a wrong read can do is end the run at the moment the frame was due to finish anyway.
    /// </summary>
    [Fact]
    public void Observe_WhileTheFrameIsStillDisplaying_IsNeverStopped()
    {
        var probe = Seed(2);

        // Far more consecutive stalls than the threshold, against a frame with a second still
        // to run and only a tenth of it elapsed.
        for (int i = 0; i < MasterClockStall.DefaultStallsBeforeStopped * 4; i++)
        {
            var outcome = MasterClockStall.Observe(
                probe,
                TimeSpan.FromSeconds(2),
                elapsedInHold: TimeSpan.FromMilliseconds(100),
                displayRemainingAtStart: TimeSpan.FromSeconds(1)
            );

            Assert.False(outcome.Stopped);
            probe = outcome.Next;
        }

        // The same probe state, once the interval has elapsed.
        Assert.True(
            MasterClockStall
                .Observe(
                    probe,
                    TimeSpan.FromSeconds(2),
                    elapsedInHold: TimeSpan.FromSeconds(1),
                    displayRemainingAtStart: TimeSpan.FromSeconds(1)
                )
                .Stopped
        );
    }

    /// <summary>
    /// A frame that owed nothing when the hold began is gated on nothing, so the window alone
    /// decides. This is the common case: the last frame of a clip is usually already at or
    /// past its end by the time the buffer empties.
    /// </summary>
    [Fact]
    public void Observe_WithNoDisplayTimeOwed_IsDecidedByTheWindowAlone()
    {
        var probe = Seed(2);
        MasterClockProbeOutcome outcome = default;

        for (int i = 0; i < MasterClockStall.DefaultStallsBeforeStopped; i++)
        {
            outcome = MasterClockStall.Observe(
                probe,
                TimeSpan.FromSeconds(2),
                elapsedInHold: TimeSpan.Zero,
                displayRemainingAtStart: TimeSpan.Zero
            );
            probe = outcome.Next;
        }

        Assert.True(outcome.Stopped);
    }

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
        var outcome = Observe(Seed(2), TimeSpan.FromSeconds(2));

        Assert.False(outcome.Stopped);
        Assert.Equal(1, outcome.Next.ConsecutiveStalls);
    }

    [Fact]
    public void Observe_TheThresholdthConsecutiveStall_IsStopped()
    {
        var probe = Seed(2);
        MasterClockProbeOutcome outcome = default;

        for (int i = 1; i <= MasterClockStall.DefaultStallsBeforeStopped; i++)
        {
            outcome = Observe(probe, TimeSpan.FromSeconds(2));
            Assert.Equal(i >= MasterClockStall.DefaultStallsBeforeStopped, outcome.Stopped);
            probe = outcome.Next;
        }

        Assert.True(outcome.Stopped);
        Assert.Equal(MasterClockStall.DefaultStallsBeforeStopped, probe.ConsecutiveStalls);
    }

    /// <summary>
    /// The case the hold must never call stopped: a master that is running behind but still
    /// moving. Any advance resets the count, so a slow clock never accumulates a verdict.
    /// </summary>
    [Fact]
    public void Observe_AnAdvance_ResetsTheCount()
    {
        var stalled = Observe(Seed(2), TimeSpan.FromSeconds(2));
        Assert.Equal(1, stalled.Next.ConsecutiveStalls);

        var moved = Observe(stalled.Next, TimeSpan.FromSeconds(2.001));

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
            var outcome = Observe(probe, TimeSpan.FromMilliseconds(i));
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
        var probe = Seed(5);
        MasterClockProbeOutcome outcome = default;

        // Every reading is below the high water, so none of them counts as an advance and the
        // stalls accumulate exactly as if the clock had not moved at all.
        for (int i = 0; i < MasterClockStall.DefaultStallsBeforeStopped; i++)
        {
            outcome = Observe(probe, TimeSpan.FromSeconds(1));
            probe = outcome.Next;
        }

        Assert.True(outcome.Stopped);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, probe.HighWaterTicks);
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
            var below = Observe(probe, TimeSpan.FromSeconds(2), threshold);
            Assert.False(below.Stopped);
            probe = below.Next;
        }

        Assert.True(Observe(probe, TimeSpan.FromSeconds(2), threshold).Stopped);
    }
}
