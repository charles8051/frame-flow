using System.Diagnostics;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Loop-video coverage for the
/// controller. Pins the lifecycle-level loop contract: event count and
/// ordering, frames flowed on each pass, and a wall-clock elapsed bound.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this does not assert.</b> Total delivered frames (consumed
/// + dropped) against a corpus-expected pass count. Pacing through bounded channels shifts the consumed/dropped
/// split from pass to pass, so an exact per-pass frame count is not a
/// stable assertion. The pins kept here:
/// loop events fire with correct counts (1, 2, 3), the video sink
/// keeps receiving frames after each restart, and wall-clock
/// elapsed is ≥ 3× single-file duration minus a small tolerance.
/// The decoded-audio-duration comparison is also unasserted, for the same
/// reason: multi-loop sample accumulation depends on session and decoder
/// pause semantics the substrate accounts for differently. Neither
/// comparison is made anywhere else — the tests that used to make them were
/// deleted with the old controller. Tracked in #113.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LoopVideoTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public LoopVideoTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RepeatOne_ThreeLoops_VideoFramesFlowEachPass()
    {
        var videoSink = new PtsRecordingVideoSink();
        var (controller, audioSink) = IntegrationTestHelper.CreateController(videoSink);
        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            var loopEvents = new List<LoopRestarted>();
            var threeLoopsTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var sub = controller.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(e =>
                {
                    loopEvents.Add(e);
                    if (loopEvents.Count >= 3)
                        threeLoopsTcs.TrySetResult();
                })
            );

            var loadResult = await controller.LoadAsync(source);
            Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");

            await controller.SetRepeatModeAsync(RepeatMode.One);

            var sw = Stopwatch.StartNew();
            var playResult = await controller.PlayAsync();
            Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await threeLoopsTcs.Task.WaitAsync(cts.Token);

            var stopResult = await controller.UnloadAsync();
            Assert.True(stopResult.IsSuccess, $"Unload failed: {stopResult.Error}");
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink);
            sw.Stop();

            // Loop event assertions: 3 restarts with incrementing counts.
            Assert.True(
                loopEvents.Count >= 3,
                $"Expected >= 3 loop events, got {loopEvents.Count}"
            );
            Assert.Equal(1, loopEvents[0].LoopCount);
            Assert.Equal(2, loopEvents[1].LoopCount);
            Assert.Equal(3, loopEvents[2].LoopCount);

            // Each loop pass must have shown video frame motion before the next
            // restart fires. Exact counts are not pinned: the pacer splits
            // presented/dropped differently from pass to pass. What must hold is
            // that frames flowed.
            AssertEachPassPresented(videoSink, loopEvents.Count);

            // Wall-clock floor: 3 loops × file duration minus a small tolerance.
            var expected = IntegrationTestHelper
                .LoadExpectations()
                .First(e => e.Filename == "test-av-h264-aac.mp4");
            var singleDuration = expected.DurationSeconds;
            // Wall-clock minimum: 3 loops × file duration minus a 2 s
            // tolerance. The post-Phase-4 substrate's EOS dispatch
            // (from the decoder's IAsyncEnumerable completing) fires
            // ~200-400 ms BEFORE the sink-side last-frame presentation
            // wallclock; over 3 loops that compounds to ~600-1200 ms,
            // with significant run-to-run variance (observed 7.2-8.4 s
            // on the same commit). The test asserts the *liveness*
            // contract (loops happen + frames flow), not millisecond
            // timing. The structural fix (sink-side EOS notification)
            // is filed in docs/DEFERRED_WORK.md under "Video-only loop pacing fires
            // EOS ~400 ms early per loop" — until that lands, this
            // tolerance keeps the test honest about the assertion's
            // actual purpose.
            var minWallClock =
                TimeSpan.FromSeconds(3.0 * singleDuration) - TimeSpan.FromSeconds(2);
            Assert.True(
                sw.Elapsed >= minWallClock,
                $"Wall-clock elapsed {sw.Elapsed.TotalSeconds:F3}s < minimum {minWallClock.TotalSeconds:F3}s "
                    + $"(3 loops × {singleDuration:F3}s - 2 s tolerance; see docs/DEFERRED_WORK.md)"
            );
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RepeatOne_VideoOnly_ThreeLoops_FramesFlowEachPass()
    {
        var videoSink = new PtsRecordingVideoSink();
        var (controller, audioSink) = IntegrationTestHelper.CreateController(videoSink);
        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-video-h264-yuv420p.mp4")!
            );

            var loopEvents = new List<LoopRestarted>();
            var threeLoopsTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var sub = controller.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(e =>
                {
                    loopEvents.Add(e);
                    if (loopEvents.Count >= 3)
                        threeLoopsTcs.TrySetResult();
                })
            );

            var loadResult = await controller.LoadAsync(source);
            Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");

            await controller.SetRepeatModeAsync(RepeatMode.One);

            var sw = Stopwatch.StartNew();
            var playResult = await controller.PlayAsync();
            Assert.True(playResult.IsSuccess, $"Play failed: {playResult.Error}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await threeLoopsTcs.Task.WaitAsync(cts.Token);

            var stopResult = await controller.UnloadAsync();
            Assert.True(stopResult.IsSuccess, $"Unload failed: {stopResult.Error}");
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink);
            sw.Stop();

            // Loop event assertions: 3 restarts with incrementing counts.
            Assert.True(
                loopEvents.Count >= 3,
                $"Expected >= 3 loop events, got {loopEvents.Count}"
            );
            Assert.Equal(1, loopEvents[0].LoopCount);
            Assert.Equal(2, loopEvents[1].LoopCount);
            Assert.Equal(3, loopEvents[2].LoopCount);

            // Frames flowed before each restart.
            AssertEachPassPresented(videoSink, loopEvents.Count);

            // Wall-clock floor: 3 loops × file duration minus a small tolerance.
            var expected = IntegrationTestHelper
                .LoadExpectations()
                .First(e => e.Filename == "test-video-h264-yuv420p.mp4");
            var singleDuration = expected.DurationSeconds;
            // Wall-clock minimum: 3 loops × file duration minus a 2 s
            // tolerance. The post-Phase-4 substrate's EOS dispatch
            // (from the decoder's IAsyncEnumerable completing) fires
            // ~200-400 ms BEFORE the sink-side last-frame presentation
            // wallclock; over 3 loops that compounds to ~600-1200 ms,
            // with significant run-to-run variance (observed 7.2-8.4 s
            // on the same commit). The test asserts the *liveness*
            // contract (loops happen + frames flow), not millisecond
            // timing. The structural fix (sink-side EOS notification)
            // is filed in docs/DEFERRED_WORK.md under "Video-only loop pacing fires
            // EOS ~400 ms early per loop" — until that lands, this
            // tolerance keeps the test honest about the assertion's
            // actual purpose.
            var minWallClock =
                TimeSpan.FromSeconds(3.0 * singleDuration) - TimeSpan.FromSeconds(2);
            Assert.True(
                sw.Elapsed >= minWallClock,
                $"Wall-clock elapsed {sw.Elapsed.TotalSeconds:F3}s < minimum {minWallClock.TotalSeconds:F3}s "
                    + $"(3 loops × {singleDuration:F3}s - 2 s tolerance; see docs/DEFERRED_WORK.md)"
            );
        }
    }

    /// <summary>
    /// Asserts that each pass a loop ended presented frames, found where the presented PTS go
    /// back. Not by counting in a <see cref="LoopRestarted"/> observer: the event is raised a
    /// command hop after the rewind, so a count read there can take in the next pass's frames.
    /// Every pass presents its newest frame, so a pass that played shows here however the
    /// runner was scheduled.
    /// </summary>
    private static void AssertEachPassPresented(PtsRecordingVideoSink videoSink, int loops)
    {
        var presented = videoSink.PresentedPts;
        var passes = presented.Count == 0 ? 0 : 1;
        for (var i = 1; i < presented.Count; i++)
        {
            if (presented[i] < presented[i - 1])
                passes++;
        }

        Assert.True(
            passes >= loops,
            $"{loops} loops ended passes, but only {passes} passes presented frames."
        );
    }
}
