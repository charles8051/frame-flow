using FrameFlow.Media;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins <see cref="AudioClockRateLimit"/>, which holds the audio master clock to the rate a
/// playing device can consume at (issue #127).
/// </summary>
/// <remarks>
/// <para>
/// The clock is the device's sample counter, credited with every buffer OpenAL reports
/// processed. "Processed" means the device let go of the buffer, not that it played it, so a
/// device that drops its queue reports the whole queue at once and a source that is started
/// but not producing reports each new buffer the moment it is queued. Both were observed:
/// a 1.09 s step across a pause, and 209 s of clock in 176 ms after it.
/// </para>
/// <para>
/// The guard is on the advance, not the value. An earlier <c>min(audioTime, sessionElapsed)</c>
/// compared source-stream PTS against elapsed-from-zero and clamped the clock to ~0 after
/// every seek; the seek cases below are the ones that form of the guard failed.
/// </para>
/// </remarks>
public sealed class AudioClockRateLimitTests
{
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(250);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void FirstReadingIsTakenAsIs()
    {
        var (anchor, position) = AudioClockRateLimit.Apply(
            AudioClockRateAnchor.None, Ms(7000), Ms(120), Slack);

        // Nothing to measure an advance over, and the device is ground truth. 7 s of source
        // time against 120 ms elapsed is an ordinary post-seek first read, not a fault.
        Assert.Equal(Ms(7000), position);
        Assert.True(anchor.Valid);
    }

    [Fact]
    public void AdvanceWithinRealTimePassesThrough()
    {
        var anchor = new AudioClockRateAnchor(Ms(1000), Ms(500), Valid: true);

        var (next, position) = AudioClockRateLimit.Apply(anchor, Ms(1050), Ms(550), Slack);

        Assert.Equal(Ms(1050), position);
        Assert.Equal(Ms(1050), next.Position);
    }

    [Fact]
    public void ABufferBoundaryInsideOneReadIntervalIsNotAFault()
    {
        var anchor = new AudioClockRateAnchor(Ms(1000), Ms(500), Valid: true);

        // 20 ms of wall time, 70 ms of counter: the counter steps a whole buffer at a time,
        // so a reading can lead elapsed time by up to a buffer without anything being wrong.
        var (_, position) = AudioClockRateLimit.Apply(anchor, Ms(1070), Ms(520), Slack);

        Assert.Equal(Ms(1070), position);
    }

    [Fact]
    public void ADrainedQueueIsHeldToWhatRealTimeAllows()
    {
        var anchor = new AudioClockRateAnchor(Ms(4340), Ms(4340), Valid: true);

        // The whole 16-buffer pool marked processed at once: +1090 ms of counter for 50 ms
        // of wall time. This is the step the report measured.
        var (next, position) = AudioClockRateLimit.Apply(anchor, Ms(5430), Ms(4390), Slack);

        Assert.Equal(Ms(4390), position);
        Assert.Equal(Ms(4390), next.Position);
    }

    [Fact]
    public void TheExcessIsDiscardedRatherThanRecoveredOverLaterReads()
    {
        // A source that is started but not producing: every reading stays far ahead. The
        // clock must keep advancing at real time, not creep back up to the device's claim
        // one slack at a time — that creep is what let 209 s through in 176 ms.
        var anchor = new AudioClockRateAnchor(Ms(4340), Ms(4340), Valid: true);
        var raw = Ms(5430);
        var elapsed = Ms(4340);

        for (int i = 0; i < 20; i++)
        {
            elapsed += Ms(50);
            raw += Ms(5000); // decode rate, not playback rate
            (anchor, var position) = AudioClockRateLimit.Apply(anchor, raw, elapsed, Slack);
            Assert.Equal(elapsed, position);
        }

        // 20 reads, 1 second of wall time, 1 second of clock — against 100 s claimed.
        Assert.Equal(Ms(5340), anchor.Position);
    }

    [Fact]
    public void AStandingStillDeviceIsLeftAlone()
    {
        var anchor = new AudioClockRateAnchor(Ms(1000), Ms(500), Valid: true);

        var (_, position) = AudioClockRateLimit.Apply(anchor, Ms(1000), Ms(560), Slack);

        Assert.Equal(Ms(1000), position);
    }

    [Fact]
    public void ADeviceGoingBackwardsIsLeftAlone()
    {
        // Monotonicity is the interpolator's job, not this one's. Clamping a backwards step
        // here would hide a seek from the layer that has to see it.
        var anchor = new AudioClockRateAnchor(Ms(1000), Ms(500), Valid: true);

        var (_, position) = AudioClockRateLimit.Apply(anchor, Ms(200), Ms(550), Slack);

        Assert.Equal(Ms(200), position);
    }

    [Fact]
    public void ASeekForwardWithADroppedAnchorIsNotClamped()
    {
        // The case that killed the absolute-value form. Seeking to 200 s moves the clock
        // 200 s in no wall time at all; the caller drops the anchor at the discontinuity, so
        // the next reading is a first reading and passes.
        var (_, position) = AudioClockRateLimit.Apply(
            AudioClockRateAnchor.None, Ms(200_000), Ms(4390), Slack);

        Assert.Equal(Ms(200_000), position);
    }

    [Fact]
    public void ASeekForwardWithoutDroppingTheAnchorWouldBeClamped()
    {
        // States the contract the caller has to meet, so a discontinuity added later that
        // forgets to drop the anchor fails here rather than in the field.
        var anchor = new AudioClockRateAnchor(Ms(4340), Ms(4340), Valid: true);

        var (_, position) = AudioClockRateLimit.Apply(anchor, Ms(200_000), Ms(4390), Slack);

        Assert.Equal(Ms(4390), position);
    }

    [Fact]
    public void ElapsedGoingBackwardsDoesNotWidenTheCeiling()
    {
        var anchor = new AudioClockRateAnchor(Ms(1000), Ms(500), Valid: true);

        // A stopwatch that reads lower than the anchor (a reset between reads) must not
        // produce a negative interval that lets a jump through.
        var (_, position) = AudioClockRateLimit.Apply(anchor, Ms(9000), Ms(100), Slack);

        Assert.Equal(Ms(1000), position);
    }
}
