using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A playlist reports its loops on <see cref="IPlaybackController.LoopRestarted"/>, with a count of
/// consecutive loops of the current item: decision 5 of <c>docs/adr/looping-on-both-players.md</c>,
/// validation rows 9 to 13.
/// </summary>
/// <remarks>
/// <para>
/// These tests wait on real playback, which is why they are in this suite (ADR-0072 rule 6). Each
/// wait completes on a signal; the bound only stops a failing run.
/// </para>
/// <para>
/// A test that asserts no loop was reported waits for a later transition first. The session performs
/// an input's actions in order before it takes the next input, so by then every loop report from the
/// earlier hand-offs is in the controller's channel, and a no-op command sent after it is dispatched
/// after them.
/// </para>
/// <para>
/// A loop that ends while paused is rebuilt and reported when its warm-up completes. That needs an
/// end-of-stream to arrive after a pause took effect, which real playback cannot be made to produce
/// on demand, so <c>PlaylistSessionProtocolTests.ALoopThatRebuilds_ReportsWhenTheItemIsBackAtItsStart</c>
/// pins it instead.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlaylistLoopReportingTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string ShortClip = "test-subsecond.mp4";
    private const string LongClip = "test-video-h264-yuv420p.mp4";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public PlaylistLoopReportingTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlaylistOfOneUnderAll_ReportsEachLoop_AndItsWrap()
    {
        await using var run = PlaylistRun.Create([Source(ShortClip)], RepeatMode.All);

        await run.PlayAsync();
        await run.WhenLoops(2).WaitAsync(Bound);

        Assert.Equal([1, 2], run.Loops.Take(2));
        Assert.All(run.Transitions.Skip(1), t => Assert.True(t.Wrapped));
        Assert.Empty(run.Errors);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlaylistOfOneUnderOne_ReportsEachLoop()
    {
        await using var run = PlaylistRun.Create([Source(ShortClip)], RepeatMode.One);

        await run.PlayAsync();
        await run.WhenLoops(2).WaitAsync(Bound);

        Assert.Equal([1, 2], run.Loops.Take(2));
        Assert.All(run.Transitions.Skip(1), t => Assert.False(t.Wrapped));
        Assert.Empty(run.Errors);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task HandOffsUnderAll_ReportNoLoop_EvenBetweenItemsOfOneSource()
    {
        // [a, a] of one source object: each hand-off is a replay decision, but to another item.
        var shared = Source(ShortClip);
        await AssertHandOffsReportNoLoopAsync([shared, shared]);

        await AssertHandOffsReportNoLoopAsync([Source(ShortClip), Source(ShortClip)]);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASkip_ReportsNoLoop_AndTheNextLoopCountsFromOne()
    {
        await using var run = PlaylistRun.Create([Source(LongClip)], RepeatMode.All);

        await run.PlayAsync();
        await run.WhenLoops(1).WaitAsync(Bound);

        // Right after a loop, a whole pass is left before the next end, so the skip is handled first.
        var transitionsAtSkip = run.Transitions.Count;
        run.Coordinator.RequestSkip();
        await run.WhenLoops(2).WaitAsync(Bound);

        Assert.Equal([1, 1], run.Loops.Take(2));
        Assert.True(
            run.Transitions.Count >= transitionsAtSkip + 2,
            $"Expected the skip's transition before the second loop; transitions: {run.Transitions.Count}."
        );
        Assert.Empty(run.Errors);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AFaultsRebuild_ReportsNoLoop_AndTheNextLoopCountsFromOne()
    {
        // The first chain throws on its 21st frame. Under All the only item is rebuilt, which is a
        // rebuild after a failure, and then plays to its end, which is its first loop.
        var faults = new FaultInjector(breaks: chain => chain == 0);
        await using var run = PlaylistRun.Create([Source(LongClip)], RepeatMode.All, faults.Configure);

        await run.PlayAsync();
        await run.WhenLoops(1).WaitAsync(Bound);

        // The load's transition, the fault's rebuild and the loop.
        Assert.True(run.Transitions.Count >= 3, $"Transitions: {run.Transitions.Count}.");
        Assert.Equal(1, run.Loops[0]);
        Assert.Contains(run.Errors, InjectedFault.Caused);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AJump_EndsTheCount_SoTheNextItemsFirstLoopIsOne()
    {
        var first = Source(LongClip);
        var second = Source(LongClip);
        await using var run = PlaylistRun.Create([first, second], RepeatMode.One);

        await run.PlayAsync();
        await run.WhenLoops(2).WaitAsync(Bound);

        // A whole pass is left before the first item's next end, so the jump is taken first.
        var secondIsCurrent = run.Transitioned(second);
        Assert.Equal(JumpRequest.Pending, run.Coordinator.RequestJump(run.Coordinator.Snapshot().Playlist[1]));
        await secondIsCurrent.WaitAsync(Bound);
        await run.WhenLoops(3).WaitAsync(Bound);

        Assert.Equal([1, 2, 1], run.Loops.Take(3));
        Assert.Empty(run.Errors);
    }

    private static async Task AssertHandOffsReportNoLoopAsync(IMediaSource[] items)
    {
        await using var run = PlaylistRun.Create(items, RepeatMode.All);

        await run.PlayAsync();
        // The load's transition, two hand-offs, and one more so both hand-offs' actions are done.
        await run.WhenTransitions(4).WaitAsync(Bound);
        Assert.True((await run.Controller.SetRepeatModeAsync(RepeatMode.All)).IsSuccess);

        Assert.Empty(run.Loops);
        Assert.Empty(run.Errors);
    }

    private static IMediaSource Source(string clip)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(clip);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }
}
