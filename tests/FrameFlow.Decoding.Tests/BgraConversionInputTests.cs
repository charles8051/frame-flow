using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Core;
using FrameFlow.Media;
using FrameFlow.Media.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// An NV12 hardware frame converts from the planar layout its software decode would have, and the
/// range (#792) and the matrix (#796, #841) are the frame's. Pure.
/// </summary>
public sealed class BgraConversionInputTests
{
    [Theory]
    [InlineData(AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_YUV420P, true)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_P010LE, AVPixelFormat.AV_PIX_FMT_P010LE, false)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_YUV420P, false)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUV420P10LE, AVPixelFormat.AV_PIX_FMT_YUV420P10LE, false)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVA420P, AVPixelFormat.AV_PIX_FMT_YUVA420P, false)]
    public void Nv12ConvertsFromYuv420P(AVPixelFormat decoded, AVPixelFormat converted, bool splitChroma)
    {
        var input = BgraConversionInput.For((int)decoded, (int)AVColorRange.AVCOL_RANGE_MPEG, YcbcrMatrix.Bt601);

        Assert.Equal(new BgraConversionInput((int)converted, splitChroma, false, BgraConversionInput.Bt601), input);
    }

    [Theory]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ420P, AVPixelFormat.AV_PIX_FMT_YUV420P)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ422P, AVPixelFormat.AV_PIX_FMT_YUV422P)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ444P, AVPixelFormat.AV_PIX_FMT_YUV444P)]
    public void AJpegFormatIsItsPlainFormatAtFullRange(AVPixelFormat decoded, AVPixelFormat converted)
    {
        var input = BgraConversionInput.For((int)decoded, (int)AVColorRange.AVCOL_RANGE_UNSPECIFIED, YcbcrMatrix.Bt709);

        Assert.Equal(new BgraConversionInput((int)converted, false, true, BgraConversionInput.Bt709), input);
    }

    [Theory]
    [InlineData(AVColorRange.AVCOL_RANGE_JPEG, true)]
    [InlineData(AVColorRange.AVCOL_RANGE_MPEG, false)]
    [InlineData(AVColorRange.AVCOL_RANGE_UNSPECIFIED, false)]
    public void TheRangeIsTheFrames(AVColorRange range, bool fullRange)
    {
        Assert.Equal(fullRange, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_NV12, (int)range, YcbcrMatrix.Bt709).FullRange);
        Assert.Equal(fullRange, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_YUV420P, (int)range, YcbcrMatrix.Bt709).FullRange);
    }

    /// <summary>
    /// The matrix each <c>AVColorSpace</c> signals, or none. Every member of the enum is here, and
    /// <see cref="EveryColorSpaceHasARow"/> fails when FFmpeg adds one, so a new value is a
    /// decision rather than a silent fallback.
    /// </summary>
    public static TheoryData<AVColorSpace, string?> Signalled() =>
        new()
        {
            { AVColorSpace.AVCOL_SPC_RGB, null },
            { AVColorSpace.AVCOL_SPC_BT709, nameof(YcbcrMatrix.Bt709) },
            { AVColorSpace.AVCOL_SPC_UNSPECIFIED, null },
            { AVColorSpace.AVCOL_SPC_RESERVED, null },
            { AVColorSpace.AVCOL_SPC_FCC, nameof(YcbcrMatrix.Fcc) },
            { AVColorSpace.AVCOL_SPC_BT470BG, nameof(YcbcrMatrix.Bt601) },
            { AVColorSpace.AVCOL_SPC_SMPTE170M, nameof(YcbcrMatrix.Bt601) },
            { AVColorSpace.AVCOL_SPC_SMPTE240M, nameof(YcbcrMatrix.Smpte240M) },
            { AVColorSpace.AVCOL_SPC_YCGCO, null },
            { AVColorSpace.AVCOL_SPC_BT2020_NCL, nameof(YcbcrMatrix.Bt2020) },
            { AVColorSpace.AVCOL_SPC_BT2020_CL, nameof(YcbcrMatrix.Bt2020) },
            { AVColorSpace.AVCOL_SPC_SMPTE2085, null },
            { AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_NCL, null },
            { AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_CL, null },
            { AVColorSpace.AVCOL_SPC_ICTCP, null },
            { AVColorSpace.AVCOL_SPC_IPT_C2, null },
            { AVColorSpace.AVCOL_SPC_YCGCO_RE, null },
            { AVColorSpace.AVCOL_SPC_YCGCO_RO, null },
            { AVColorSpace.AVCOL_SPC_NB, null },
        };

    /// <summary>
    /// A frame converts with the matrix it signals at every size. One that signals none converts
    /// with BT.601 below 720 rows and BT.709 from 720 (#841).
    /// </summary>
    [Theory]
    [MemberData(nameof(Signalled))]
    public void TheMatrixIsTheFramesElseItsSizes(AVColorSpace colorSpace, string? signalledName)
    {
        YcbcrMatrix? signalled = signalledName is null ? null : Enum.Parse<YcbcrMatrix>(signalledName);
        Assert.Equal(signalled, BgraConversionInput.Signalled((int)colorSpace));
        Assert.Equal(signalled ?? YcbcrMatrix.Bt601, BgraConversionInput.MatrixFor((int)colorSpace, 480));
        Assert.Equal(signalled ?? YcbcrMatrix.Bt601, BgraConversionInput.MatrixFor((int)colorSpace, 719));
        Assert.Equal(signalled ?? YcbcrMatrix.Bt709, BgraConversionInput.MatrixFor((int)colorSpace, 720));
        Assert.Equal(signalled ?? YcbcrMatrix.Bt709, BgraConversionInput.MatrixFor((int)colorSpace, 1080));
    }

    [Fact]
    public void EveryColorSpaceHasARow()
    {
        var named = Signalled().Select(row => (AVColorSpace)row[0]).ToHashSet();

        Assert.Empty(Enum.GetValues<AVColorSpace>().Where(c => !named.Contains(c)));
    }

    /// <summary>
    /// A value outside the enum, from a newer FFmpeg or a corrupt frame, converts as an unsignalled
    /// frame does.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(18)]
    [InlineData(255)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void AnUnknownColorSpaceConvertsAsUnsignalled(int colorSpace)
    {
        Assert.Null(BgraConversionInput.Signalled(colorSpace));
        Assert.Equal(YcbcrMatrix.Bt601, BgraConversionInput.MatrixFor(colorSpace, 480));
        Assert.Equal(YcbcrMatrix.Bt709, BgraConversionInput.MatrixFor(colorSpace, 1080));
    }

    /// <summary>
    /// A caller's fixed matrix replaces a tagged frame's matrix and an untagged frame's size rule
    /// alike (#841).
    /// </summary>
    [Theory]
    [InlineData(ColorMatrix.Bt601, nameof(YcbcrMatrix.Bt601))]
    [InlineData(ColorMatrix.Bt709, nameof(YcbcrMatrix.Bt709))]
    [InlineData(ColorMatrix.Bt2020, nameof(YcbcrMatrix.Bt2020))]
    public void AFixedMatrixWinsOverATaggedAndAnUntaggedFrame(ColorMatrix choice, string expectedName)
    {
        var expected = Enum.Parse<YcbcrMatrix>(expectedName);
        AVColorSpace[] tags =
        [
            AVColorSpace.AVCOL_SPC_BT709,
            AVColorSpace.AVCOL_SPC_SMPTE170M,
            AVColorSpace.AVCOL_SPC_BT2020_NCL,
            AVColorSpace.AVCOL_SPC_UNSPECIFIED,
        ];
        foreach (int height in new[] { 480, 1080 })
        {
            foreach (var tag in tags)
                Assert.Equal(expected, BgraConversionInput.MatrixFor((int)tag, height, choice));
        }
    }

    [Theory]
    [InlineData(nameof(YcbcrMatrix.Bt601), BgraConversionInput.Bt601)]
    [InlineData(nameof(YcbcrMatrix.Bt709), BgraConversionInput.Bt709)]
    [InlineData(nameof(YcbcrMatrix.Bt2020), BgraConversionInput.Bt2020)]
    [InlineData(nameof(YcbcrMatrix.Fcc), BgraConversionInput.Fcc)]
    [InlineData(nameof(YcbcrMatrix.Smpte240M), BgraConversionInput.Smpte240M)]
    public void EachMatrixHasItsSwscaleCoefficients(string matrixName, int sws)
    {
        var matrix = Enum.Parse<YcbcrMatrix>(matrixName);
        Assert.Equal(sws, BgraConversionInput.SwsMatrix(matrix));
        Assert.Equal(sws, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_NV12, 0, matrix).Matrix);
        Assert.Equal(sws, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_YUVJ420P, 0, matrix).Matrix);
        Assert.Equal(sws, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_YUV420P10LE, 0, matrix).Matrix);
    }

    /// <summary>The <c>SWS_CS_*</c> values the matrices name, as libswscale defines them.</summary>
    [Fact]
    public void TheMatricesAreSwscalesValues()
    {
        Assert.Equal(ffmpeg.SWS_CS_ITU709, BgraConversionInput.Bt709);
        Assert.Equal(ffmpeg.SWS_CS_FCC, BgraConversionInput.Fcc);
        Assert.Equal(ffmpeg.SWS_CS_ITU601, BgraConversionInput.Bt601);
        Assert.Equal(ffmpeg.SWS_CS_SMPTE240M, BgraConversionInput.Smpte240M);
        Assert.Equal(ffmpeg.SWS_CS_BT2020, BgraConversionInput.Bt2020);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(960)]
    public void Nv12ChromaSplitsIntoUAndV(int samples)
    {
        var uv = new byte[samples * 2];
        for (int i = 0; i < uv.Length; i++)
            uv[i] = (byte)(i * 37 + 11);
        var u = new byte[samples];
        var v = new byte[samples];

        ChromaSplit.Nv12Row(uv, u, v, samples);

        Assert.Equal(Enumerable.Range(0, samples).Select(i => uv[2 * i]), u);
        Assert.Equal(Enumerable.Range(0, samples).Select(i => uv[2 * i + 1]), v);
    }
}
