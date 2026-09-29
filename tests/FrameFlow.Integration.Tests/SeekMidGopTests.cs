using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A seek into the middle of a GOP on a container that seeks by timestamp shows the frame
/// at the target, not the next keyframe (#495).
/// </summary>
/// <remarks>
/// <para>
/// The MPEG-TS demuxer has no index, so <c>av_seek_frame</c> lands on the packet nearest the
/// target whether or not it is a keyframe. The decoder drops everything from there to the
/// next keyframe, so the first frame the player showed was that keyframe, up to a GOP past
/// the target. On the clip below, a seek to 1.52 s showed 2.0 s first.
/// </para>
/// <para>
/// The clip has a keyframe a second, at 0, 1 and 2 s of media time, at 24 fps. Its container
/// timestamps start at 1.4 s, so the target is also a check that the search runs in media
/// time.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekMidGopTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-start-offset.ts";
    private const string HowToGenerate = "Run: dotnet run scripts/generate-test-corpus.cs";
    private const double FramesPerSecond = 24;

    private readonly FfmpegBootstrapFixture _fixture;

    public SeekMidGopTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresCorpusFileFact(Clip, HowToGenerate)]
    public async Task SeekingBetweenKeyframes_ShowsTheFrameAtTheTargetFirst()
    {
        var target = TimeSpan.FromSeconds(1.52);
        var firstAtTarget = TimeSpan.FromSeconds(37 / FramesPerSecond);
        var video = new PtsRecordingVideoSink();
        var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile(Clip)!);
            Assert.True((await controller.LoadAsync(source)).IsSuccess);

            // Seek before the first play, so every frame the sink records comes after it.
            Assert.True((await controller.SeekAsync(target)).IsSuccess);

            // Barrier on the controller's own end-of-playback transition, subscribed before
            // Play so it cannot be missed.
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(transition =>
                {
                    if (
                        transition.Current
                        is PlaybackState.Ended
                            or PlaybackState.Unloaded
                            or PlaybackState.Error
                    )
                        ended.TrySetResult();
                })
            );

            Assert.True((await controller.PlayAsync()).IsSuccess);

            // The timeout only bounds a failure.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ended.Task.WaitAsync(cts.Token);
            Assert.Equal(PlaybackState.Ended, controller.State);

            var presented = video.PresentedPts;
            Assert.NotEmpty(presented);
            Assert.True(
                Math.Abs((presented[0] - firstAtTarget).TotalMilliseconds) < 1,
                $"the first frame after seeking to {target.TotalSeconds:F2}s presented at "
                    + $"{presented[0].TotalSeconds:F4}s, not at "
                    + $"{firstAtTarget.TotalSeconds:F4}s. The seek landed mid-GOP and the "
                    + "decoder dropped the frames up to the next keyframe."
            );
        }
    }
}
