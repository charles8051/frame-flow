using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player.Tests;

/// <summary>
/// One player type: the player a single source builds is the one a playlist builds, over a queue
/// of one. Decisions 1 and 2 of <c>docs/adr/ADR-0077-one-player-type.md</c>, validation row 1.
/// </summary>
/// <remarks>
/// <see cref="MediaPlayer"/> needs FFmpeg and a real source, so the wiring is checked
/// here over the same core it builds, with a stub controller and a coordinator of one item.
/// <c>SingleSourceAsAQueueOfOneTests</c> drives the factory itself over real playback.
/// </remarks>
public sealed class OnePlayerTypeTests
{
    [Fact]
    public void ThePlayerOverOneSource_CarriesThePlaylistSurface()
    {
        var source = new MediaSource("a");
        var player = NewPlayer(source);

        Assert.IsAssignableFrom<IMediaPlayer>(player);
        var queued = Assert.Single(player.GetPlaylist().Playlist);
        Assert.Same(source, queued.Source);
        Assert.Same(source, player.CurrentSource ?? queued.Source);
    }

    [Fact]
    public async Task ThePlayerOverOneSource_TakesASecondSource()
    {
        // A caller who started with one file adds another to the player they already hold, which
        // is what one player type buys them.
        var player = NewPlayer(new MediaSource("a"));

        var added = await player.AddAsync(new MediaSource("b"));

        Assert.Equal(2, player.GetPlaylist().Playlist.Count);
        Assert.Same(added, player.GetPlaylist().Playlist[1]);
    }

    private static IMediaPlaylistPlayer NewPlayer(IMediaSource source) =>
        new PlaylistMediaPlayerCore(
            new PlaylistPlayerQueueTests.StubController(),
            new PlaylistCoordinator([source], RepeatMode.Off),
            audioSink: null,
            NullLogger.Instance
        );
}
