using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// The builder over a queue. <c>FrameFlowPlayer.Open</c> takes one source or many, and
/// <c>BuildPlayerAsync</c> hands back the queue surface either way, so the builder can express
/// everything <c>MediaPlayer.CreateAsync</c> can.
/// </summary>
/// <remarks>
/// The terminal bootstraps FFmpeg and loads the first item, which is why these are here
/// (ADR-0072 rule 6). Nothing plays; the assertions read the player the builder returns.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlayerBuilderQueueTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-subsecond.mp4";

    public PlayerBuilderQueueTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheQueueEntry_BuildsAPlayerOverEveryItem()
    {
        var first = SourceOf(Clip);
        var second = SourceOf(Clip);

        await using var player = await FrameFlowPlayer.Open([first, second]).BuildPlayerAsync();

        var queue = player.GetPlaylist().Playlist;
        Assert.Equal(2, queue.Count);
        Assert.Same(first, queue[0].Source);
        Assert.Same(second, queue[1].Source);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheSingleSourceEntry_BuildsAQueueOfOne()
    {
        var source = SourceOf(Clip);

        await using var player = await FrameFlowPlayer.Open(source).BuildPlayerAsync();

        var only = Assert.Single(player.GetPlaylist().Playlist);
        Assert.Same(source, only.Source);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheQueueEntry_CarriesTheChainsOptions()
    {
        await using var player = await FrameFlowPlayer
            .Open([SourceOf(Clip), SourceOf(Clip)])
            .WithRepeatMode(RepeatMode.All)
            .BuildPlayerAsync();

        Assert.Equal(RepeatMode.All, player.GetDiagnostics().RepeatMode);
    }

    [Fact]
    public void AnEmptyQueue_IsRefused()
    {
        var error = Assert.Throws<ArgumentException>(
            () => FrameFlowPlayer.Open(Array.Empty<IMediaSource>())
        );

        Assert.Equal("sources", error.ParamName);
    }

    private static IMediaSource SourceOf(string clip)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }
}
