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

        var decoder = capture.Diagnostics.Pipeline.Stream.VideoDecoder;

        // The count, not merely "some frames arrived". The bug dropped every frame
        // of the file, and a partial-decode regression on a four-plane format would
        // look the same to a NotEmpty assertion.
        Assert.True(
            decoder.FramesDecoded == expectedFrames,
            $"Decoded {decoder.FramesDecoded} of {expectedFrames} frames from a "
                + "yuva420p source. Zero means the conversion rejected every frame, which "
                + "is the #341 shape; a number in between means it rejected some."
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
