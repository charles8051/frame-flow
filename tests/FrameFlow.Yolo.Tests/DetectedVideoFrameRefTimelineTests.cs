using FrameFlow.Graph;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Yolo.Tests;

/// <summary>A detection result rides on its frame's timeline (ADR-0084, decision 3).</summary>
public sealed class DetectedVideoFrameRefTimelineTests
{
    [Fact]
    public void TheItem_IsOnItsFramesTimeline()
    {
        var frame = DerivedItemConformance.VideoInput();

        using var item = new DetectedVideoFrameRef(frame, []);

        Assert.NotEqual(TimelineId.None, frame.Timeline);
        Assert.Equal(frame.Timeline, ((IFrame)item).Timeline);
    }
}
