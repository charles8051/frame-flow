using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>
/// Pins the SSD anchor table for the front-128 face model. The table is
/// load-bearing — a drift here silently corrupts every decoded box — so
/// these assertions nail the count and the boundary anchors against
/// MediaPipe's known-good <c>face_detection_front</c> generation.
/// </summary>
public sealed class SsdAnchorGeneratorTests
{
    [Fact]
    public void Front128_Generates896Anchors()
    {
        var anchors = SsdAnchorGenerator.Generate(Front128Options());
        // 16×16×2 (stride-8 layer) + 8×8×6 (merged stride-16 layers) = 896.
        Assert.Equal(896, anchors.Count);
    }

    [Fact]
    public void Front128_FirstCellHasTwoAnchorsAtSameCentre()
    {
        var anchors = SsdAnchorGenerator.Generate(Front128Options());

        // Layer 0: stride 8 → 16×16 map. First cell centre = (0+0.5)/16.
        Assert.Equal(0.03125f, anchors[0].XCenter, 5);
        Assert.Equal(0.03125f, anchors[0].YCenter, 5);
        // Interpolated-scale anchor shares the cell centre.
        Assert.Equal(anchors[0].XCenter, anchors[1].XCenter, 6);
        Assert.Equal(anchors[0].YCenter, anchors[1].YCenter, 6);
    }

    [Fact]
    public void Front128_FixedAnchorSize_AllUnitSized()
    {
        var anchors = SsdAnchorGenerator.Generate(Front128Options());
        Assert.All(anchors, a =>
        {
            Assert.Equal(1f, a.Width, 6);
            Assert.Equal(1f, a.Height, 6);
        });
    }

    [Fact]
    public void Front128_LastAnchorIsInStride16Grid()
    {
        var anchors = SsdAnchorGenerator.Generate(Front128Options());

        // Boundary between the layers: index 512 is the first stride-16 anchor.
        // Stride-16 → 8×8 map, first cell centre = (0+0.5)/8 = 0.0625.
        Assert.Equal(0.0625f, anchors[512].XCenter, 5);
        // Last cell centre = (7+0.5)/8 = 0.9375.
        Assert.Equal(0.9375f, anchors[^1].XCenter, 5);
        Assert.Equal(0.9375f, anchors[^1].YCenter, 5);
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        var a = SsdAnchorGenerator.Generate(Front128Options());
        var b = SsdAnchorGenerator.Generate(Front128Options());
        Assert.Equal(a, b);
    }

    [Fact]
    public void Generate_ThrowsWhenStridesCountDiffersFromNumLayers()
    {
        var bad = Front128Options() with { Strides = [8, 16] }; // NumLayers stays 4
        Assert.Throws<ArgumentException>(() => SsdAnchorGenerator.Generate(bad));
    }

    private static SsdAnchorOptions Front128Options() => new()
    {
        InputSizeWidth = 128,
        InputSizeHeight = 128,
        MinScale = 0.1484375f,
        MaxScale = 0.75f,
        NumLayers = 4,
        Strides = [8, 16, 16, 16],
        AspectRatios = [1.0f],
        InterpolatedScaleAspectRatio = 1.0f,
        FixedAnchorSize = true,
    };
}
