using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// One factory over one source or many. Both overloads of <c>MediaPlayer.CreateAsync</c> build the
/// same player, so they must agree on what an omitted option means.
/// </summary>
/// <remarks>
/// The factory bootstraps FFmpeg and loads the first item, which is why these are here
/// (ADR-0072 rule 6). Nothing plays; the assertions read the player the factory returns.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlayerFactoryTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-subsecond.mp4";

    public PlayerFactoryTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task BothOverloads_DefaultToTheSameRepeatMode()
    {
        // The queue overload used to default to All, so the same omitted argument meant "play
        // once" over one source and "loop forever" over two.
        await using var one = await MediaPlayer.CreateAsync(SourceOf(Clip));
        await using var many = await MediaPlayer.CreateAsync([SourceOf(Clip), SourceOf(Clip)]);

        Assert.Equal(RepeatMode.Off, one.GetDiagnostics().RepeatMode);
        Assert.Equal(RepeatMode.Off, many.GetDiagnostics().RepeatMode);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheQueueOverload_TakesTheRepeatModeItIsGiven()
    {
        await using var player = await MediaPlayer.CreateAsync(
            [SourceOf(Clip), SourceOf(Clip)],
            initialRepeatMode: RepeatMode.All
        );

        Assert.Equal(RepeatMode.All, player.GetDiagnostics().RepeatMode);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OneSource_IsAQueueOfOne()
    {
        await using var player = await MediaPlayer.CreateAsync(SourceOf(Clip));

        var only = Assert.Single(player.GetPlaylist().Playlist);
        Assert.NotNull(only.Source);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnEmptyQueue_BuildsAPlayerThatHasNothingLoaded()
    {
        await using var player = await MediaPlayer.CreateAsync(Array.Empty<IMediaSource>());

        Assert.Empty(player.GetPlaylist().Playlist);
        Assert.Equal(PlaybackState.Idle, player.State);
    }

    private static IMediaSource SourceOf(string clip)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }
}
