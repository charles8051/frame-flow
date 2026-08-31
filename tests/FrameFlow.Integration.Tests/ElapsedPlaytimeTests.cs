using System.Diagnostics;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Pacing-accuracy coverage for the
/// controller. Asserts the wall-clock elapsed time from Playing →
/// Ended is within tolerance of the file duration, proving the
/// <see cref="FrameFlow.Playback.PaceUntil"/> operator throttles
/// frames correctly against the master clock.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ElapsedPlaytimeTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public ElapsedPlaytimeTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PacedPlayback_ElapsedTimeMatchesFileDuration()
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

            // Playing → Ended elapsed should be within tolerance of the
            // file duration. The substrate paces video against the
            // wallclock source (when no IClockSource audio sink), so a
            // 3-second file should take ~3 seconds.
            var elapsed = Stopwatch.GetElapsedTime(
                Volatile.Read(ref playingTimestamp),
                Volatile.Read(ref endedTimestamp)
            );
            var expectedDuration = TimeSpan.FromSeconds(expected.DurationSeconds);
            var tolerance = IntegrationTestHelper.WallClockTolerance(expected.DurationSeconds);
            var delta = (elapsed - expectedDuration).Duration();

            Assert.True(
                delta <= tolerance,
                $"Playing→Ended elapsed {elapsed.TotalSeconds:F3}s outside tolerance of "
                    + $"{expectedDuration.TotalSeconds:F3}s ±{tolerance.TotalMilliseconds:F0}ms."
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }
}
