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
        Assert.Same(Holding.Boundary, VideoOperators.ConvertPixelFormat("convert", PixelFormat.Rgba32).Holding);
        Assert.Same(Holding.Boundary, VideoOperators.Resize("resize", 64, 32).Holding);
        Assert.Same(Holding.Boundary, VideoOperators.ResizeAndConvert("rc", 64, 64, PixelFormat.Rgba32).Holding);
    }

    [Fact]
    public void ToCpu_CanForwardItsInput()
    {
        // A CPU frame goes through as itself, so the node is not a boundary for it.
        Assert.Same(Holding.InFlight, VideoOperators.ToCpu("to-cpu").Holding);
    }
}
