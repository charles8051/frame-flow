using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Media.Tests;

/// <summary>
/// <see cref="VideoFrameRef"/> is an <see cref="IFrame"/>, so a join that retains it must set a
/// lead bound (ADR-0080, decision 8).
/// </summary>
public sealed class SyncJoinFrameSecondaryTests
{
    [Fact]
    public void VideoFrameRefSecondary_WithoutALead_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SyncJoinNode<VideoFrameRef, VideoFrameRef, VideoFrameRef>(
                "frames",
                (primary, _, _) => ValueTask.FromResult<VideoFrameRef?>(primary),
                new SyncJoinKeys<VideoFrameRef, VideoFrameRef>(
                    p => p.Timestamp,
                    s => (s.Timestamp, s.Timestamp)
                ),
                SyncMatch.MostRecentAtOrBefore,
                window: TimeSpan.FromSeconds(1)
            )
        );

        Assert.Equal("maxLead", ex.ParamName);
    }
}
