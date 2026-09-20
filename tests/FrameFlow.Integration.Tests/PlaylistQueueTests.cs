using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// The playlist queue over real playback (#171): jumps, removal of an item still opening, a
/// skip under <see cref="RepeatMode.One"/>, and Play from <see cref="PlaybackState.Ended"/> over
/// an item that can no longer be opened. The ordering rules themselves are unit-tested in
/// <c>PlaylistCoordinatorTests</c>.
/// </summary>
/// <remarks>
/// These tests wait on real playback, which is why they are in this suite (ADR-0072 rule 6).
/// Each wait completes on a signal; the bound only stops a failing run. The numbers in the test
/// comments are the Validation rows of <c>docs/adr/ADR-0074-playlist-queue-model.md</c>.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlaylistQueueTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // The clip presents 72 frames. A pending jump is also taken when the item before it ends,
    // so a test that a jump took effect at once checks that the item before it presented fewer
    // than half of those.
    private const int HalfTheClip = 36;

    public PlaylistQueueTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task JumpWhilePaused_MakesTheItemCurrent_WithoutPlayingIt()
    {
        // Row 19.
        var (a, b, c) = (ClipSource(), ClipSource(), ClipSource());
        await using var run = PlaylistRun.Create([a, b, c], RepeatMode.Off);
        await run.PlayAsync();
        await run.Sink.WhenPresented(10).WaitAsync(Bound);
        Assert.True((await run.Controller.PauseAsync()).IsSuccess);

        var cIsCurrent = run.Transitioned(c);
        Assert.Equal(JumpRequest.Pending, run.Coordinator.RequestJump(ItemOf(run, c)));
        await cIsCurrent.WaitAsync(Bound);

        Assert.Equal(PlaybackState.Paused, run.Controller.State);
        Assert.Equal(TimeSpan.Zero, run.Controller.Position);

        var framesAtJump = run.Sink.Presented;
        Assert.True((await run.Controller.PlayAsync()).IsSuccess);
        await run.Sink.WhenPresented(framesAtJump + 1).WaitAsync(Bound);
        Assert.Equal([a, c], run.Transitions.Select(t => t.Item.Source));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task JumpAtEnded_ThenPlay_PlaysTheTarget()
    {
        // Row 20, first half. Without the jump, Play from Ended would start again from a.
        var (a, b, c) = (ClipSource(), ClipSource(), ClipSource());
        await using var run = PlaylistRun.Create([a, b, c], RepeatMode.Off);
        await PlayToEndAsync(run);

        Assert.Equal(JumpRequest.Pending, run.Coordinator.RequestJump(ItemOf(run, b)));
        var bAgain = run.Transitioned(b);
        Assert.True((await run.Controller.PlayAsync()).IsSuccess);
        await bAgain.WaitAsync(Bound);

        Assert.Equal([a, b, c, b], run.Transitions.Select(t => t.Item.Source));
        Assert.Empty(run.Errors);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task JumpAtEnded_ThenSeekAndPlay_PlaysTheTarget()
    {
        // Row 20, second half. The seek moves to Paused on the kept item, and the Play that
        // resumes it takes the jump.
        var (a, b, c) = (ClipSource(), ClipSource(), ClipSource());
        await using var run = PlaylistRun.Create([a, b, c], RepeatMode.Off);
        await PlayToEndAsync(run);

        Assert.Equal(JumpRequest.Pending, run.Coordinator.RequestJump(ItemOf(run, b)));
        Assert.True((await run.Controller.SeekAsync(TimeSpan.Zero)).IsSuccess);
        Assert.Equal(PlaybackState.Paused, run.Controller.State);

        var bAgain = run.Transitioned(b);
        var framesAtPlay = run.Sink.Presented;
        Assert.True((await run.Controller.PlayAsync()).IsSuccess);
        await bAgain.WaitAsync(Bound);

        Assert.Equal([a, b, c, b], run.Transitions.Select(t => t.Item.Source));
        // The kept item did not play through first.
        Assert.InRange(run.Sink.Presented - framesAtPlay, 0, HalfTheClip - 1);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task JumpDuringAnAdvance_TakesTheTargetStraightAfterTheAdvancedItemStarts()
    {
        // Row 21. Hold a skip's advance inside the gate, after it has taken b and before b
        // starts, and jump back to a. Under Off, b would otherwise be followed by c, so a second
        // transition to a can only come from the jump.
        var (a, b, c) = (ClipSource(), ClipSource(), ClipSource());
        using var clock = new HoldableClock();
        await using var run = PlaylistRun.Create([a, b, c], RepeatMode.Off, clock: clock);
        await run.PlayAsync();
        await run.Sink.WhenPresented(10).WaitAsync(Bound);

        var held = clock.HoldNextStop();
        run.Coordinator.RequestSkip();
        await held.WaitAsync(Bound);

        var aAgain = run.Transitioned(a);
        Assert.Equal(JumpRequest.Pending, run.Coordinator.RequestJump(ItemOf(run, a)));
        var framesAtJump = run.Sink.Presented;
        clock.Release();
        await aAgain.WaitAsync(Bound);

        Assert.Equal([a, b, a], run.Transitions.Select(t => t.Item.Source));
        Assert.InRange(run.Sink.Presented - framesAtJump, 0, HalfTheClip - 1);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task JumpJustAfterAnItemStarts_TakesTheTarget()
    {
        // Row 21, the other ordering: the jump is recorded after the advance has looked for one,
        // so the jump's own request takes it.
        var (a, b, c) = (ClipSource(), ClipSource(), ClipSource());
        await using var run = PlaylistRun.Create([a, b, c], RepeatMode.Off);
        await run.PlayAsync();
        await run.Sink.WhenPresented(10).WaitAsync(Bound);

        var bIsCurrent = run.Transitioned(b);
        run.Coordinator.RequestSkip();
        await bIsCurrent.WaitAsync(Bound);

        var aAgain = run.Transitioned(a);
        var framesAtJump = run.Sink.Presented;
        Assert.Equal(JumpRequest.Pending, run.Coordinator.RequestJump(ItemOf(run, a)));
        await aAgain.WaitAsync(Bound);

        Assert.Equal([a, b, a], run.Transitions.Select(t => t.Item.Source));
        Assert.InRange(run.Sink.Presented - framesAtJump, 0, HalfTheClip - 1);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PlayFromEnded_OverAFirstItemThatCanNoLongerBeOpened_PlaysTheNext()
    {
        // Row 23. The replay's first item fails to open. It is reported and passed over, as an
        // advance passes over one; before, a failed replay load put the player in Error.
        var copy = Path.Combine(Path.GetTempPath(), $"frameflow-queue-{Guid.NewGuid():N}.mp4");
        File.Copy(ClipPath(), copy);
        try
        {
            var removable = MediaSource.FromFile(copy);
            var b = ClipSource();
            await using var run = PlaylistRun.Create([removable, b], RepeatMode.Off);
            await PlayToEndAsync(run);

            File.Delete(copy);

            var bAgain = run.Transitioned(b);
            Assert.True((await run.Controller.PlayAsync()).IsSuccess);
            await bAgain.WaitAsync(Bound);

            Assert.NotEqual(PlaybackState.Error, run.Controller.State);
            var error = Assert.Single(run.Errors);
            Assert.Contains("could not be started", error.Message);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SkipUnderOne_PlaysTheNextItem_WhichThenRepeats()
    {
        // Row 24. Under One a skip used to restart the current item.
        var (a, b) = (ClipSource(), ClipSource());
        await using var run = PlaylistRun.Create([a, b], RepeatMode.One);
        await run.PlayAsync();
        await run.Sink.WhenPresented(10).WaitAsync(Bound);

        var bIsCurrent = run.Transitioned(b);
        run.Coordinator.RequestSkip();
        await bIsCurrent.WaitAsync(Bound);

        // A same-source loop raises the transition again when b repeats.
        var bRepeats = run.Transitioned(b);
        await bRepeats.WaitAsync(Bound);

        Assert.Equal([a, b, b], run.Transitions.Select(t => t.Item.Source));
        Assert.Equal(PlaybackState.Playing, run.Controller.State);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RemovingAnItemStillOpening_LetsItPlay_ThenContinuesAfterIt()
    {
        // Row 26. Hold a skip's advance after it has taken b and before b starts, and remove b.
        var (a, b, c) = (ClipSource(), ClipSource(), ClipSource());
        using var clock = new HoldableClock();
        await using var run = PlaylistRun.Create([a, b, c], RepeatMode.Off, clock: clock);
        await run.PlayAsync();
        await run.Sink.WhenPresented(10).WaitAsync(Bound);
        var bItem = ItemOf(run, b);

        var held = clock.HoldNextStop();
        run.Coordinator.RequestSkip();
        await held.WaitAsync(Bound);

        var opening = run.Coordinator.Snapshot();
        Assert.Same(bItem, opening.Current);
        Assert.False(opening.CurrentStarted);
        Assert.True(run.Coordinator.Remove(bItem));

        var bStarts = run.Transitioned(b);
        var cStarts = run.Transitioned(c);
        clock.Release();
        await bStarts.WaitAsync(Bound);
        await cStarts.WaitAsync(Bound);

        Assert.Equal([a, b, c], run.Transitions.Select(t => t.Item.Source));
        Assert.DoesNotContain(bItem, run.Coordinator.Snapshot().Playlist);
    }

    private static async Task PlayToEndAsync(PlaylistRun run)
    {
        var ended = run.Settled(PlaybackState.Ended);
        await run.PlayAsync();
        await ended.WaitAsync(Bound);
    }

    private static PlaylistItem ItemOf(PlaylistRun run, IMediaSource source) =>
        run.Coordinator.Snapshot().Playlist.Single(i => ReferenceEquals(i.Source, source));

    private static string ClipPath()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        return path!;
    }

    private static IMediaSource ClipSource() => MediaSource.FromFile(ClipPath());
}
