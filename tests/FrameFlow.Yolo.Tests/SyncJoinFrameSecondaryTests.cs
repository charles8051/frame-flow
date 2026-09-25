using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// <see cref="DetectedVideoFrameRef"/> carries a frame and is an <see cref="IFrame"/>, so a join that retains it
/// must set a lead bound (ADR-0080, decision 8).
/// </summary>
public sealed class SyncJoinFrameSecondaryTests
{
    [Fact]
    public void DetectedVideoFrameRefSecondary_WithoutALead_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SyncJoinNode<RefBox<int>, DetectedVideoFrameRef, RefBox<int>>(
                "detections",
                (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
                new SyncJoinKeys<RefBox<int>, DetectedVideoFrameRef>(
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
