using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Plays the decode-pressure fixture and asserts that whatever the pipeline does under load,
/// it accounts for it.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of <see cref="NominalRunHealthTests"/>. That one states what a clip
/// with no reason to drop anything must look like; this one takes a clip that costs real decode
/// work and asserts the weaker, more important property — nothing goes missing silently.
/// </para>
/// <para>
/// <b>Why a separate fixture was needed.</b> Every other video entry in the corpus is 320x240
/// or 1920x1080 landscape H.264 built from flat <c>testsrc2</c>, which encodes to almost
/// nothing and decodes nearly for free. #134 reproduced on a 720x1280 portrait HEVC clip and
/// did not reproduce on 640x360 H.264; the variable between them was decode cost per unit of
/// wall time. <c>test-portrait-hevc-pressure.mp4</c> is that shape, and it reaches a state the
/// rest of the corpus does not — on the machine this was written on it decodes all 90 frames
/// and drops one for sync, where every 320x240 clip drops none.
/// </para>
/// <para>
/// <b>What is not asserted, and why the difference from the nominal gate matters.</b> Zero
/// shed and zero sync drops are the wrong demand here: the fixture exists precisely to push the
/// pipeline to where it may legitimately do both, and how far it gets depends on the machine.
/// Asserting zero would make this a wall-clock test wearing a counter's clothes. What holds at
/// any speed is that a frame is either presented, or counted on its way out, and that a decode
/// shortfall has a shed recorded against it. #134 was not "the counters were wrong" — every
/// counter was correct and nothing compared them to each other.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DecodePressureTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string PressureClip = "test-portrait-hevc-pressure.mp4";

    private readonly FfmpegBootstrapFixture _fixture;

    public DecodePressureTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    // libkvazaar is LGPL and in the pinned runtimes, but a Homebrew or PATH FFmpeg need not
    // carry it. The generator reports this fixture UNAVL there rather than failing, so this
    // test has to skip on the file rather than on the corpus as a whole.
    [RequiresCorpusFileFact(
        PressureClip,
        "It needs libkvazaar. Run: dotnet run scripts/generate-test-corpus.cs"
    )]
    public async Task UnderPressure_EveryFrameIsAccountedFor()
    {
        var filePath = IntegrationTestEnvironment.GetCorpusFile(PressureClip)!;

        var expectation = IntegrationTestHelper.GetCorpusExpectation(PressureClip);
        Assert.True(expectation is not null, $"No corpus expectation for {PressureClip}.");

        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var errors = new List<PlaybackError>();
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(errors.Add)
            );

            var (load, play) = await IntegrationTestHelper.PlayToCompletionAsync(
                controller,
                MediaSource.FromFile(filePath)
            );

            Assert.True(load.IsSuccess, $"LoadAsync failed ({load.Error?.Message}).");
            Assert.True(play.IsSuccess, $"PlayAsync failed ({play.Error?.Message}).");
            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Empty(errors);

            await videoSink.WaitForDrainAsync();

            var pipeline = controller.GetDiagnostics().Pipeline;
            var decoder = pipeline.Stream.VideoDecoder;

            // Pressure is not an excuse for a corrupt decode.
            Assert.True(
                decoder.DecodeErrors == 0,
                $"{decoder.DecodeErrors} decode errors under pressure."
            );

            // The file's frames are fully accounted for on the decode side: each was decoded,
            // shed for backpressure, or dropped while resynchronising to the next keyframe
            // after a shed (#137). Not "a shed exists" — the shortfall has to be explained
            // exactly, or a counter under-reporting by one would pass. Frames disappearing with
            // every counter reading zero is the #134 shape.
            //
            // This sums packets with frames, which is sound for this stream because an MP4
            // video sample is one coded frame whatever the decode order. The demux count below
            // checks that rather than assuming it: the clip is video-only, so every packet read
            // is a video frame.
            var expected = expectation!.ExpectedVideoFrames!.Value;
            Assert.True(
                pipeline.Stream.Demux.PacketsRead == expected,
                $"demuxed {pipeline.Stream.Demux.PacketsRead} packets from a video-only clip of "
                    + $"{expected} frames, so packets and frames do not correspond one to one "
                    + "here and the accounting below would not be sound."
            );

            var decodeAccounted =
                decoder.FramesDecoded
                + decoder.PacketsDroppedForBackpressure
                + decoder.PacketsDroppedToGopResync;
            Assert.True(
                decodeAccounted == expected,
                $"{expected} frames in the file, {decodeAccounted} accounted for — "
                    + $"{decoder.FramesDecoded} decoded, {decoder.PacketsDroppedForBackpressure} "
                    + $"shed, {decoder.PacketsDroppedToGopResync} dropped to GOP resync."
            );

            // Every decoded frame either reached the sink or was counted leaving.
            var accountedFor =
                pipeline.VideoSink.FramesPresented + pipeline.VideoFramesDroppedForSync;
            Assert.True(
                accountedFor == decoder.FramesDecoded,
                $"decoded {decoder.FramesDecoded} frames but accounted for {accountedFor} — "
                    + $"{pipeline.VideoSink.FramesPresented} presented plus "
                    + $"{pipeline.VideoFramesDroppedForSync} dropped for sync."
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }
}
