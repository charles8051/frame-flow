using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Yolo.Tests;

/// <summary>What the detection operator declares it holds (ADR-0081).</summary>
public sealed class YoloHoldingTests
{
    [Fact]
    public void DetectWith_CarriesItsInputFrameForward()
    {
        var node = YoloOperators.DetectWith("detect", _ => []);

        Assert.Same(FrameHolding.InFlight, node.Holding);
    }
}
