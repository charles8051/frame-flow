using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Diagnostics-snapshot coverage for the
/// controller. Pins the rollup shape
/// (<see cref="IPlaybackController.GetDiagnostics"/>) returns
/// coherent values when called against an in-flight session backed
/// by <see cref="FrameFlow.Playback.SubstrateSession"/>.
/// </summary>
/// <remarks>
/// The new session synthesises the stream-level snapshot from raw
/// demux + decoder diagnostics rather than from a wrapped
/// <c>IDecodedMediaStream</c> (which doesn't exist on the new path).
/// This test verifies the synthesised snapshot has the expected
/// fields populated.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DiagnosticsSurfaceTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public DiagnosticsSurfaceTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task GetDiagnostics_BeforeLoad_ReturnsEmpty()
    {
        var (controller, _, _) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var snapshot = controller.GetDiagnostics();
            // No session yet — pipeline rollup is Empty.
            Assert.Equal(PlaybackState.Idle, snapshot.State);
            Assert.Equal(SeekState.NotSeeking, snapshot.SeekingState);
            Assert.Equal(RepeatMode.Off, snapshot.RepeatMode);
            Assert.Equal(TimeSpan.Zero, snapshot.Duration);
            Assert.Null(snapshot.MediaInfo);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task GetDiagnostics_AfterLoad_PopulatesMediaInfo()
    {
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );
            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess);

            var snapshot = controller.GetDiagnostics();
            Assert.Equal(PlaybackState.Paused, snapshot.State);
            Assert.NotNull(snapshot.MediaInfo);
            Assert.True(snapshot.Duration > TimeSpan.Zero);

            // Pipeline rollup: stream snapshot should have demux info.
            Assert.NotNull(snapshot.Pipeline);
            // Demux container name should be populated for an opened file.
            Assert.NotNull(snapshot.Pipeline.Stream.Demux);

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task GetDiagnostics_DuringPlayback_UpdatesDecoderCounters()
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
            using var sub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Playing)
                        playingTcs.TrySetResult();
                })
            );

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess);
            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await playingTcs.Task.WaitAsync(cts.Token);

            // Let some frames flow.
            await Task.Delay(500);

            var snapshot = controller.GetDiagnostics();
            // Either video or audio decoder should have produced frames
            // by now. Don't pin exact counters (those depend on pacing
            // + decode speed); just that the snapshot is coherent.
            Assert.NotNull(snapshot.Pipeline);
            Assert.True(
                snapshot.Pipeline.Stream.VideoDecoder.FramesDecoded > 0
                    || snapshot.Pipeline.Stream.AudioDecoder.BuffersDecoded > 0,
                "Expected at least one decoder to have produced frames after 500ms of playback."
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }
}
