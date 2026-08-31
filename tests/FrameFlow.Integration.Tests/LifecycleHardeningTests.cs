using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Regression coverage for two latent-bug fixes called out in docs/DEFERRED_WORK.md:
/// (1) the playback clock now pauses on entry to Ended, so reported
/// Position no longer climbs forever past Duration; and (2) the audio
/// sink is no longer activated for sources that have no decodable
/// audio stream.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LifecycleHardeningTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public LifecycleHardeningTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task VideoOnlyFile_AudioSinkIsNotActivated()
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(
            "test-video-h264-yuv420p.mp4"
        );
        Assert.True(filePath is not null, "Corpus file test-video-h264-yuv420p.mp4 not found.");

        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile(filePath!);
            var (load, play) = await IntegrationTestHelper.PlayToCompletionAsync(
                controller,
                source
            );
            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
            Assert.Equal(PlaybackState.Ended, controller.State);

            Assert.Equal(0, audioSink.ActivateCount);
            Assert.Equal(0, audioSink.PauseCount);
            Assert.Equal(0, audioSink.ResumeCount);
            Assert.False(audioSink.IsActive);

            await IntegrationTestHelper
                .StabilizeForDisposeAsync(controller, audioSink, videoSink)
                .ConfigureAwait(false);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task PositionIsStableAfterEnded()
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(
            "test-video-h264-yuv420p.mp4"
        );
        Assert.True(filePath is not null, "Corpus file test-video-h264-yuv420p.mp4 not found.");

        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile(filePath!);
            var (load, play) = await IntegrationTestHelper.PlayToCompletionAsync(
                controller,
                source
            );
            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");
            Assert.Equal(PlaybackState.Ended, controller.State);

            var duration = controller.Duration;
            Assert.True(duration > TimeSpan.Zero, "Duration should be positive.");

            // Sample twice across a 1-second window. With the clock paused
            // on Ended, both samples should be identical (the EOS path
            // calls _clock.Pause()). Without the fix, the second sample
            // would be ~1s greater than the first and well past Duration.
            var positionAtEnded = controller.Position;
            await Task.Delay(TimeSpan.FromSeconds(1));
            var positionOneSecondLater = controller.Position;

            Assert.Equal(positionAtEnded, positionOneSecondLater);
            Assert.True(
                positionOneSecondLater <= duration,
                $"Position {positionOneSecondLater} climbed past Duration {duration} "
                    + "after Ended — playback clock failed to pause."
            );

            await IntegrationTestHelper
                .StabilizeForDisposeAsync(controller, audioSink, videoSink)
                .ConfigureAwait(false);
        }
    }
}
