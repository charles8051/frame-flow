using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// An NV12 hardware frame converts from the planar layout its software decode would have, and the
/// range (#792) and the matrix (#796) are the frame's. Pure.
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
        var input = BgraConversionInput.For((int)decoded, (int)AVColorRange.AVCOL_RANGE_MPEG, (int)AVColorSpace.AVCOL_SPC_BT470BG);

        Assert.Equal(new BgraConversionInput((int)converted, splitChroma, false, BgraConversionInput.Bt601), input);
    }

    [Theory]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ420P, AVPixelFormat.AV_PIX_FMT_YUV420P)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ422P, AVPixelFormat.AV_PIX_FMT_YUV422P)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ444P, AVPixelFormat.AV_PIX_FMT_YUV444P)]
    public void AJpegFormatIsItsPlainFormatAtFullRange(AVPixelFormat decoded, AVPixelFormat converted)
    {
        var input = BgraConversionInput.For((int)decoded, (int)AVColorRange.AVCOL_RANGE_UNSPECIFIED, (int)AVColorSpace.AVCOL_SPC_BT709);

        Assert.Equal(new BgraConversionInput((int)converted, false, true, BgraConversionInput.Bt709), input);
    }

    [Theory]
    [InlineData(AVColorRange.AVCOL_RANGE_JPEG, true)]
    [InlineData(AVColorRange.AVCOL_RANGE_MPEG, false)]
    [InlineData(AVColorRange.AVCOL_RANGE_UNSPECIFIED, false)]
    public void TheRangeIsTheFrames(AVColorRange range, bool fullRange)
    {
        const int unspecified = (int)AVColorSpace.AVCOL_SPC_UNSPECIFIED;
        Assert.Equal(fullRange, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_NV12, (int)range, unspecified).FullRange);
        Assert.Equal(fullRange, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_YUV420P, (int)range, unspecified).FullRange);
    }

    /// <summary>
    /// The matrix each <c>AVColorSpace</c> converts with. Every member of the enum is here, and
    /// <see cref="EveryColorSpaceHasAMatrix"/> fails when FFmpeg adds one, so a new value is a
    /// decision rather than a silent fallback.
    /// </summary>
    public static TheoryData<AVColorSpace, int> Matrices() =>
        new()
        {
            { AVColorSpace.AVCOL_SPC_RGB, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_BT709, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_UNSPECIFIED, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_RESERVED, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_FCC, BgraConversionInput.Fcc },
            { AVColorSpace.AVCOL_SPC_BT470BG, BgraConversionInput.Bt601 },
            { AVColorSpace.AVCOL_SPC_SMPTE170M, BgraConversionInput.Bt601 },
            { AVColorSpace.AVCOL_SPC_SMPTE240M, BgraConversionInput.Smpte240M },
            { AVColorSpace.AVCOL_SPC_YCGCO, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_BT2020_NCL, BgraConversionInput.Bt2020 },
            { AVColorSpace.AVCOL_SPC_BT2020_CL, BgraConversionInput.Bt2020 },
            { AVColorSpace.AVCOL_SPC_SMPTE2085, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_NCL, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_CL, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_ICTCP, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_IPT_C2, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_YCGCO_RE, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_YCGCO_RO, BgraConversionInput.Bt709 },
            { AVColorSpace.AVCOL_SPC_NB, BgraConversionInput.Bt709 },
        };

    [Theory]
    [MemberData(nameof(Matrices))]
    public void TheMatrixIsTheFrames(AVColorSpace colorSpace, int matrix)
    {
        Assert.Equal(matrix, BgraConversionInput.MatrixFor((int)colorSpace));
        Assert.Equal(matrix, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_NV12, 0, (int)colorSpace).Matrix);
        Assert.Equal(matrix, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_YUVJ420P, 0, (int)colorSpace).Matrix);
        Assert.Equal(matrix, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_YUV420P10LE, 0, (int)colorSpace).Matrix);
    }

    [Fact]
    public void EveryColorSpaceHasAMatrix()
    {
        var named = Matrices().Select(row => (AVColorSpace)row[0]).ToHashSet();

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
        Assert.Equal(BgraConversionInput.Unsignalled, BgraConversionInput.MatrixFor(colorSpace));
    }

    /// <summary>
    /// An unsignalled frame converts with BT.709 at every size, as the D3D11 and D3D12 presenters
    /// convert every frame (ADR-0063), so the CPU path and the GPU path agree on it.
    /// </summary>
    [Fact]
    public void AnUnsignalledFrameConvertsAsThePresentersDo()
    {
        Assert.Equal(BgraConversionInput.Bt709, BgraConversionInput.Unsignalled);
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
