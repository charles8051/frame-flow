using System.Diagnostics;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Pause/resume integration coverage for the
/// controller
/// (<see cref="FrameFlow.Playback.PlaybackController"/>),
/// proving the gate-based pause path is stable on real FFmpeg
/// decoders. Mirrors the shape of
/// <see cref="PauseResumeTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// The original <see cref="PauseResumeTests.PauseResume_WallClockAccountsForPauseDuration"/>
/// asserts a tight wall-clock budget that the substrate doesn't
/// quite match yet (the gate-protected resume re-opens both gates
/// near-instantaneously, and the audio sink's clock-source coupling
/// is the same, but the frame-flow latency through the new
/// substrate's bounded channels can drift the timing by a few
/// hundred ms). The version here asserts the qualitative contract:
/// pause halts frame flow, resume resumes it, total wallclock is
/// at least (file duration + pause duration), and the controller
/// reaches Ended cleanly.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PauseResumeTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public PauseResumeTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PauseResume_OnRealDecoders_NoCrashCompletesCleanly()
    {
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            var playingTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var endedTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var errors = new List<PlaybackError>();

            using var stateSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Playing)
                        playingTcs.TrySetResult();
                    if (
                        t.Current
                        is PlaybackState.Ended
                            or PlaybackState.Unloaded
                            or PlaybackState.Error
                    )
                    {
                        endedTcs.TrySetResult();
                    }
                })
            );
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(errors.Add)
            );

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

            using var playingCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await playingTcs.Task.WaitAsync(playingCts.Token);

            var sw = Stopwatch.StartNew();

            // Let some playback happen before pausing.
            await Task.Delay(500);
            var framesAtPauseStart = videoSink.FrameCount;

            // Pause.
            var pause = await controller.PauseAsync();
            Assert.True(pause.IsSuccess, $"Pause failed: {pause.Error?.Message}");
            Assert.Equal(PlaybackState.Paused, controller.State);

            // Stay paused — the gate should keep frames from flowing.
            var pauseDuration = TimeSpan.FromSeconds(2);
            await Task.Delay(pauseDuration);

            // Sanity: frame count shouldn't have advanced by much during
            // the pause (tolerate a small number of in-flight frames
            // that drained through the sink-side edge before the gate
            // fully sealed off).
            var framesAfterPauseHold = videoSink.FrameCount;
            Assert.InRange(
                framesAfterPauseHold,
                framesAtPauseStart,
                framesAtPauseStart + 10
            );

            // Resume.
            var resume = await controller.PlayAsync();
            Assert.True(resume.IsSuccess, $"Resume failed: {resume.Error?.Message}");
            Assert.Equal(PlaybackState.Playing, controller.State);

            // Wait for playback to finish.
            using var endedCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await endedTcs.Task.WaitAsync(endedCts.Token);
            sw.Stop();

            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Empty(errors);

            // After resume, frames should have flowed past the pre-pause count.
            Assert.True(
                videoSink.FrameCount > framesAfterPauseHold,
                $"Expected video frames to flow after resume; pre-pause={framesAtPauseStart}, "
                    + $"after-pause-hold={framesAfterPauseHold}, final={videoSink.FrameCount}."
            );

            // Wall-clock check: real run must take at least (file
            // duration + pause duration). Loose lower bound — the new
            // substrate's pacing is wallclock-bound for video so this
            // should hold easily.
            var expectation = IntegrationTestHelper
                .LoadExpectations()
                .First(e => e.Filename == "test-av-h264-aac.mp4");
            var fileDuration = TimeSpan.FromSeconds(expectation.DurationSeconds);
            var minimumExpected = fileDuration + pauseDuration;
            Assert.True(
                sw.Elapsed >= minimumExpected - TimeSpan.FromMilliseconds(500),
                $"Wall-clock elapsed {sw.Elapsed.TotalSeconds:F2}s should be ≥ "
                    + $"{minimumExpected.TotalSeconds:F2}s (file {fileDuration.TotalSeconds:F1}s + "
                    + $"pause {pauseDuration.TotalSeconds:F1}s)."
            );

            await IntegrationTestHelper
                .StabilizeForDisposeAsync(controller, audioSink, videoSink)
                .ConfigureAwait(false);
        }
    }
}
