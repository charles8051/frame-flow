using FrameFlow.Audio.OpenAL;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Pins <see cref="AudioClockInterpolation"/>, which smooths the audio master clock between
/// device updates (issue #125).
/// </summary>
/// <remarks>
/// <para>
/// <c>AL_SAMPLE_OFFSET</c> only moves once per OpenAL mixing period — measured at exactly
/// 20.00 ms on the device this was written against, every step. Everything paced against the
/// master clock therefore moved in 20 ms increments, so a 60 fps source released at 50 fps
/// and the extra frames were discarded as late.
/// </para>
/// <para>
/// The two properties that make this safe to put under the master clock are bounded lead and
/// non-decreasing output, so those get the most coverage here.
/// </para>
/// </remarks>
public sealed class AudioClockInterpolationTests
{
    private const long Freq = 1_000_000; // 1 tick = 1 microsecond
    private static readonly TimeSpan Cap = TimeSpan.FromMilliseconds(20);

    private static long Us(double ms) => (long)(ms * 1000);

    private static (AudioClockAnchor, TimeSpan) Read(
        AudioClockAnchor anchor,
        double rawMs,
        double nowMs,
        bool interpolate = true
    ) =>
        AudioClockInterpolation.Read(
            anchor,
            TimeSpan.FromMilliseconds(rawMs),
            Us(nowMs),
            Freq,
            Cap,
            interpolate
        );

    [Fact]
    public void TheFirstReadAnchorsOnTheDevice()
    {
        var (anchor, position) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 0);

        Assert.Equal(TimeSpan.FromMilliseconds(100), position);
        Assert.True(anchor.Valid);
    }

    [Fact]
    public void AStaleDeviceValueAdvancesByElapsedTime()
    {
        var (anchor, _) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 0);

        // Device has not moved, but 8 ms of wall time has. Without this the clock would sit
        // at 100 for the whole mixing period and everything pacing against it would step.
        var (_, position) = Read(anchor, rawMs: 100, nowMs: 8);

        Assert.Equal(TimeSpan.FromMilliseconds(108), position);
    }

    [Fact]
    public void ExtrapolationIsCapped()
    {
        var (anchor, _) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 0);

        // A device that stops updating — underrun, teardown — must make the clock stop, not
        // run away. The cap is the most it can ever lead the device by.
        var (_, position) = Read(anchor, rawMs: 100, nowMs: 5000);

        Assert.Equal(TimeSpan.FromMilliseconds(120), position);
    }

    [Fact]
    public void TheCapMeasuresFromTheLastRealObservation()
    {
        var (anchor, _) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 0);

        // Repeated reads must not each add their own elapsed time on top of the last.
        (anchor, _) = Read(anchor, rawMs: 100, nowMs: 5);
        (anchor, _) = Read(anchor, rawMs: 100, nowMs: 10);
        var (_, position) = Read(anchor, rawMs: 100, nowMs: 15);

        Assert.Equal(TimeSpan.FromMilliseconds(115), position);
    }

    [Fact]
    public void AMovingDeviceReAnchors()
    {
        var (anchor, _) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 0);
        (anchor, _) = Read(anchor, rawMs: 100, nowMs: 10); // interpolated to 110

        // The device catches up and passes what was published: ground truth wins.
        var (next, position) = Read(anchor, rawMs: 120, nowMs: 20);

        Assert.Equal(TimeSpan.FromMilliseconds(120), position);
        Assert.Equal(TimeSpan.FromMilliseconds(120), next.RawPosition);
    }

    [Fact]
    public void ADeviceUpdateBelowWhatWasPublishedIsHeld()
    {
        var (anchor, _) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 0);
        (anchor, var led) = Read(anchor, rawMs: 100, nowMs: 18); // interpolated to 118
        Assert.Equal(TimeSpan.FromMilliseconds(118), led);

        // The device lands at 110, behind the interpolation. Publishing it would step the
        // master clock backwards and make every "is this frame late?" answer wrong.
        var (_, position) = Read(anchor, rawMs: 110, nowMs: 19);

        Assert.Equal(TimeSpan.FromMilliseconds(118), position);
    }

    [Fact]
    public void TheOutputNeverDecreasesAcrossARealisticSequence()
    {
        // 20 ms device steps read every 4 ms, the shape that produced the bug.
        var anchor = AudioClockAnchor.None;
        var previous = TimeSpan.MinValue;

        for (int readMs = 0; readMs <= 400; readMs += 4)
        {
            var deviceMs = readMs / 20 * 20;
            (anchor, var position) = Read(anchor, deviceMs, readMs);

            Assert.True(position >= previous, $"stepped back at {readMs}ms");
            previous = position;
        }
    }

    [Fact]
    public void TheClockStaysWithinTheCapOfTheDevice()
    {
        // Bounded lead is what keeps this under the master clock: A/V sync tolerates a known
        // small offset, not an unbounded one.
        var anchor = AudioClockAnchor.None;

        for (int readMs = 0; readMs <= 400; readMs += 4)
        {
            var deviceMs = readMs / 20 * 20;
            (anchor, var position) = Read(anchor, deviceMs, readMs);

            var lead = position - TimeSpan.FromMilliseconds(deviceMs);
            Assert.True(lead <= Cap, $"led the device by {lead.TotalMilliseconds}ms at {readMs}ms");
        }
    }

    [Fact]
    public void NotAdvancingTracksTheDeviceExactly()
    {
        var (anchor, _) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 0, interpolate: false);

        // Paused: the clock must not creep forward while the stream is stopped.
        var (_, position) = Read(anchor, rawMs: 100, nowMs: 500, interpolate: false);

        Assert.Equal(TimeSpan.FromMilliseconds(100), position);
    }

    [Fact]
    public void AnInvalidatedAnchorAcceptsABackwardsJump()
    {
        var (anchor, _) = Read(AudioClockAnchor.None, rawMs: 5000, nowMs: 0);
        (anchor, _) = Read(anchor, rawMs: 5000, nowMs: 10);

        // A seek invalidates the anchor. The monotonic guard must not survive it, or the
        // clock would refuse to move to where the seek put it.
        Assert.NotEqual(AudioClockAnchor.None, anchor);
        var (_, position) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 20);

        Assert.Equal(TimeSpan.FromMilliseconds(100), position);
    }

    [Fact]
    public void AStaleAnchorWouldLeadTheDeviceByTheFullCap()
    {
        // Why the sink drops the anchor on resume rather than relying on the cap. A paused
        // session's last read anchors at the pause instant and nothing reads the clock while
        // paused, so a resume that kept the anchor would measure its gap from then — and jump
        // straight to the ceiling instead of starting where the device is.
        var (staleAnchor, _) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 0, interpolate: false);

        var (_, withStaleAnchor) = Read(staleAnchor, rawMs: 100, nowMs: 60_000);
        Assert.Equal(TimeSpan.FromMilliseconds(120), withStaleAnchor);

        // Dropped instead, the first read after resume is the device's own value.
        var (_, reAnchored) = Read(AudioClockAnchor.None, rawMs: 100, nowMs: 60_000);
        Assert.Equal(TimeSpan.FromMilliseconds(100), reAnchored);
    }

    [Fact]
    public void AZeroFrequencyClockFallsBackToTheDevice()
    {
        var (_, position) = AudioClockInterpolation.Read(
            AudioClockAnchor.None,
            TimeSpan.FromMilliseconds(100),
            nowTicks: 0,
            ticksPerSecond: 0,
            Cap,
            interpolate: true
        );

        Assert.Equal(TimeSpan.FromMilliseconds(100), position);
    }
}
