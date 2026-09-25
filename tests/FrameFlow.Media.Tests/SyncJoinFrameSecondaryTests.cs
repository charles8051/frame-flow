using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Media.Tests;

/// <summary>
/// <see cref="IVideoFrame"/> is an <see cref="IFrame"/>, so a join that retains it must set a
/// lead bound (ADR-0080, decision 8).
/// </summary>
public sealed class SyncJoinFrameSecondaryTests
{
    [Fact]
    public void VideoFrameRefSecondary_WithoutALead_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SyncJoinNode<IVideoFrame, IVideoFrame, IVideoFrame>(
                "frames",
                (primary, _, _) => ValueTask.FromResult<IVideoFrame?>(primary),
                new SyncJoinKeys<IVideoFrame, IVideoFrame>(
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
