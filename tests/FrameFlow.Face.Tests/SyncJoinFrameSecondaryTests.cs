using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>
/// <see cref="DetectedFaceFrameRef"/> carries a frame and is an <see cref="IFrame"/>, so a join that retains it
/// must set a lead bound (ADR-0080, decision 8).
/// </summary>
public sealed class SyncJoinFrameSecondaryTests
{
    [Fact]
    public void DetectedFaceFrameRefSecondary_WithoutALead_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SyncJoinNode<RefBox<int>, DetectedFaceFrameRef, RefBox<int>>(
                "detections",
                (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
                new SyncJoinKeys<RefBox<int>, DetectedFaceFrameRef>(
                    p => TimeSpan.FromMilliseconds(p.Value),
                    s => (s.Timestamp, s.Timestamp)
                ),
                SyncMatch.MostRecentAtOrBefore,
                window: TimeSpan.FromSeconds(1)
            )
        );

        Assert.Equal("maxLead", ex.ParamName);
    }
}
