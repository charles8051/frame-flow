using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A skip on the playlist player follows the controller's state (#182). It advances and plays
/// while <see cref="PlaybackState.Playing"/>, advances without playing while
/// <see cref="PlaybackState.Paused"/>, is dropped at <see cref="PlaybackState.Ended"/>, and waits
/// for the first play when the playlist has not played yet.
/// </summary>
/// <remarks>
/// These tests wait on real playback, which is why they are in this suite (ADR-0072 rule 6).
/// Each wait completes on a signal; the bound only stops a failing run.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlaylistSkipStateTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public PlaylistSkipStateTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SkipWhilePaused_MakesTheNextItemCurrent_WithoutPlayingIt()
    {
        var first = ClipSource();
        var second = ClipSource();
        await using var run = PlaylistRun.Create([first, second], RepeatMode.Off);
        await PlayThenPauseAsync(run);

        var secondIsCurrent = run.Transitioned(second);
        run.Coordinator.RequestSkip();
        await secondIsCurrent.WaitAsync(Bound);

        // The item's clock starts on its first play. An item that was played reads past zero
        // by the time its transition has been raised and observed.
        Assert.Equal(PlaybackState.Paused, run.Controller.State);
        Assert.Equal(TimeSpan.Zero, run.Controller.Position);

        // Play starts the item the skip made current.
        var framesAtSkip = run.Sink.Presented;
        Assert.True((await run.Controller.PlayAsync()).IsSuccess);
        await run.Sink.WhenPresented(framesAtSkip + 1).WaitAsync(Bound);
        Assert.Equal(PlaybackState.Playing, run.Controller.State);
        Assert.Equal([first, second], run.Transitions.Select(t => t.Item.Source));
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipWhilePausedOnTheLastItem_EndsThePlaylist(bool asSingleSource)
    {
        // Under Off a skip on the last item ends the playlist, and it must do so while paused
        // too. The end-of-stream it reports used to be dropped, because Paused had no
        // transition for it, and the player stayed Paused with nothing loaded.
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.Off, asSingleSource: asSingleSource);
        await PlayThenPauseAsync(run);

        var ended = run.Settled(PlaybackState.Ended);
        run.Coordinator.RequestSkip();
        await ended.WaitAsync(Bound);

        Assert.Equal(PlaybackState.Ended, run.Controller.State);
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipWhileEnded_IsDropped_AndPlayPlaysTheEnqueuedItem(bool asSingleSource)
    {
        // At Ended the queue has already ended, so a skip has nothing to end. It used to start
        // the next queued item while the state said Ended, which also took the item Play would
        // have started, so Play then found an empty queue.
        var first = ClipSource();
        var enqueued = ClipSource();
        await using var run = PlaylistRun.Create([first], RepeatMode.Off, asSingleSource: asSingleSource);
        var ended = run.Settled(PlaybackState.Ended);
        await run.PlayAsync();
        await ended.WaitAsync(Bound);

        var enqueuedIsCurrent = run.Transitioned(enqueued);
        run.Coordinator.Enqueue(enqueued);
        run.Coordinator.RequestSkip();

        var play = await run.Controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
        await enqueuedIsCurrent.WaitAsync(Bound);

        Assert.Equal(PlaybackState.Playing, run.Controller.State);
        Assert.Empty(run.Errors);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SkipBeforeTheFirstPlay_TakesEffectWhenPlayStarts()
    {
        var first = ClipSource();
        var second = ClipSource();
        await using var run = PlaylistRun.Create([first, second], RepeatMode.Off);
        await run.LoadAsync();
        Assert.Equal(PlaybackState.Paused, run.Controller.State);

        var secondIsCurrent = run.Transitioned(second);
        run.Coordinator.RequestSkip();
        Assert.True((await run.Controller.PlayAsync()).IsSuccess);
        await secondIsCurrent.WaitAsync(Bound);
        await run.Sink.WhenPresented(1).WaitAsync(Bound);

        Assert.Equal(PlaybackState.Playing, run.Controller.State);
        Assert.Equal([first, second], run.Transitions.Select(t => t.Item.Source));
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayQueuedBehindASkipThatEnds_LeavesTheSessionEnded(bool asSingleSource)
    {
        // Paused on the last item: skip, then play at once. The play is dispatched before the
        // end-of-stream the skip raises, so the controller goes Playing and then Ended. The
        // play reaches the session only after the skip has ended the queue, and must not mark
        // it playing again. If it did, a later enqueue and skip would start the item while
        // the state said Ended and take it from the queue, and Play would then find nothing.
        var first = ClipSource();
        var enqueued = ClipSource();
        using var clock = new HoldableClock();
        await using var run = PlaylistRun.Create([first], RepeatMode.Off, clock: clock, asSingleSource: asSingleSource);
        await PlayThenPauseAsync(run);

        // Hold the skip's advance inside its gate, where it pauses the clock while pausing the
        // item it keeps. Play is queued to the controller while it is held, so the controller
        // dispatches Play before the end-of-stream the advance goes on to raise, and the
        // session sees Play only after the advance has ended the queue.
        var ended = run.Settled(PlaybackState.Ended);
        var held = clock.HoldNextPause();
        run.Coordinator.RequestSkip();
        await held.WaitAsync(Bound);
        var queuedPlay = run.Controller.PlayAsync();
        clock.Release();
        _ = await queuedPlay;
        await ended.WaitAsync(Bound);

        var enqueuedIsCurrent = run.Transitioned(enqueued);
        run.Coordinator.Enqueue(enqueued);
        run.Coordinator.RequestSkip();

        var play = await run.Controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
        await enqueuedIsCurrent.WaitAsync(Bound);
        Assert.Equal(PlaybackState.Playing, run.Controller.State);
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipBeforeTheFirstPlay_UnderRepeatOne_PlaysTheItem(bool asSingleSource)
    {
        // Under One the skip replays the same item. It is taken by the first play, when the
        // item has never played, so it must start the item rather than rewind it in place:
        // a rewind of an item that never started leaves its clocks stopped, and frames trickle
        // out at the pacer's wait cap.
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.One, asSingleSource: asSingleSource);
        await run.LoadAsync();

        run.Coordinator.RequestSkip();
        Assert.True((await run.Controller.PlayAsync()).IsSuccess);
        await run.Sink.WhenPresented(15).WaitAsync(Bound);

        Assert.Equal(PlaybackState.Playing, run.Controller.State);
        Assert.True(run.Controller.Position > TimeSpan.Zero);
    }

    private static async Task PlayThenPauseAsync(PlaylistRun run)
    {
        await run.PlayAsync();
        await run.Sink.WhenPresented(10).WaitAsync(Bound);
        Assert.True((await run.Controller.PauseAsync()).IsSuccess);
        Assert.Equal(PlaybackState.Paused, run.Controller.State);
    }

    private static IMediaSource ClipSource()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }
}
