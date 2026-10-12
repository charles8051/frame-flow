using FrameFlow.Whisper.Core;

namespace FrameFlow.Whisper.Tests;

/// <summary>
/// The transcription window's timeline: contiguous audio accumulates, a jump starts a new window
/// at the jump's own time, and closed windows carry the times of their first samples.
/// </summary>
public sealed class WindowTimelineTests
{
    private const int Rate = 16_000;
    private const int FiveSeconds = 5 * Rate;

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    private static int SamplesFor(double seconds) => (int)(seconds * Rate);

    // ─── Admit ───────────────────────────────────────────────────────

    [Fact]
    public void FirstBuffer_AnchorsTheWindowAtItsOwnTime()
    {
        var admission = WindowTimeline.Admit(null, Seconds(42), SamplesFor(0.1), Rate);

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
            var admission = WindowTimeline.Admit(clock, Seconds(10 + i * 0.1), SamplesFor(0.1), Rate);
            Assert.False(admission.Discard);
            clock = admission.Clock;
        }

        Assert.Equal(Seconds(10), clock!.Value.Start);
        Assert.Equal(Seconds(13), clock.Value.End);
    }

    [Theory]
    [InlineData(-0.040)]
    [InlineData(-0.002)]
    [InlineData(0.002)]
    [InlineData(0.040)]
    public void TimestampRounding_IsTheNextAudio(double offsetSeconds)
    {
        var held = new WindowClock(Seconds(10), SamplesFor(2), Rate);

        var admission = WindowTimeline.Admit(held, held.End + Seconds(offsetSeconds), SamplesFor(0.1), Rate);

        Assert.False(admission.Discard);
        Assert.Equal(Seconds(10), admission.Clock.Start);
        Assert.Equal(SamplesFor(2.1), admission.Clock.Samples);
    }

    [Theory]
    [InlineData(-0.1)]  // a small step back
    [InlineData(0.1)]   // a small step forward
    [InlineData(-10)]   // seek back to the start, or a loop
    [InlineData(-5)]    // seek back
    [InlineData(47)]    // seek forward
    public void Jump_DropsTheHeldAudio_AndStartsAtTheJumpTime(double jumpBySeconds)
    {
        var held = new WindowClock(Seconds(10), SamplesFor(3), Rate);
        var jumpTo = held.End + Seconds(jumpBySeconds);

        var admission = WindowTimeline.Admit(held, jumpTo, SamplesFor(0.1), Rate);

        Assert.True(admission.Discard);
        Assert.Equal(jumpTo, admission.Clock.Start);
        Assert.Equal(SamplesFor(0.1), admission.Clock.Samples);
    }

    [Fact]
    public void Jump_JustPastTolerance_DropsTheHeldAudio()
    {
        var held = new WindowClock(Seconds(10), SamplesFor(1), Rate);
        var tolerance = WindowTimeline.DiscontinuityTolerance;
        var tick = TimeSpan.FromTicks(1);

        Assert.False(WindowTimeline.Admit(held, held.End + tolerance, SamplesFor(0.1), Rate).Discard);
        Assert.True(WindowTimeline.Admit(held, held.End + tolerance + tick, SamplesFor(0.1), Rate).Discard);
        Assert.True(WindowTimeline.Admit(held, held.End - tolerance - tick, SamplesFor(0.1), Rate).Discard);
    }

    [Fact]
    public void EmptyWindow_TakesTheBufferTime_WithoutDiscarding()
    {
        var empty = new WindowClock(Seconds(15), 0, Rate);

        var admission = WindowTimeline.Admit(empty, Seconds(90), SamplesFor(0.1), Rate);

        Assert.False(admission.Discard);
        Assert.Equal(Seconds(90), admission.Clock.Start);
    }

    // ─── CloseOne ────────────────────────────────────────────────────

    [Fact]
    public void CloseOne_BelowAWindow_ClosesNothing() =>
        Assert.Null(WindowTimeline.CloseOne(new WindowClock(Seconds(10), FiveSeconds - 1, Rate), FiveSeconds));

    [Fact]
    public void CloseOne_TakesOneWindow_AndTheRemainderStartsWhereItEnds()
    {
        var clock = new WindowClock(Seconds(10), 2 * FiveSeconds + SamplesFor(0.5), Rate);

        var first = WindowTimeline.CloseOne(clock, FiveSeconds)!.Value;
        var second = WindowTimeline.CloseOne(first.Remainder, FiveSeconds)!.Value;

        Assert.Equal(Seconds(10), first.Start);
        Assert.Equal(Seconds(15), second.Start);
        Assert.Equal(Seconds(20), second.Remainder.Start);
        Assert.Equal(SamplesFor(0.5), second.Remainder.Samples);
        Assert.Null(WindowTimeline.CloseOne(second.Remainder, FiveSeconds));
    }

    [Fact]
    public void CloseOne_ZeroWindow_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WindowTimeline.CloseOne(new WindowClock(Seconds(0), 10, Rate), 0)
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
            var admission = WindowTimeline.Admit(clock, Seconds(pts), SamplesFor(0.1), Rate);
            held = admission.Discard ? SamplesFor(0.1) : held + SamplesFor(0.1);
            var current = admission.Clock;
            while (WindowTimeline.CloseOne(current, FiveSeconds) is { } window)
            {
                closed.Add(window.Start);
                held -= FiveSeconds;
                current = window.Remainder;
            }
            clock = current;
            Assert.Equal(held, current.Samples);
        }

        for (int i = 0; i < 30; i++)
            Feed(i * 0.1);
        for (int i = 0; i < 60; i++)
            Feed(60 + i * 0.1);

        Assert.Equal(new[] { Seconds(60) }, closed);
        Assert.Equal(Seconds(65), clock!.Value.Start);
    }

    // ─── Mono ────────────────────────────────────────────────────────

    [Fact]
    public void AppendMono_Mono_Scales()
    {
        var target = new List<float>();

        WhisperSamples.AppendMono(target, [16384, -32768, 0], channels: 1);

        Assert.Equal(new[] { 0.5f, -1f, 0f }, target);
    }

    [Fact]
    public void AppendMono_Stereo_AveragesEachFrame()
    {
        var target = new List<float> { 0.25f };

        WhisperSamples.AppendMono(target, [16384, 0, -32768, -32768, 32767, -32768], channels: 2);

        Assert.Equal(4, target.Count);
        Assert.Equal(0.25f, target[0]);
        Assert.Equal(0.25f, target[1]);
        Assert.Equal(-1f, target[2]);
        Assert.Equal(-1f / 65536f, target[3], 1e-7f);
    }
}
