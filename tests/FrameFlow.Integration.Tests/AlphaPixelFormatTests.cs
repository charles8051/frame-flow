using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A source whose pixel format has four planes decodes (#341).
/// </summary>
/// <remarks>
/// <para>
/// The software conversion in <c>VideoDecoder.BuildManagedFrameFromCpu</c> handed
/// <c>sws_scale</c> three source plane pointers and a hardcoded null for the fourth.
/// swscale validates one pointer per plane the source format declares, so every
/// four-plane frame failed the check with "bad src image pointers" and converted
/// nothing. The formats that reach it are yuva420p and the other YUVA variants,
/// plus GBRAP.
/// </para>
/// <para>
/// The failure was silent in both directions. The demuxer read every packet and the
/// converter returned null for each, so the run reached
/// <see cref="PlaybackState.Ended"/> with no frames, no error raised, and
/// <c>DecodeErrors</c> at zero. Playback of an entire file produced nothing and
/// reported nothing, which is why the assertions below are on counts rather than on
/// a comparison: <see cref="ReferenceDecoder"/> drives the same decoders, so it
/// returned zero frames too and matched the broken capture exactly.
/// </para>
/// <para>
/// The fixture is ffv1 rather than the animated WebP that surfaced this. Two things
/// differ between an animated WebP and the still WebP that always worked — the
/// decoder and the pixel format — and only one of them is the cause. ffv1 holds the
/// decoder constant against
/// <c>test-video-h264-yuv420p.mp4</c>-shaped fixtures and varies the plane count
/// alone. It is also builtin, so no FFmpeg build reports the fixture unavailable.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(ContentCaptureCollection.Name)]
public sealed class AlphaPixelFormatTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string YuvaClip = "test-video-ffv1-yuva420p.mkv";

    [RequiresCorpusFileFact(YuvaClip, "Run: dotnet run scripts/generate-test-corpus.cs")]
    public async Task FourPlaneSource_DecodesEveryFrame()
    {
        var expectation = IntegrationTestHelper.GetCorpusExpectation(YuvaClip);
        Assert.True(expectation is not null, $"No corpus expectation for {YuvaClip}.");
        var expectedFrames = expectation!.ExpectedVideoFrames!.Value;

        var capture = await PlaybackHarness.PlayCorpusFileAsync(YuvaClip);

        Assert.True(
            capture.LoadResult.IsSuccess,
            $"LoadAsync failed: {capture.LoadResult.Error?.Message}"
        );
        Assert.True(
            capture.PlayResult.IsSuccess,
            $"PlayAsync failed: {capture.PlayResult.Error?.Message}"
        );
        Assert.Equal(PlaybackState.Ended, capture.FinalState);

        var pipeline = capture.Diagnostics.Pipeline;
        var decoder = pipeline.Stream.VideoDecoder;

        // The clip is video-only, so one packet is one coded frame and the
        // accounting below can compare packets against frames. Asserted rather than
        // assumed, matching DecodePressureTests.
        Assert.True(
            pipeline.Stream.Demux.PacketsRead == expectedFrames,
            $"demuxed {pipeline.Stream.Demux.PacketsRead} packets from a video-only clip "
                + $"of {expectedFrames} frames, so packets and frames do not correspond one "
                + "to one here and the accounting below would not be sound."
        );

        // Every frame is accounted for: decoded, shed for backpressure, or dropped
        // while resynchronising to the next keyframe after a shed. Not
        // FramesDecoded == expected on its own, which a loaded runner could fail for
        // a reason that has nothing to do with pixel formats; and not "some frames
        // arrived", which a partial-conversion regression would pass. On the broken
        // build all three counters read zero, so the sum is zero and this still
        // fails.
        var accounted =
            decoder.FramesDecoded
            + decoder.PacketsDroppedForBackpressure
            + decoder.PacketsDroppedToGopResync;
        Assert.True(
            accounted == expectedFrames,
            $"{expectedFrames} frames in the file, {accounted} accounted for — "
                + $"{decoder.FramesDecoded} decoded, {decoder.PacketsDroppedForBackpressure} "
                + $"shed, {decoder.PacketsDroppedToGopResync} dropped to GOP resync. Zero "
                + "decoded is the #341 shape: the conversion rejected every frame of a "
                + "yuva420p source."
        );

        // Shedding explains a shortfall; it does not explain decoding nothing.
        Assert.True(
            decoder.FramesDecoded > 0,
            "No frame of a yuva420p source converted at all."
        );

        Assert.True(
            decoder.DecodeErrors == 0,
            $"{decoder.DecodeErrors} decode errors on a yuva420p source."
        );

        // Frames reached the sink carrying pixels, rather than the conversion
        // reporting success over a buffer it never wrote.
        Assert.NotEmpty(capture.Video);
        var first = capture.Video[0];
        Assert.Equal(PixelFormat.Bgra32, first.Format);
        Assert.True(
            first.Pixels.Distinct().Count() > 1,
            "Every byte of the first converted frame is identical, so the destination "
                + "buffer was not written from the source planes."
        );
    }
}
