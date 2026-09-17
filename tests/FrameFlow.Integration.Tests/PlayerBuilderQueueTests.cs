using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// The builder over a queue. <c>FrameFlowPlayer.Create</c> takes one source or many, and
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

    // Bounds a failing wait only; every wait here completes on a signal.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public PlayerBuilderQueueTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheQueueEntry_BuildsAPlayerOverEveryItem()
    {
        var first = SourceOf(Clip);
        var second = SourceOf(Clip);

        await using var player = await FrameFlowPlayer.Create().WithMedia([first, second]).BuildPlayerAsync();

        var queue = player.GetPlaylist().Playlist;
        Assert.Equal(2, queue.Count);
        Assert.Same(first, queue[0].Source);
        Assert.Same(second, queue[1].Source);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheSingleSourceEntry_BuildsAQueueOfOne()
    {
        var source = SourceOf(Clip);

        await using var player = await FrameFlowPlayer.Create().WithMedia(source).BuildPlayerAsync();

        var only = Assert.Single(player.GetPlaylist().Playlist);
        Assert.Same(source, only.Source);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheQueueEntry_CarriesTheChainsOptions()
    {
        await using var player = await FrameFlowPlayer
            .Create()
            .WithMedia([SourceOf(Clip), SourceOf(Clip)])
            .WithRepeatMode(RepeatMode.All)
            .BuildPlayerAsync();

        Assert.Equal(RepeatMode.All, player.GetDiagnostics().RepeatMode);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task WithMedia_Replaces_RatherThanAppending()
    {
        // Every other With* on the builder replaces. Appending would let a chain name two sources
        // and still offer BuildAsync, which a session cannot honour.
        var second = SourceOf(Clip);

        await using var player = await FrameFlowPlayer
            .Create()
            .WithMedia(SourceOf(Clip))
            .WithMedia(second)
            .BuildPlayerAsync();

        var only = Assert.Single(player.GetPlaylist().Playlist);
        Assert.Same(second, only.Source);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task NoSourceAtAll_BuildsAPlayerWithAnEmptyQueue()
    {
        await using var player = await FrameFlowPlayer.Create().BuildPlayerAsync();

        Assert.Empty(player.GetPlaylist().Playlist);
        Assert.Null(player.CurrentSource);
        Assert.Equal(PlaybackState.Idle, player.State);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerBuiltEmpty_StartsWhatWasAddedToIt()
    {
        var source = SourceOf(Clip);
        await using var player = await FrameFlowPlayer.Create().BuildPlayerAsync();

        var started = new TaskCompletionSource<PlaylistTransition>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sub = player.SourceTransitioned.Subscribe(t => started.TrySetResult(t));

        await player.AddAsync(source);
        var play = await player.PlayAsync();

        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
        Assert.Same(source, (await started.Task.WaitAsync(Bound)).Item.Source);
        Assert.Same(source, player.CurrentSource);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerBuiltEmpty_RefusesToPlayNothing()
    {
        await using var player = await FrameFlowPlayer.Create().BuildPlayerAsync();

        var play = await player.PlayAsync();

        Assert.False(play.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, play.Error.Category);
        Assert.Equal(PlaybackState.Idle, player.State);
    }

    private static IMediaSource SourceOf(string clip)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }
}
