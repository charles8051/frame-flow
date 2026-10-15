using System.Diagnostics;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Pacing coverage for the controller. Asserts the wall-clock elapsed time from Playing →
/// Ended is no shorter than the file duration less a tolerance, proving the pacer
/// throttles frames against the master clock rather than presenting at decode speed.
/// </summary>
/// <remarks>
/// A floor only. A ceiling on the same interval measured the runner: a loaded machine takes
/// longer to play a clip on a correct build. That a due frame is presented when the clock
/// reaches it, and the clip ends when its last frame has finished displaying rather than
/// later, is pinned without a wall clock by <c>ClockSelectVideoSinkTests</c>
/// (<c>DeliversFramesInOrder_AtTheirClockTime</c>,
/// <c>WaitForDrain_CompletesOnlyAfterLastFrameFinishesDisplaying</c>), and that the wallclock's
/// waits end at their targets by <c>WallClockSourceManualTimeTests</c>.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ElapsedPlaytimeTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public ElapsedPlaytimeTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PacedPlayback_TakesAtLeastTheFileDuration()
    {
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );
            var expected = IntegrationTestHelper
                .LoadExpectations()
                .First(e => e.Filename == "test-av-h264-aac.mp4");

            var playingTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var endedTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            long playingTimestamp = 0;
            long endedTimestamp = 0;

            using var stateSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Playing)
                    {
                        Interlocked.CompareExchange(
                            ref playingTimestamp,
                            Stopwatch.GetTimestamp(),
                            0
                        );
                        playingTcs.TrySetResult();
                    }
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

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess);
            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess);

            using var endedCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await endedTcs.Task.WaitAsync(endedCts.Token);

            Assert.Equal(PlaybackState.Ended, controller.State);

            // Playing → Ended should take at least the file duration less a tolerance. The
            // substrate paces video against the wallclock source (when no IClockSource audio
            // sink), so a 3-second file takes ~3 seconds, and load only makes it longer.
            var elapsed = Stopwatch.GetElapsedTime(
                Volatile.Read(ref playingTimestamp),
                Volatile.Read(ref endedTimestamp)
            );
            var expectedDuration = TimeSpan.FromSeconds(expected.DurationSeconds);
            var tolerance = IntegrationTestHelper.WallClockTolerance(expected.DurationSeconds);

            Assert.True(
                elapsed >= expectedDuration - tolerance,
                $"Playing→Ended elapsed {elapsed.TotalSeconds:F3}s, under "
                    + $"{expectedDuration.TotalSeconds:F3}s -{tolerance.TotalMilliseconds:F0}ms."
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }
}
