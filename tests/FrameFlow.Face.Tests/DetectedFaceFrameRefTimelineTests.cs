using FrameFlow.Graph;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>A face detection result rides on its frame's timeline (ADR-0084, decision 3).</summary>
public sealed class DetectedFaceFrameRefTimelineTests
{
    [Fact]
    public void TheItem_IsOnItsFramesTimeline()
    {
        var frame = DerivedItemConformance.VideoInput();

        using var item = new DetectedFaceFrameRef(frame, []);

        Assert.NotEqual(TimelineId.None, frame.Timeline);
        Assert.Equal(frame.Timeline, ((IFrame)item).Timeline);
    }
}
