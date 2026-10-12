using FrameFlow.Whisper.Core;

namespace FrameFlow.Whisper.Tests;

/// <summary>
/// The transcription window's timeline: contiguous audio accumulates, a jump starts a new window
/// at the jump's own time, and closed windows carry the times of their first samples.
/// </summary>
public sealed class WindowTimelineTests
{
    private const int Mono16k = 16_000;
    private const int FiveSecondsMono = 5 * Mono16k;

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    private static int SamplesFor(double seconds, int samplesPerSecond = Mono16k) =>
        (int)(seconds * samplesPerSecond);

    // ─── Admit ───────────────────────────────────────────────────────

    [Fact]
    public void FirstBuffer_AnchorsTheWindowAtItsOwnTime()
    {
        var admission = WindowTimeline.Admit(null, Seconds(42), SamplesFor(0.1), Mono16k);

        Assert.False(admission.Discard);
        Assert.Equal(Seconds(42), admission.Clock.Start);
        Assert.Equal(SamplesFor(0.1), admission.Clock.Samples);
        Assert.Equal(Seconds(42.1), admission.Clock.End);
    }

    [Fact]
    public void ContiguousBuffers_Accumulate()
    {
        WindowClock? clock = null;
        for (int i = 0; i < 30; i++)
        {
            var admission = WindowTimeline.Admit(clock, Seconds(10 + i * 0.1), SamplesFor(0.1), Mono16k);
            Assert.False(admission.Discard);
            clock = admission.Clock;
        }

        Assert.Equal(Seconds(10), clock!.Value.Start);
        Assert.Equal(Seconds(13), clock.Value.End);
    }

    [Theory]
    [InlineData(-0.150)]
    [InlineData(-0.005)]
    [InlineData(0.005)]
    [InlineData(0.150)]
    public void Jitter_WithinTolerance_IsTheNextAudio(double offsetSeconds)
    {
        var held = new WindowClock(Seconds(10), SamplesFor(2), Mono16k);

        var admission = WindowTimeline.Admit(held, held.End + Seconds(offsetSeconds), SamplesFor(0.1), Mono16k);

        Assert.False(admission.Discard);
        Assert.Equal(Seconds(10), admission.Clock.Start);
        Assert.Equal(SamplesFor(2.1), admission.Clock.Samples);
    }

    [Theory]
    [InlineData(0.0)]   // seek back to the start, or a loop
    [InlineData(5.0)]   // seek back
    [InlineData(60.0)]  // seek forward
    public void Jump_DropsTheHeldAudio_AndStartsAtTheJumpTime(double jumpToSeconds)
    {
        var held = new WindowClock(Seconds(10), SamplesFor(3), Mono16k);

        var admission = WindowTimeline.Admit(held, Seconds(jumpToSeconds), SamplesFor(0.1), Mono16k);

        Assert.True(admission.Discard);
        Assert.Equal(Seconds(jumpToSeconds), admission.Clock.Start);
        Assert.Equal(SamplesFor(0.1), admission.Clock.Samples);
    }

    [Fact]
    public void Jump_JustPastTolerance_DropsTheHeldAudio()
    {
        var held = new WindowClock(Seconds(10), SamplesFor(1), Mono16k);
        var past = held.End + WindowTimeline.DiscontinuityTolerance + TimeSpan.FromTicks(1);

        Assert.True(WindowTimeline.Admit(held, past, SamplesFor(0.1), Mono16k).Discard);
    }

    [Fact]
    public void EmptyWindow_TakesTheBufferTime_WithoutDiscarding()
    {
        var empty = new WindowClock(Seconds(15), 0, Mono16k);

        var admission = WindowTimeline.Admit(empty, Seconds(90), SamplesFor(0.1), Mono16k);

        Assert.False(admission.Discard);
        Assert.Equal(Seconds(90), admission.Clock.Start);
    }

    [Fact]
    public void Stereo_TimesSamplesAcrossBothChannels()
    {
        const int stereo16k = 2 * Mono16k;
        var held = new WindowClock(Seconds(0), SamplesFor(1, stereo16k), stereo16k);

        Assert.Equal(Seconds(1), held.End);
        Assert.False(WindowTimeline.Admit(held, Seconds(1), SamplesFor(0.1, stereo16k), stereo16k).Discard);
    }

    // ─── Close ───────────────────────────────────────────────────────

    [Fact]
    public void Close_BelowAWindow_ClosesNothing()
    {
        var clock = new WindowClock(Seconds(10), FiveSecondsMono - 1, Mono16k);

        var (starts, remainder) = WindowTimeline.Close(clock, FiveSecondsMono);

        Assert.Empty(starts);
        Assert.Equal(clock, remainder);
    }

    [Fact]
    public void Close_EachWindowStartsWhereThePreviousEnded_AndTheRemainderFollows()
    {
        var clock = new WindowClock(Seconds(10), 2 * FiveSecondsMono + SamplesFor(0.5), Mono16k);

        var (starts, remainder) = WindowTimeline.Close(clock, FiveSecondsMono);

        Assert.Equal(new[] { Seconds(10), Seconds(15) }, starts);
        Assert.Equal(Seconds(20), remainder.Start);
        Assert.Equal(SamplesFor(0.5), remainder.Samples);
    }

    [Fact]
    public void Close_ZeroWindow_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WindowTimeline.Close(new WindowClock(Seconds(0), 10, Mono16k), 0)
        );

    // ─── A seek, end to end ──────────────────────────────────────────

    [Fact]
    public void AfterASeek_TheNextWindowHoldsOnlyAudioFromTheNewPosition()
    {
        // Three seconds of audio from 0 s, then a seek to 60 s and six seconds from there, in
        // 100 ms buffers. The first window to close must start at 60 s: the 3 s from before
        // the seek are dropped rather than joined to the new audio.
        WindowClock? clock = null;
        var closed = new List<TimeSpan>();
        var held = 0L;

        void Feed(double pts)
        {
            var admission = WindowTimeline.Admit(clock, Seconds(pts), SamplesFor(0.1), Mono16k);
            held = admission.Discard ? SamplesFor(0.1) : held + SamplesFor(0.1);
            var (starts, remainder) = WindowTimeline.Close(admission.Clock, FiveSecondsMono);
            closed.AddRange(starts);
            held -= starts.Count * (long)FiveSecondsMono;
            clock = remainder;
            Assert.Equal(held, clock.Value.Samples);
        }

        for (int i = 0; i < 30; i++)
            Feed(i * 0.1);
        for (int i = 0; i < 60; i++)
            Feed(60 + i * 0.1);

        Assert.Equal(new[] { Seconds(60) }, closed);
        Assert.Equal(Seconds(65), clock!.Value.Start);
    }
}
