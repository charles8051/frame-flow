using FrameFlow.Avalonia.Core;
using FrameFlow.Graph;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #576: the wait before a frame is judged from the end of the previous frame's display, not
/// from its arrival, so a still held for its dwell is not reported as upstream having stopped. And
/// #624: the wait before the first frame of a new timeline is not judged, so the next item after
/// the player sat at Ended is not reported either.
/// </summary>
/// <remarks>
/// Values in, verdicts out: no sink, no clock. <see cref="AvaloniaVideoSinkPresentGapTests"/>
/// drives the same cases through the sink.
/// </remarks>
public sealed class PresentGapTests
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(333_333); // 30 fps
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(3);

    // A source that carries no timeline: it pins the judgement every frame had before timelines.
    private static readonly TimelineId Untracked = TimelineId.None;

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
        var (_, gap) = default(PresentGapTracker).Observe(At(5), Dwell, Untracked);

        Assert.Null(gap);
    }

    [Fact]
    public void TheGapAfterAStill_IsJudgedOnTheStillsDuration()
    {
        var tracker = default(PresentGapTracker);

        (tracker, _) = tracker.Observe(At(0), Dwell, Untracked); // the still
        var (_, gap) = tracker.Observe(At(0) + Dwell + Transition, FrameInterval, Untracked); // the next item

        Assert.Equal(new PresentGap(Dwell + Transition, Held: Dwell), gap);
        Assert.False(gap!.Value.IsStall);
    }

    [Fact]
    public void AStillsDuration_DoesNotExcuseTheWaitBeforeIt()
    {
        var tracker = default(PresentGapTracker);

        // A clip's last frame, then the still two seconds later. The still's dwell is the time
        // after it, so the two seconds are judged on the clip's frame interval.
        (tracker, _) = tracker.Observe(At(0), FrameInterval, Untracked);
        var (_, gap) = tracker.Observe(At(2), Dwell, Untracked);

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
            (tracker, var gap) = tracker.Observe(arrival, duration, Untracked);
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

    // ── A new timeline starts the tracker over (ADR-0084, decision 5; #624) ──

    // The item that played to its end, and the item the player was given after it.
    private static readonly TimelineId Ended = TimelineId.Next();
    private static readonly TimelineId Given = TimelineId.Next();

    // How long the player sat at Ended before it was given the next item when #624 was seen.
    private static readonly TimeSpan SatAtEnded = TimeSpan.FromMilliseconds(3193);

    [Fact]
    public void ANewTimelinesWait_IsNeverAStall()
    {
        var gap = new PresentGap(TimeSpan.FromMinutes(5), Held: FrameInterval, NewTimeline: true);

        Assert.False(gap.IsStall);
    }

    [Fact]
    public void TheWaitBeforeANewTimelinesFirstFrame_IsNotJudged()
    {
        var tracker = default(PresentGapTracker);

        (tracker, _) = tracker.Observe(At(0), FrameInterval, Ended); // the item's last frame
        var (_, gap) = tracker.Observe(At(0) + SatAtEnded, FrameInterval, Given);

        // The wait is still reported, so the sink can log it, but marked as the player moving on.
        Assert.Equal(new PresentGap(SatAtEnded, Held: FrameInterval, NewTimeline: true), gap);
        Assert.False(gap!.Value.IsStall);
    }

    [Fact]
    public void AFrameAfterANewTimelinesFirst_IsJudgedFromThatFrame()
    {
        // Starting over is not going quiet: the new timeline's first frame is the one the next
        // frame is judged against.
        var tracker = default(PresentGapTracker);
        var late = FrameInterval + TimeSpan.FromMilliseconds(600);

        (tracker, _) = tracker.Observe(At(0), Dwell, Ended);
        (tracker, _) = tracker.Observe(At(0) + SatAtEnded, FrameInterval, Given);
        var (_, gap) = tracker.Observe(At(0) + SatAtEnded + late, FrameInterval, Given);

        Assert.Equal(new PresentGap(late, Held: FrameInterval), gap);
        Assert.True(gap!.Value.IsStall);
    }

    [Fact]
    public void AFrameLateWithinOneTimeline_IsAStall()
    {
        var tracker = default(PresentGapTracker);

        (tracker, _) = tracker.Observe(At(0), FrameInterval, Given);
        var (_, gap) = tracker.Observe(At(0.9), FrameInterval, Given);

        Assert.Equal(new PresentGap(At(0.9), Held: FrameInterval), gap);
        Assert.True(gap!.Value.IsStall);
    }

    [Fact]
    public void AnUntrackedFrame_IsJudgedOnTheTimelineBeingFollowed()
    {
        var tracker = default(PresentGapTracker);

        (tracker, _) = tracker.Observe(At(0), FrameInterval, Ended);
        var (_, gap) = tracker.Observe(At(0) + SatAtEnded, FrameInterval, Untracked);

        Assert.True(gap!.Value.IsStall);
    }

    [Fact]
    public void TheTimelineAnUntrackedFrameIsJudgedOn_IsTheOneBeingFollowed()
    {
        var tracker = default(PresentGapTracker);

        Assert.Equal(TimelineId.None, tracker.Timeline);
        (tracker, _) = tracker.Observe(At(0), FrameInterval, Ended);
        Assert.Equal(Ended, tracker.Timeline);
        (tracker, _) = tracker.Observe(At(1), FrameInterval, Untracked);
        Assert.Equal(Ended, tracker.Timeline);
        (tracker, _) = tracker.Observe(At(2), FrameInterval, Given);
        Assert.Equal(Given, tracker.Timeline);
    }

    [Fact]
    public void TheFirstTimelineNamed_IsAdoptedWithoutStartingOver()
    {
        // Frames that carried no timeline, then one that does: the tracker had nothing to leave.
        var tracker = default(PresentGapTracker);

        (tracker, _) = tracker.Observe(At(0), FrameInterval, Untracked);
        var (_, gap) = tracker.Observe(At(0) + SatAtEnded, FrameInterval, Given);

        Assert.True(gap!.Value.IsStall);
    }

    [Fact]
    public void AnUntrackedFrame_DoesNotMakeTheTrackerForgetItsTimeline()
    {
        var tracker = default(PresentGapTracker);

        (tracker, _) = tracker.Observe(At(0), FrameInterval, Ended);
        (tracker, _) = tracker.Observe(At(0) + FrameInterval, FrameInterval, Untracked);
        var (_, gap) = tracker.Observe(At(0) + SatAtEnded, FrameInterval, Given);

        Assert.True(gap!.Value.NewTimeline);
        Assert.False(gap.Value.IsStall);
    }

    [Fact]
    public void TimelinesThatInterleave_AreNeverReportedAsStalls()
    {
        // A sink receives frames in the player's order, so this does not arise. Were it to, each
        // change would only start the tracker over: a wait goes unjudged, and none reads as a stall.
        var tracker = default(PresentGapTracker);
        var gaps = new List<PresentGap?>();

        foreach (var (arrival, timeline) in new[] { (At(0), Ended), (At(2), Given), (At(4), Ended), (At(6), Given) })
        {
            (tracker, var gap) = tracker.Observe(arrival, FrameInterval, timeline);
            gaps.Add(gap);
        }

        Assert.All(gaps.Skip(1), g => Assert.True(g!.Value.NewTimeline));
        Assert.All(gaps.Skip(1), g => Assert.False(g!.Value.IsStall));
    }

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
}
