using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// An NV12 hardware frame converts from the planar layout its software decode would have, and the
/// range is the frame's (#792). Pure.
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
        var input = BgraConversionInput.For((int)decoded, (int)AVColorRange.AVCOL_RANGE_MPEG);

        Assert.Equal(new BgraConversionInput((int)converted, splitChroma, false), input);
    }

    [Theory]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ420P, AVPixelFormat.AV_PIX_FMT_YUV420P)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ422P, AVPixelFormat.AV_PIX_FMT_YUV422P)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUVJ444P, AVPixelFormat.AV_PIX_FMT_YUV444P)]
    public void AJpegFormatIsItsPlainFormatAtFullRange(AVPixelFormat decoded, AVPixelFormat converted)
    {
        var input = BgraConversionInput.For((int)decoded, (int)AVColorRange.AVCOL_RANGE_UNSPECIFIED);

        Assert.Equal(new BgraConversionInput((int)converted, false, true), input);
    }

    [Theory]
    [InlineData(AVColorRange.AVCOL_RANGE_JPEG, true)]
    [InlineData(AVColorRange.AVCOL_RANGE_MPEG, false)]
    [InlineData(AVColorRange.AVCOL_RANGE_UNSPECIFIED, false)]
    public void TheRangeIsTheFrames(AVColorRange range, bool fullRange)
    {
        Assert.Equal(fullRange, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_NV12, (int)range).FullRange);
        Assert.Equal(fullRange, BgraConversionInput.For((int)AVPixelFormat.AV_PIX_FMT_YUV420P, (int)range).FullRange);
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
