using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>What the face detection operator declares it holds (ADR-0081).</summary>
public sealed class FaceHoldingTests
{
    [Fact]
    public void DetectWith_CarriesItsInputFrameForward()
    {
        var node = FaceOperators.DetectWith("faces", _ => []);

        Assert.Same(Holding.InFlight, node.Holding);
    }
}
