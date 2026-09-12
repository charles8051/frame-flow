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
///     <c>VideoFramesDroppedForSync == 0</c>. It is derived from the same timing relationship as
///     the lag: a runner descheduled for longer than a frame interval makes the clock pass a
///     queued frame, and the pipeline legitimately counts a drop. The conservation check below
///     covers what this gate actually needs from it.
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
            var (load, play) = await IntegrationTestHelper.PlayToCompletionAsync(
                controller,
                MediaSource.FromFile(filePath!)
            );

            Assert.True(load.IsSuccess, $"{filename}: LoadAsync failed ({load.Error?.Message}).");
            Assert.True(play.IsSuccess, $"{filename}: PlayAsync failed ({play.Error?.Message}).");
            Assert.Equal(PlaybackState.Ended, controller.State);

            var info = controller.MediaInfo;
            Assert.NotNull(info);
            if (info!.VideoStreams.Count > 0)
                await videoSink.WaitForDrainAsync();

            var pipeline = controller.GetDiagnostics().Pipeline;
            var decoder = pipeline.Stream.VideoDecoder;

            // ── Decode correctness. Independent of how fast the machine is. ──
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
                // is not subject to pacing, so it is an equality rather than a range. It is also
                // what catches a shed indirectly: shed packets never reach the decoder.
                Assert.True(
                    decoder.FramesDecoded == expectedFrames,
                    $"{filename}: decoded {decoder.FramesDecoded} frames, expected {expectedFrames} "
                        + "from tests/corpus/test-expectations.json."
                );
            }

            // ── Conservation. Every decoded frame is accounted for. ──
            //
            // Not "nothing was dropped for sync". That counter moves when the playback clock
            // passes a queued frame, which a descheduled runner can cause on a tree with no
            // defect in it — the same wall-clock sensitivity that keeps VideoPresentationLag out
            // of this gate. What is asserted instead is that frames do not go missing silently:
            // a decoded frame either reached the sink or was counted on its way out. That holds
            // at any speed, and it is the class of bug this gate exists for — #134 was correct
            // counters and nothing checking them against each other.
            if (info.VideoStreams.Count > 0)
            {
                var accountedFor =
                    pipeline.VideoSink.FramesPresented + pipeline.VideoFramesDroppedForSync;
                Assert.True(
                    accountedFor == decoder.FramesDecoded,
                    $"{filename}: decoded {decoder.FramesDecoded} frames but accounted for "
                        + $"{accountedFor} — {pipeline.VideoSink.FramesPresented} presented plus "
                        + $"{pipeline.VideoFramesDroppedForSync} dropped for sync. The difference "
                        + "went missing with no counter recording it."
                );
            }

            // ── Audio actually flowed. ──
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
