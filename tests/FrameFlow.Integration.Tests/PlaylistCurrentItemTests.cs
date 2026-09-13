using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// The playlist controller's <see cref="IPlaybackController.Duration"/> and
/// <see cref="IPlaybackController.MediaInfo"/> follow the current item, and so does its
/// loop-stall watchdog (#183).
/// </summary>
/// <remarks>
/// These tests wait on real playback, which is why they are in this suite (ADR-0072 rule 6).
/// Each wait completes on a signal; the bound only stops a failing run.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlaylistCurrentItemTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string ShortClip = "test-subsecond.mp4";
    private const string LongClip = "test-video-h264-yuv420p.mp4";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public PlaylistCurrentItemTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AfterAnAdvance_DurationAndMediaInfo_DescribeTheNewItem()
    {
        var shortItem = Source(ShortClip);
        var longItem = Source(LongClip);
        await using var run = PlaylistRun.Create([shortItem, longItem], RepeatMode.Off);

        var longIsCurrent = run.Transitioned(longItem);
        await run.PlayAsync();
        await longIsCurrent.WaitAsync(Bound);

        // The session posts the new item's metadata before it reports the transition, and
        // commands are dispatched in order, so a no-op command sent now completes after it.
        Assert.True((await run.Controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        var transition = run.Transitions[^1];
        Assert.Same(transition.MediaInfo, run.Controller.MediaInfo);
        Assert.Equal(transition.MediaInfo.Duration, run.Controller.Duration);
        Assert.True(
            run.Controller.Duration > TimeSpan.FromSeconds(2),
            $"Duration still describes the short item: {run.Controller.Duration}."
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RepeatOneOnALongerLaterItem_RaisesNoLoopStalled()
    {
        // The watchdog reports a stall when the position stays past the duration for two
        // seconds. Against the first item's half second, a healthy 3-second second item
        // crossed that line on every pass.
        var shortItem = Source(ShortClip);
        var longItem = Source(LongClip);
        await using var run = PlaylistRun.Create([shortItem, longItem], RepeatMode.Off);

        var stalls = 0;
        using var stallSub = run.Controller.LoopStalled.Subscribe(
            new ActionObserver<LoopStalled>(_ => Interlocked.Increment(ref stalls))
        );

        var longIsCurrent = run.Transitioned(longItem);
        await run.PlayAsync();
        await longIsCurrent.WaitAsync(Bound);

        // Loop the long item, as the player facade does: controller, then coordinator.
        Assert.True((await run.Controller.SetRepeatModeAsync(RepeatMode.One)).IsSuccess);
        run.Coordinator.RepeatMode = RepeatMode.One;

        // A same-clip loop reports a transition on each pass. Two passes after the first
        // is well past the point the stale duration tripped the watchdog.
        var passes = 0;
        var twoMorePasses = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var passSub = run.Coordinator.SourceTransitioned.Subscribe(
            new ActionObserver<PlaylistTransition>(t =>
            {
                if (ReferenceEquals(t.Source, longItem) && Interlocked.Increment(ref passes) == 2)
                    twoMorePasses.TrySetResult();
            })
        );
        await twoMorePasses.Task.WaitAsync(Bound);

        Assert.Equal(PlaybackState.Playing, run.Controller.State);
        Assert.Equal(0, Volatile.Read(ref stalls));
    }

    private static IMediaSource Source(string name)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(name);
        Assert.NotNull(path);
        return MediaSource.FromFile(path!);
    }
}
