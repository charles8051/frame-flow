using FrameFlow.Avalonia.Core;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #576: the wait before a frame is judged from the end of the previous frame's display, not
/// from its arrival, so a still held for its dwell is not reported as upstream having stopped.
/// </summary>
/// <remarks>
/// Values in, verdicts out: no sink, no clock. <see cref="AvaloniaVideoSinkPresentGapTests"/>
/// drives the same cases through the sink.
/// </remarks>
public sealed class PresentGapTests
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(333_333); // 30 fps
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(3);

    // What moving from a still to the next item took when this was reproduced.
    private static readonly TimeSpan Transition = TimeSpan.FromMilliseconds(110);

    [Fact]
    public void AFrameThatFollowsAStillsDwell_IsNotAStall()
    {
        var gap = new PresentGap(Dwell + Transition, Held: Dwell);

        Assert.Equal(Transition, gap.Overrun);
        Assert.False(gap.IsStall);
    }

    [Fact]
    public void AFrameLatePastAStillsDwell_IsAStall()
    {
        var gap = new PresentGap(Dwell + TimeSpan.FromMilliseconds(600), Held: Dwell);

        Assert.Equal(TimeSpan.FromMilliseconds(600), gap.Overrun);
        Assert.True(gap.IsStall);
    }

    [Fact]
    public void FramesAtAClipsRate_AreNotAStall()
    {
        Assert.False(new PresentGap(FrameInterval, Held: FrameInterval).IsStall);
    }

    [Fact]
    public void AFrameLateInAClip_IsAStall()
    {
        var gap = new PresentGap(TimeSpan.FromMilliseconds(600), Held: FrameInterval);

        Assert.True(gap.IsStall);
    }

    [Fact]
    public void TheThresholdIsExclusive()
    {
        var atThreshold = new PresentGap(Dwell + PresentGap.StallThreshold, Held: Dwell);
        var pastIt = new PresentGap(Dwell + PresentGap.StallThreshold + TimeSpan.FromTicks(1), Held: Dwell);

        Assert.False(atThreshold.IsStall);
        Assert.True(pastIt.IsStall);
    }

    [Fact]
    public void AFrameWithNoDuration_IsJudgedOnTheWholeInterval()
    {
        // A frame that does not say how long it shows is held to the 500 ms the sink always used.
        Assert.False(new PresentGap(TimeSpan.FromMilliseconds(500), Held: TimeSpan.Zero).IsStall);
        Assert.True(new PresentGap(TimeSpan.FromMilliseconds(501), Held: TimeSpan.Zero).IsStall);
    }

    [Fact]
    public void ANegativeDuration_CountsAsNone()
    {
        var gap = new PresentGap(TimeSpan.FromMilliseconds(400), Held: TimeSpan.FromSeconds(-1));

        Assert.Equal(TimeSpan.FromMilliseconds(400), gap.Overrun);
        Assert.False(gap.IsStall);
    }

    [Fact]
    public void AFrameThatCutsAStillShort_HasNoOverrun()
    {
        // A skip or a seek replaces a still before its dwell is up.
        var gap = new PresentGap(TimeSpan.FromSeconds(1), Held: Dwell);

        Assert.Equal(TimeSpan.Zero, gap.Overrun);
        Assert.False(gap.IsStall);
    }

    [Fact]
    public void TheFirstFrame_HasNoGap()
    {
        var (_, gap) = default(PresentGapTracker).Observe(At(5), Dwell);

        Assert.Null(gap);
    }

    [Fact]
    public void TheGapAfterAStill_IsJudgedOnTheStillsDuration()
    {
        var tracker = default(PresentGapTracker);

        (tracker, _) = tracker.Observe(At(0), Dwell); // the still
        var (_, gap) = tracker.Observe(At(0) + Dwell + Transition, FrameInterval); // the next item

        Assert.Equal(new PresentGap(Dwell + Transition, Held: Dwell), gap);
        Assert.False(gap!.Value.IsStall);
    }

    [Fact]
    public void AStillsDuration_DoesNotExcuseTheWaitBeforeIt()
    {
        var tracker = default(PresentGapTracker);

        // A clip's last frame, then the still two seconds later. The still's dwell is the time
        // after it, so the two seconds are judged on the clip's frame interval.
        (tracker, _) = tracker.Observe(At(0), FrameInterval);
        var (_, gap) = tracker.Observe(At(2), Dwell);

        Assert.Equal(new PresentGap(TimeSpan.FromSeconds(2), Held: FrameInterval), gap);
        Assert.True(gap!.Value.IsStall);
    }

    [Fact]
    public void EachGap_IsMeasuredFromTheFrameBeforeIt()
    {
        var tracker = default(PresentGapTracker);
        var gaps = new List<PresentGap?>();

        foreach (var (arrival, duration) in new[] { (At(0), Dwell), (At(3.1), FrameInterval), (At(4), FrameInterval) })
        {
            (tracker, var gap) = tracker.Observe(arrival, duration);
            gaps.Add(gap);
        }

        Assert.Equal(
            [
                null,
                new PresentGap(TimeSpan.FromSeconds(3.1), Held: Dwell),
                new PresentGap(TimeSpan.FromSeconds(0.9), Held: FrameInterval),
            ],
            gaps
        );
        Assert.Equal([false, true], gaps.Skip(1).Select(g => g!.Value.IsStall));
    }

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
}
