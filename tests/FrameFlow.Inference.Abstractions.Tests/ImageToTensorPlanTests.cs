using FrameFlow.Inference.Core;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// The arithmetic of <see cref="ImageToTensorPlan"/>: where a tensor pixel lands in the frame,
/// which pixels a letterbox covers, and the transform handed back to postprocessors.
/// </summary>
public sealed class ImageToTensorPlanTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void AStretchedWholeFrame_MapsTensorCornersToFrameCorners()
    {
        var plan = ImageToTensorPlan.Create(
            RotatedRect.FromBounds(0, 0, 1920, 1080), 640, 640, ImageFit.Stretch);

        // The first tensor pixel's centre, half a tensor pixel in on each axis.
        Assert.Equal(1.5, plan.SourceX(0, 0), Tolerance);
        Assert.Equal(0.84375, plan.SourceY(0, 0), Tolerance);
        AssertPoint((0, 0), plan.Transform.ToFrame(0, 0));
        AssertPoint((1920, 1080), plan.Transform.ToFrame(1, 1));
        Assert.True(plan.CoversColumn(0) && plan.CoversColumn(639));
        Assert.True(plan.CoversRow(0) && plan.CoversRow(639));
    }

    [Fact]
    public void ALetterbox_ScalesBothAxesAlikeAndBarsTheRest()
    {
        var plan = ImageToTensorPlan.Create(
            RotatedRect.FromBounds(0, 0, 1920, 1080), 640, 640, ImageFit.Letterbox);

        // Three frame pixels per tensor pixel on both axes: 1080 rows fill 360, centred.
        Assert.Equal(140, plan.FitTop, Tolerance);
        Assert.Equal(500, plan.FitBottom, Tolerance);
        Assert.False(plan.CoversRow(139));
        Assert.True(plan.CoversRow(140));
        Assert.True(plan.CoversRow(499));
        Assert.False(plan.CoversRow(500));
        Assert.True(plan.CoversColumn(0) && plan.CoversColumn(639));
        Assert.Equal(1.5, plan.SourceY(0, 140), Tolerance);
        AssertPoint((960, 540), plan.Transform.ToFrame(0.5f, 0.5f));
        AssertPoint((0, 0), plan.Transform.ToFrame(0, 140 / 640f));
    }

    [Fact]
    public void AQuarterTurnClockwise_RunsTheTensorsTopEdgeDownTheCropsRightSide()
    {
        // A 20 x 10 crop about (50, 50), turned a quarter clockwise, spans x 45..55 and y 40..60.
        var crop = new RotatedRect(50, 50, 20, 10, MathF.PI / 2);
        var plan = ImageToTensorPlan.Create(crop, 20, 10, ImageFit.Stretch);

        AssertPoint((55, 40), plan.Transform.ToFrame(0, 0));
        AssertPoint((55, 60), plan.Transform.ToFrame(1, 0));
        AssertPoint((45, 40), plan.Transform.ToFrame(0, 1));
        AssertPoint((50, 50), plan.Transform.ToFrame(0.5f, 0.5f));
        Assert.False(plan.IsAxisAligned);
    }

    [Fact]
    public void ToTensor_InvertsToFrame()
    {
        var crop = new RotatedRect(300, 200, 120, 80, 0.4f);
        var transform = ImageToTensorPlan.Create(crop, 192, 192, ImageFit.Letterbox).Transform;

        foreach (var (u, v) in new[] { (0f, 0f), (1f, 1f), (0.25f, 0.8f), (0.5f, 0.5f) })
        {
            var (x, y) = transform.ToFrame(u, v);
            var (u2, v2) = transform.ToTensor(x, y);
            Assert.Equal(u, u2, 1e-4f);
            Assert.Equal(v, v2, 1e-4f);
        }
    }

    [Fact]
    public void OnlyAnUnrotatedCrop_IsAxisAligned()
    {
        Assert.True(ImageToTensorPlan.Create(new RotatedRect(10, 10, 4, 4), 8, 8, ImageFit.Stretch).IsAxisAligned);
        Assert.False(ImageToTensorPlan.Create(new RotatedRect(10, 10, 4, 4, 0.01f), 8, 8, ImageFit.Stretch).IsAxisAligned);
    }

    [Fact]
    public void LinearIndex_StraddlesThePixelCentres()
    {
        // Pixel i's centre is at i + 0.5, so 1.25 sits a quarter of the way from pixel 0 to pixel 1.
        Assert.Equal((0, 1, 0.75f), ImageToTensorKernel.LinearIndex(1.25, 9));
        Assert.Equal((0, 0, 0.75f), ImageToTensorKernel.LinearIndex(0.25, 9));
        Assert.Equal((9, 9, 0.25f), ImageToTensorKernel.LinearIndex(9.75, 9));
    }

    private static void AssertPoint((float X, float Y) expected, (float X, float Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 1e-3f);
        Assert.Equal(expected.Y, actual.Y, 1e-3f);
    }
}
