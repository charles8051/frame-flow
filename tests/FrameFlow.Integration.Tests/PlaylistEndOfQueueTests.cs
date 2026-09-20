using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// The playlist player at the end of its queue (#170). The last item that played to its end, or
/// was skipped, is kept at <see cref="PlaybackState.Ended"/>, so it can be sought and played
/// again and its counters can be read. Play from Ended with nothing queued is refused and leaves
/// the player in Ended.
/// </summary>
/// <remarks>
/// These tests wait on real playback, which is why they are in this suite (ADR-0072 rule 6).
/// Each wait completes on a signal and the bound only stops a failing run, except where a test
/// says it observes for a fixed time.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlaylistEndOfQueueTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public PlaylistEndOfQueueTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeekThenPlayFromEnded_PlaysTheLastItemAgain(bool asSingleSource)
    {
        // The last item used to be disposed before the end-of-stream was reported, so the seek
        // and the play found nothing, and the player said Playing while nothing decoded.
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.Off, asSingleSource: asSingleSource);
        await PlayToEndAsync(run);

        await SeekThenPlayToEndAsync(run);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekThenPlayFromEnded_OnATwoItemPlaylist_PlaysTheSecondItemAgain()
    {
        var first = ClipSource();
        var second = ClipSource();
        await using var run = PlaylistRun.Create([first, second], RepeatMode.Off);
        await PlayToEndAsync(run);

        await SeekThenPlayToEndAsync(run);

        // The seek replays the item that ended the queue. It is not a new hand-off.
        Assert.Equal([first, second], run.Transitions.Select(t => t.Item.Source));
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticsAtEnded_DescribeTheLastItem(bool asSingleSource)
    {
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.Off, asSingleSource: asSingleSource);
        await PlayToEndAsync(run);

        var decoded = run.Controller.GetDiagnostics().Pipeline.Stream.VideoDecoder.FramesDecoded;
        Assert.True(decoded > 0, $"Decoded frames at Ended: {decoded}");
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeekThenPlayAfterSkippingTheLastItem_PlaysIt(bool asSingleSource)
    {
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.Off, asSingleSource: asSingleSource);
        await run.PlayAsync();
        await run.Sink.WhenPresented(10).WaitAsync(Bound);

        var ended = run.Settled(PlaybackState.Ended);
        run.Coordinator.RequestSkip();
        await ended.WaitAsync(Bound);

        await SeekThenPlayToEndAsync(run);
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipOnTheLastItem_StopsPresentation(bool asSingleSource)
    {
        // The skipped item is kept at Ended, so it must also be paused. Kept without a pause it
        // went on presenting after Ended. Nothing signals that frames have stopped, so this
        // observes the sink for a fixed time. It cannot fail on a correct build, whatever the
        // machine's speed; a slow machine only makes it less likely to catch the fault.
        await using var run = PlaylistRun.Create([ClipSource()], RepeatMode.Off, asSingleSource: asSingleSource);
        await run.PlayAsync();
        await run.Sink.WhenPresented(10).WaitAsync(Bound);

        var ended = run.Settled(PlaybackState.Ended);
        run.Coordinator.RequestSkip();
        await ended.WaitAsync(Bound);

        var framesAtEnded = run.Sink.Presented;
        await Task.Delay(TimeSpan.FromSeconds(1));

        // A frame already on its way to the sink when the item paused may still land.
        Assert.InRange(run.Sink.Presented, framesAtEnded, framesAtEnded + 1);
        Assert.Equal(PlaybackState.Ended, run.Controller.State);
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayFromEndedWithNothingQueued_StartsThePlaylistAgain(bool asSingleSource)
    {
        // The playlist keeps its items (#171), so Play from Ended with nothing queued starts it
        // again from its first item. Before #170 this faulted into Error; #170 refused it.
        var first = ClipSource();
        await using var run = PlaylistRun.Create([first], RepeatMode.Off, asSingleSource: asSingleSource);
        await PlayToEndAsync(run);

        var firstAgain = run.Transitioned(first);
        var framesAtEnded = run.Sink.Presented;
        var play = await run.Controller.PlayAsync();

        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
        await firstAgain.WaitAsync(Bound);
        await run.Sink.WhenPresented(framesAtEnded + 1).WaitAsync(Bound);
        Assert.Empty(run.Errors);
        Assert.False(run.Transitions[^1].Wrapped);
    }

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayFromEndedWithAnItemQueued_PlaysIt(bool asSingleSource)
    {
        var first = ClipSource();
        var enqueued = ClipSource();
        await using var run = PlaylistRun.Create([first], RepeatMode.Off, asSingleSource: asSingleSource);
        await PlayToEndAsync(run);

        var enqueuedIsCurrent = run.Transitioned(enqueued);
        run.Coordinator.Enqueue(enqueued);
        var framesAtEnded = run.Sink.Presented;

        var play = await run.Controller.PlayAsync();

        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
        await enqueuedIsCurrent.WaitAsync(Bound);
        await run.Sink.WhenPresented(framesAtEnded + 1).WaitAsync(Bound);
        Assert.Equal(PlaybackState.Playing, run.Controller.State);
        Assert.Empty(run.Errors);
    }

    private static async Task PlayToEndAsync(PlaylistRun run)
    {
        var ended = run.Settled(PlaybackState.Ended);
        await run.PlayAsync();
        await ended.WaitAsync(Bound);
    }

    /// <summary>
    /// From Ended: seeks to the start, plays, and waits for the item to end again, checking
    /// that it presented more frames on the way.
    /// </summary>
    private static async Task SeekThenPlayToEndAsync(PlaylistRun run)
    {
        Assert.Equal(PlaybackState.Ended, run.Controller.State);
        var framesAtEnded = run.Sink.Presented;

        var seek = await run.Controller.SeekAsync(TimeSpan.Zero);
        Assert.True(seek.IsSuccess, $"Seek failed: {seek.Error?.Message}");
        Assert.Equal(PlaybackState.Paused, run.Controller.State);

        var endedAgain = run.Settled(PlaybackState.Ended);
        var play = await run.Controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        await run.Sink.WhenPresented(framesAtEnded + 10).WaitAsync(Bound);
        await endedAgain.WaitAsync(Bound);
        Assert.Empty(run.Errors);
    }

    private static IMediaSource ClipSource()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }
}
