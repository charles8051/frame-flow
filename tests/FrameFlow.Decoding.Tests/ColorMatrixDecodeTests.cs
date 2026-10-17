using FrameFlow.Media;
using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A stream converts to Bgra32 with the matrix it is tagged with (#796), and an untagged stream by
/// its height (#841), unless the caller picks the matrix. Before #796, every stream converted with
/// BT.601. The clips hold the same lossless samples, tagged BT.709, tagged BT.601 and untagged at
/// two sizes, so a decoder that ignored the tag or the size fails one of them.
/// </summary>
public sealed class ColorMatrixDecodeTests(FfmpegBootstrapFixture fixture) : IClassFixture<FfmpegBootstrapFixture>
{
    /// <summary>
    /// What FFmpeg's own <c>scale=out_color_matrix=bt709,format=bgra</c> converts each bar to, in
    /// blue, green, red, with the pinned runtime, which reads the matrix from the frame's tag. Every
    /// pixel of a bar is the same. <c>scale=in_color_matrix=bt709:out_color_matrix=bt709</c> gives
    /// the untagged clips the same values at both sizes.
    /// </summary>
    private static readonly byte[][] FfmpegBt709 =
    [
        [190, 190, 190],
        [0, 188, 190],
        [190, 189, 0],
        [0, 189, 0],
        [191, 0, 190],
        [0, 0, 190],
        [190, 0, 0],
        [0, 0, 0],
    ];

    /// <summary>The same for the bars tagged BT.601.</summary>
    private static readonly byte[][] FfmpegBt601 =
    [
        [190, 190, 190],
        [6, 201, 188],
        [188, 210, 15],
        [4, 222, 14],
        [185, 0, 175],
        [1, 0, 174],
        [182, 0, 0],
        [0, 0, 0],
    ];

    [RequiresFfmpegAndCorpusTheory]
    [InlineData("test-video-ffv1-bt709-bars.mkv", 709)]
    [InlineData("test-video-ffv1-bt601-bars.mkv", 601)]
    public async Task AStreamConvertsWithItsTaggedMatrix(string clip, int tagged)
    {
        using var frame = await DecodeFirstAsync(clip, ColorMatrix.Auto);

        Assert.Equal(1280, frame.Width);
        Assert.Equal(720, frame.Height);
        AssertBars(frame, tagged, clip);
    }

    /// <summary>
    /// An untagged stream converts by its height (#841): the same samples as the tagged clips, with
    /// no tag, convert with BT.601 at 480 rows and with BT.709 at 1080.
    /// </summary>
    [RequiresFfmpegAndCorpusTheory]
    [InlineData("test-video-ffv1-untagged-bars-480p.mkv", 720, 480, 601)]
    [InlineData("test-video-ffv1-untagged-bars-1080p.mkv", 1920, 1080, 709)]
    public async Task AnUntaggedStreamConvertsByItsHeight(string clip, int width, int height, int expected)
    {
        using var frame = await DecodeFirstAsync(clip, ColorMatrix.Auto);

        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        AssertBars(frame, expected, clip);
    }

    /// <summary>
    /// A caller's matrix replaces the tag and the size rule (#841): the untagged SD clip converts
    /// with BT.709 and the HD clip tagged BT.709 with BT.601 when asked.
    /// </summary>
    [RequiresFfmpegAndCorpusTheory]
    [InlineData("test-video-ffv1-untagged-bars-480p.mkv", ColorMatrix.Bt709, 709)]
    [InlineData("test-video-ffv1-untagged-bars-1080p.mkv", ColorMatrix.Bt601, 601)]
    [InlineData("test-video-ffv1-bt709-bars.mkv", ColorMatrix.Bt601, 601)]
    [InlineData("test-video-ffv1-bt601-bars.mkv", ColorMatrix.Bt709, 709)]
    public async Task ACallersMatrixReplacesTheTagAndTheSizeRule(string clip, ColorMatrix choice, int expected)
    {
        using var frame = await DecodeFirstAsync(clip, choice);

        AssertBars(frame, expected, clip);
    }

    private async Task<IVideoFrame> DecodeFirstAsync(string clip, ColorMatrix choice)
    {
        Assert.True(fixture.IsBootstrapped);
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.True(file is not null, $"Corpus is present but {clip} is missing.");

        await using var demux = await OpenAsync(file!);
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null,
            new VideoDecoderOptions { ColorMatrix = choice }
        );
        using var frames = await DecodeAllAsync(demux, decoder);

        Assert.NotEmpty(frames);
        return frames[0].AddRef();
    }

    /// <summary>
    /// Every bar, at the top, middle and bottom rows, is what FFmpeg converts it to with
    /// <paramref name="matrix"/> (601 or 709) within 1 level, and the matrix's own result within 3.
    /// </summary>
    private static void AssertBars(IVideoFrame frame, int matrix, string clip)
    {
        var (ffmpeg, reference) = matrix == 709 ? (FfmpegBt709, YuvReference.Bt709) : (FfmpegBt601, YuvReference.Bt601);
        int barWidth = frame.Width / YuvReference.Bars.Length;
        foreach (int row in new[] { 0, frame.Height / 2 - 1, frame.Height - 1 })
        {
            for (int bar = 0; bar < YuvReference.Bars.Length; bar++)
            {
                int column = bar * barWidth + barWidth / 2;
                var actual = new[] { BgraSample(frame, column, row, 0), BgraSample(frame, column, row, 1), BgraSample(frame, column, row, 2) };
                YuvReference.AssertNear(ffmpeg[bar], actual, $"{clip} bar {bar}, row {row}");

                // And the matrix's own result, at swscale's precision.
                var (y, cb, cr) = YuvReference.Bars[bar];
                YuvReference.AssertNear(YuvReference.Bgr(y, cb, cr, reference, fullRange: false), actual, $"{clip} bar {bar}, row {row}", tolerance: 3);
            }
        }
    }
}
