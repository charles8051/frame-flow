using System.Diagnostics;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A still image held on screen for a set time, through the demuxer options on
/// <see cref="IMediaSource"/> (#248).
/// </summary>
/// <remarks>
/// A still was already playable and not holdable: it decodes to one frame with no duration,
/// so under <see cref="RepeatMode.Off"/> the item ended the moment that frame was presented.
/// Opening it as <c>image2</c> with a <c>framerate</c> gives it a timeline, and the pacer's
/// end-of-content hold (#249) is what then keeps the frame up for it.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class StillImageDwellTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Still = "test-still.png";

    // Short enough to keep the suite quick, long enough that no scheduling latency accounts
    // for it: an unheld still ends in milliseconds.
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(1);

    /// <summary>How long the repeat test watches for. Long enough to separate a paced loop
    /// from an unpaced one by more than an order of magnitude.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private static IMediaSource Paced(string path, TimeSpan dwell) =>
        MediaSource.FromFile(path) with
        {
            InputFormat = "image2",
            DemuxerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["framerate"] = $"1/{dwell.TotalSeconds:0}",
            },
        };

    [RequiresFfmpegAndCorpusFact]
    public async Task APacedStill_StaysUpForItsDwell_ThenEnds()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Still);
        Assert.True(path is not null, $"Corpus file {Still} not found.");

        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var errors = new List<PlaybackError>();
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(errors.Add)
            );

            long playingTimestamp = 0;
            long endedTimestamp = 0;
            var endedTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            using var stateSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Playing)
                        Interlocked.CompareExchange(
                            ref playingTimestamp,
                            Stopwatch.GetTimestamp(),
                            0
                        );

                    if (
                        t.Current
                        is PlaybackState.Ended
                            or PlaybackState.Error
                            or PlaybackState.Unloaded
                    )
                    {
                        Interlocked.CompareExchange(
                            ref endedTimestamp,
                            Stopwatch.GetTimestamp(),
                            0
                        );
                        endedTcs.TrySetResult();
                    }
                })
            );

            var load = await controller.LoadAsync(Paced(path!, Dwell));
            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

            // The still is a clip of a known length before a frame of it has been decoded.
            Assert.Equal(Dwell, controller.Duration);

            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

            using var endedCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await endedTcs.Task.WaitAsync(endedCts.Token);

            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Empty(errors);

            // A lower bound only. A loaded runner makes the dwell longer, never shorter, so
            // this fails in the direction the regression lies in: an unheld still reaches
            // Ended in milliseconds. An upper bound would fail for the machine being busy,
            // which is not what is under test here.
            var elapsed = Stopwatch.GetElapsedTime(
                Volatile.Read(ref playingTimestamp),
                Volatile.Read(ref endedTimestamp)
            );
            var floor = Dwell - TimeSpan.FromMilliseconds(100);

            Assert.True(
                elapsed >= floor,
                $"Playing to Ended took {elapsed.TotalSeconds:F3}s for a {Dwell.TotalSeconds:F0}s "
                    + "still, so the frame was not held for its interval."
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AStillUnderRepeat_LoopsAtItsOwnRateRatherThanSpinning()
    {
        // The open question on #248: an item with no timeline under a repeating mode had
        // nothing to pace against, so it restarted as fast as the machine allowed rather
        // than displaying anything. Frames carrying their display interval (#249) settle it
        // without the queue needing a rule about zero-length items: even the probed still,
        // with no demuxer options at all, has a one-frame interval to be held for.
        var path = IntegrationTestEnvironment.GetCorpusFile(Still);
        Assert.True(path is not null, $"Corpus file {Still} not found.");

        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            int loops = 0;
            using var loopSub = controller.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(_ => Interlocked.Increment(ref loops))
            );

            await controller.SetRepeatModeAsync(RepeatMode.One);
            Assert.True((await controller.LoadAsync(MediaSource.FromFile(path!))).IsSuccess);

            var sw = Stopwatch.StartNew();
            Assert.True((await controller.PlayAsync()).IsSuccess);
            await Task.Delay(Window);
            sw.Stop();
            await controller.PauseAsync();

            var observed = Volatile.Read(ref loops);

            // An upper bound only, and it fails in the one direction that matters. Pacing is
            // clock-driven, so a faster machine cannot exceed the rate; a slower or busier
            // one produces fewer loops and still passes. Only pacing being absent puts the
            // count above the ceiling. The still probes to 25 fps, so the ceiling is four
            // times what a paced loop reaches over the window.
            var ceiling = (int)(sw.Elapsed.TotalSeconds * 25 * 4);

            Assert.True(
                observed <= ceiling,
                $"{observed} loop restarts in {sw.Elapsed.TotalSeconds:F2}s exceeds the {ceiling} "
                    + "a paced still can reach, so the item is restarting rather than displaying."
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AStillWithoutTheOptions_EndsAtOnce()
    {
        // The contrast, and the reason the test above is not measuring its own harness. The
        // same file probed the ordinary way carries no timeline, so it ends as fast as the
        // machine can present one frame.
        var path = IntegrationTestEnvironment.GetCorpusFile(Still);
        Assert.True(path is not null, $"Corpus file {Still} not found.");

        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var (load, play) = await IntegrationTestHelper.PlayToCompletionAsync(
                controller,
                MediaSource.FromFile(path!)
            );

            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Equal(TimeSpan.Zero, controller.Duration);

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }
}
