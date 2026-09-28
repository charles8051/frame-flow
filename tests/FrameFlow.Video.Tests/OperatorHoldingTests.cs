using FrameFlow.Graph;
using FrameFlow.Media;
using Xunit;

namespace FrameFlow.Video.Tests;

/// <summary>
/// What the video operators declare they hold, and which are storage boundaries (ADR-0081).
/// </summary>
public sealed class OperatorHoldingTests
{
    [Fact]
    public void TheConverters_EmitANewFrame()
    {
        Assert.Same(FrameHolding.Boundary, VideoOperators.ConvertPixelFormat("convert", PixelFormat.Rgba32).Holding);
        Assert.Same(FrameHolding.Boundary, VideoOperators.Resize("resize", 64, 32).Holding);
        Assert.Same(FrameHolding.Boundary, VideoOperators.ResizeAndConvert("rc", 64, 64, PixelFormat.Rgba32).Holding);
    }

    [Fact]
    public void ToCpu_CanForwardItsInput()
    {
        // A CPU frame goes through as itself, so the node is not a boundary for it.
        Assert.Same(FrameHolding.InFlight, VideoOperators.ToCpu("to-cpu").Holding);
    }

    [Fact]
    public void TheConverters_TakeCpuFramesOnly_AndToCpuDownloads()
    {
        // sws_scale reads the input on the CPU (#435).
        Assert.Same(FrameDomainRule.CpuOnly, VideoOperators.ConvertPixelFormat("convert", PixelFormat.Rgba32).Domains);
        Assert.Same(FrameDomainRule.CpuOnly, VideoOperators.Resize("resize", 64, 32).Domains);
        Assert.Same(FrameDomainRule.CpuOnly, VideoOperators.ResizeAndConvert("rc", 64, 64, PixelFormat.Rgba32).Domains);
        Assert.Same(FrameDomainRule.ToCpu, VideoOperators.ToCpu("to-cpu").Domains);
    }
}
