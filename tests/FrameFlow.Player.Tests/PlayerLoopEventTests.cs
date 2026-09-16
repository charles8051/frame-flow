using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player.Tests;

/// <summary>
/// <see cref="IMediaPlayer.LoopRestarted"/> is the controller's event, handed out unprojected:
/// decision 5 of <c>docs/adr/ADR-0075-looping-on-both-players.md</c>, validation row 14. There is one player
/// type now, so there is one player to check.
/// </summary>
public sealed class PlayerLoopEventTests
{
    [Fact]
    public void LoopRestarted_IsTheControllersEvent()
    {
        var controller = new PlaylistPlayerQueueTests.StubController();

        IMediaPlayer player = new PlaylistMediaPlayerCore(
            controller,
            new PlaylistCoordinator([new MediaSource("a")], RepeatMode.All),
            audioSink: null,
            NullLogger.Instance
        );

        Assert.Same(controller.LoopRestarted, player.LoopRestarted);
    }
}
