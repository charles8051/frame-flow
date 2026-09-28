using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Core;
using FrameFlow.Media;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The format a hardware frame reports is its pool's <c>sw_format</c> (#430), not Nv12 whatever
/// the surface holds.
/// </summary>
public sealed class GpuFrameFormatTests
{
    [Theory]
    [InlineData(AVPixelFormat.AV_PIX_FMT_NV12, PixelFormat.Nv12)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_P010LE, PixelFormat.P010)]
    public void APoolsSoftwareFormat_IsTheFrameFormat(AVPixelFormat softwareFormat, PixelFormat expected)
    {
        Assert.Equal(expected, GpuFrameFormat.From((int)softwareFormat));
    }

    [Theory]
    [InlineData(AVPixelFormat.AV_PIX_FMT_P016LE)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUV420P)]
    [InlineData(AVPixelFormat.AV_PIX_FMT_NONE)]
    public void AFormatPixelFormatCannotName_HasNone(AVPixelFormat softwareFormat)
    {
        Assert.Null(GpuFrameFormat.From((int)softwareFormat));
    }
}
