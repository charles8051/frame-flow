using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// States what the diagnostics counters must say after a straight play-to-completion, and
/// fails the run when they say otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Every counter here was already being computed and already being ignored. The pipeline
/// counts shed packets, decode errors, dropped frames and sync drops; <c>DiagnosticsSurfaceTests</c>
/// asserted the surface was <i>populated</i>, and nothing anywhere asserted a value. A run could
/// shed a third of its packets and the suite passed (#140).
/// </para>
/// <para>
/// <b>Why zero is the right expectation.</b> These are 3-second 320x240 clips decoded in
/// software with no inference stage. Nothing in a nominal run has a reason to shed a packet,
/// drop a frame for sync, or fail a decode. A clip that legitimately cannot be decoded in real
/// time belongs in a separately marked test with its own stated budget, not in here behind a
/// loose threshold that makes the gate meaningless.
/// </para>
/// <para>
/// <b>What is deliberately not asserted.</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>VideoPresentationLag</c>. It is wall-clock derived, so a bound on it is a timing
///     assertion, and a loaded CI runner would fail it on a tree with no defect in it — the
///     failure mode that #148 and #78 were both filed about. The integer counters below say
///     whether the pipeline paid to keep up; the lag says how the runner was feeling.
///   </description></item>
///   <item><description>
///     <c>UnderrunCount</c> and <c>BackpressureEvents</c>. <see cref="HarnessAudioSink"/>
///     accepts every block immediately and has no device behind it, so it cannot starve and
///     cannot push back; both are structurally zero and asserting on them would be a test that
///     can never fail. The counters that can move belong to <c>OpenAlAudioSink</c>, and
///     reaching them from an integration run is #146.
///   </description></item>
/// </list>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class NominalRunHealthTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public NominalRunHealthTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task H264Aac_Mp4_RunsClean()
    {
        await AssertNominalRunIsHealthyAsync("test-av-h264-aac.mp4");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task VideoOnly_H264_RunsClean()
    {
        await AssertNominalRunIsHealthyAsync("test-video-h264-yuv420p.mp4");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AudioOnly_Aac_RunsClean()
    {
        await AssertNominalRunIsHealthyAsync("test-audio-aac.m4a");
    }

    private static async Task AssertNominalRunIsHealthyAsync(string filename)
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(filename);
        Assert.True(filePath is not null, $"Corpus file {filename} not found.");

        var expectation = IntegrationTestHelper.GetCorpusExpectation(filename);
        Assert.True(expectation is not null, $"No corpus expectation recorded for {filename}.");

        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new List<PlaybackError>();

            using var stateSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (
                        t.Current
                        is PlaybackState.Ended
                            or PlaybackState.Error
                            or PlaybackState.Unloaded
                    )
                    {
                        ended.TrySetResult();
                    }
                })
            );
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(errors.Add)
            );

            var load = await controller.LoadAsync(MediaSource.FromFile(filePath!));
            Assert.True(load.IsSuccess, $"{filename}: LoadAsync failed ({load.Error?.Message}).");

            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess, $"{filename}: PlayAsync failed ({play.Error?.Message}).");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await ended.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"{filename}: timed out waiting for a terminal state.");
            }

            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Empty(errors);

            var info = controller.MediaInfo;
            Assert.NotNull(info);
            if (info!.VideoStreams.Count > 0)
                await videoSink.WaitForDrainAsync();

            var pipeline = controller.GetDiagnostics().Pipeline;
            var decoder = pipeline.Stream.VideoDecoder;

            // ── The decode side ──────────────────────────────────────────
            Assert.True(
                decoder.DecodeErrors == 0,
                $"{filename}: {decoder.DecodeErrors} decode errors on a nominal run."
            );
            Assert.True(
                decoder.PacketsDroppedForBackpressure == 0,
                $"{filename}: shed {decoder.PacketsDroppedForBackpressure} packets for backpressure "
                    + "on a nominal run. After #137 a shed costs the rest of its GOP, so this is "
                    + "frame loss, not just a slow patch."
            );

            if (expectation!.ExpectedVideoFrames is { } expectedFrames)
            {
                // The decoder sees every frame in the file. Unlike a presented-frame count this
                // is not subject to pacing, so it is an equality rather than a range.
                Assert.True(
                    decoder.FramesDecoded == expectedFrames,
                    $"{filename}: decoded {decoder.FramesDecoded} frames, expected {expectedFrames} "
                        + "from tests/corpus/test-expectations.json."
                );
            }

            // ── The presentation side ────────────────────────────────────
            if (info.VideoStreams.Count > 0)
            {
                Assert.True(
                    pipeline.VideoSink.FramesPresented == decoder.FramesDecoded,
                    $"{filename}: the pipeline decoded {decoder.FramesDecoded} frames and handed "
                        + $"{pipeline.VideoSink.FramesPresented} to the sink. Every decoded frame "
                        + "should reach the sink on a run that sheds nothing and drops nothing."
                );
                // VideoSink.FramesDropped is deliberately not asserted. For HarnessVideoSink
                // it counts the double's own pump losing a race with the next arrival against
                // its single slot, which moves with machine load and says nothing about the
                // pipeline. It flickered between 0 and 1 across runs of this very test.
                Assert.True(
                    pipeline.VideoFramesDroppedForSync == 0,
                    $"{filename}: dropped {pipeline.VideoFramesDroppedForSync} frames for sync. "
                        + "The clock ran past frames the pipeline had already decoded."
                );
            }

            // ── The audio side ───────────────────────────────────────────
            if (info.AudioStreams.Count > 0)
            {
                Assert.True(
                    pipeline.AudioSink.BlocksWritten > 0,
                    $"{filename}: the rollup reports no audio blocks written."
                );
            }

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }
}
