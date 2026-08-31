using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Coverage of the
/// <see cref="FrameFlow.Playback.PlaybackController"/>
/// against the same corpus files
/// <see cref="PlayToCompletionTests"/> exercises against the old
/// controller. Proves the new controller is a drop-in replacement
/// for the Load → Play → Ended happy path on real FFmpeg decoders.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> This file mirrors a subset of
/// <see cref="PlayToCompletionTests"/> — the smaller subset that
/// the new controller can run today without leaning on surfaces
/// that haven't been ported yet (pull-mode channels, the
/// pipeline-configurator diagnostics rollup, etc.). The shape is
/// identical: load a corpus file, play to completion, assert the
/// terminal state and basic frame/audio counts.
/// </para>
/// <para>
/// <b>What this does not assert.</b> Some
/// <c>PipelineDiagnosticsSnapshot</c> fields are still stubbed in the
/// rollup from <see cref="FrameFlow.Playback.SubstrateSession"/>, so the
/// corpus-expectation comparisons that read them are not made. They are not
/// made anywhere else either. What is pinned: state transitions, a non-zero
/// frame count, and EOS firing.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlayToCompletionTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public PlayToCompletionTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task H264Aac_Mp4_PlayToCompletion()
    {
        await AssertCorpusPlayToCompletionAsync("test-av-h264-aac.mp4");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task VideoOnly_H264_PlayToCompletion()
    {
        await AssertCorpusPlayToCompletionAsync("test-video-h264-yuv420p.mp4");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AudioOnly_Aac_PlayToCompletion()
    {
        await AssertCorpusPlayToCompletionAsync("test-audio-aac.m4a");
    }

    private async Task AssertCorpusPlayToCompletionAsync(string filename)
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(filename);
        Assert.True(filePath is not null, $"Corpus file {filename} not found.");

        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var endedTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var errors = new List<PlaybackError>();
            var transitions = new List<StateTransition<PlaybackState>>();

            using var stateSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    transitions.Add(t);
                    if (
                        t.Current
                        is PlaybackState.Ended
                            or PlaybackState.Error
                            or PlaybackState.Unloaded
                    )
                    {
                        endedTcs.TrySetResult();
                    }
                })
            );
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(errors.Add)
            );

            var source = MediaSource.FromFile(filePath!);

            var load = await controller.LoadAsync(source);
            Assert.True(
                load.IsSuccess,
                $"{filename}: LoadAsync should have succeeded ({load.Error?.Category}: {load.Error?.Message})."
            );

            var play = await controller.PlayAsync();
            Assert.True(
                play.IsSuccess,
                $"{filename}: PlayAsync should have succeeded ({play.Error?.Category}: {play.Error?.Message})."
            );

            // Wait for terminal state (Ended/Error/Unloaded) with a
            // generous budget — the substrate's pacing is wallclock
            // bound, so 3s media takes ~3s plus startup.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await endedTcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(
                    $"{filename}: Timed out waiting for terminal state. state={controller.State}, "
                        + $"position={controller.Position}, "
                        + $"video_consumed={videoSink.FrameCount}, audio_decoded={audioSink.DecodedDurationSeconds:F3}s, "
                        + $"errors={errors.Count}, transitions=[{string.Join(", ", transitions.Select(t => $"{t.Previous}->{t.Current}"))}]."
                );
            }

            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Empty(errors);
            Assert.Contains(
                transitions,
                t => t.Previous == PlaybackState.Playing && t.Current == PlaybackState.Ended
            );

            // Verify dataflow actually ran. We don't compare against
            // the corpus-expectation frame count here — the new
            // substrate's pacing + gate semantics may shift exact
            // counts by 1-2 between passes, and the lifecycle
            // contract is what this file is pinning.
            var info = controller.MediaInfo;
            Assert.NotNull(info);

            if (info!.VideoStreams.Count > 0)
            {
                await videoSink.WaitForDrainAsync();
                Assert.True(
                    videoSink.FrameCount > 0,
                    $"{filename}: expected video frames to be consumed; got {videoSink.FrameCount}."
                );
                Assert.True(
                    videoSink.IsPtsMonotonic,
                    $"{filename}: video PTS monotonicity violated."
                );
            }

            if (info.AudioStreams.Count > 0)
            {
                Assert.True(
                    audioSink.DecodedDurationSeconds > 0,
                    $"{filename}: expected audio buffers to be consumed; got {audioSink.DecodedDurationSeconds:F3}s."
                );
            }

            await IntegrationTestHelper
                .StabilizeForDisposeAsync(controller, audioSink, videoSink)
                .ConfigureAwait(false);
        }
    }
}
