using FrameFlow.Media;
using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A stream converts to Bgra32 with the matrix it is tagged with (#796). Before, every stream
/// converted with BT.601. The two clips hold the same lossless samples, tagged BT.709 and BT.601,
/// so a decoder that ignored the tag fails one of them.
/// </summary>
public sealed class ColorMatrixDecodeTests(FfmpegBootstrapFixture fixture) : IClassFixture<FfmpegBootstrapFixture>
{
    /// <summary>
    /// What FFmpeg's own <c>scale=out_color_matrix=bt709,format=bgra</c> converts each bar to, in
    /// blue, green, red, with the pinned runtime, which reads the matrix from the frame's tag. Every
    /// pixel of a bar is the same.
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
        Assert.True(fixture.IsBootstrapped);
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.True(file is not null, $"Corpus is present but {clip} is missing.");
        var (ffmpeg, matrix) = tagged == 709 ? (FfmpegBt709, YuvReference.Bt709) : (FfmpegBt601, YuvReference.Bt601);

        await using var demux = await OpenAsync(file!);
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );
        using var frames = await DecodeAllAsync(demux, decoder);

        Assert.NotEmpty(frames);
        var frame = frames[0];
        Assert.Equal(1280, frame.Width);
        Assert.Equal(720, frame.Height);
        int barWidth = frame.Width / YuvReference.Bars.Length;
        foreach (int row in new[] { 0, 359, 719 })
        {
            for (int bar = 0; bar < YuvReference.Bars.Length; bar++)
            {
                int column = bar * barWidth + barWidth / 2;
                var actual = new[] { BgraSample(frame, column, row, 0), BgraSample(frame, column, row, 1), BgraSample(frame, column, row, 2) };
                YuvReference.AssertNear(ffmpeg[bar], actual, $"{clip} bar {bar}, row {row}");

                // And the matrix's own result, at swscale's precision.
                var (y, cb, cr) = YuvReference.Bars[bar];
                YuvReference.AssertNear(YuvReference.Bgr(y, cb, cr, matrix, fullRange: false), actual, $"{clip} bar {bar}, row {row}", tolerance: 3);
            }
        }
    }
}
