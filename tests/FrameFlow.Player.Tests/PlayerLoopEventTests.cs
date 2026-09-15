using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player.Tests;

/// <summary>
/// <see cref="IMediaPlayer.LoopRestarted"/> is the controller's event on both players: decision 5 of
/// <c>docs/adr/looping-on-both-players.md</c>, validation row 14.
/// </summary>
public sealed class PlayerLoopEventTests
{
    [Fact]
    public void LoopRestarted_IsTheControllersEvent_OnBothPlayers()
    {
        var controller = new PlaylistPlayerQueueTests.StubController();

        IMediaPlayer single = new MediaPlayerCore(controller, audioSink: null, ownedProvider: null, NullLogger.Instance);
        IMediaPlayer playlist = new PlaylistMediaPlayerCore(
            controller,
            new PlaylistCoordinator([new MediaSource("a")], RepeatMode.All),
            audioSink: null,
            NullLogger.Instance
        );

        Assert.Same(controller.LoopRestarted, single.LoopRestarted);
        Assert.Same(controller.LoopRestarted, playlist.LoopRestarted);
    }
}
