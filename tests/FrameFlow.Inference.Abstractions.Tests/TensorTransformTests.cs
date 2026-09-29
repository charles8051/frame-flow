using System.Numerics;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary><see cref="TensorTransform"/> with its inverse computed once (#484).</summary>
public sealed class TensorTransformTests
{
    private static readonly TensorTransform Rotated = ImageToTensor.Transform(
        new RotatedRect(640, 360, 162, 90, 0.2f), new ImageToTensorOptions(256, 256) { Fit = ImageFit.Letterbox });

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.25f, 0.8f)]
    [InlineData(1f, 1f)]
    public void ToTensor_UndoesToFrame(float u, float v)
    {
        var (x, y) = Rotated.ToFrame(u, v);
        var (u2, v2) = Rotated.ToTensor(x, y);

        Assert.Equal(u, u2, 1e-4f);
        Assert.Equal(v, v2, 1e-4f);
    }

    [Fact]
    public void A_With_OnTheMatrix_InvertsTheNewOne()
    {
        var scaled = Rotated with { TensorToFrame = Matrix3x2.CreateScale(4, 2) };

        Assert.Equal((0.5f, 0.25f), scaled.ToTensor(2, 0.5f));
    }

    [Fact]
    public void TheDefault_MapsToNaN_AsItAlwaysHas()
    {
        var (u, v) = default(TensorTransform).ToTensor(1, 1);

        Assert.True(float.IsNaN(u) && float.IsNaN(v));
    }

    [Fact]
    public void ASingularMatrix_MapsToNaN_AndStillEqualsItself()
    {
        var singular = new Matrix3x2(1, 2, 2, 4, 0, 0);
        var a = new TensorTransform(singular);
        var b = new TensorTransform(singular);

        Assert.True(float.IsNaN(a.ToTensor(1, 1).U));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_IsTheMatrixs()
    {
        var copy = new TensorTransform(Rotated.TensorToFrame);

        Assert.Equal(Rotated, copy);
        Assert.Equal(Rotated.GetHashCode(), copy.GetHashCode());
        Assert.NotEqual(Rotated, new TensorTransform(Matrix3x2.Identity));
    }

    [Fact]
    public void Deconstruct_GivesTheMatrix()
    {
        Rotated.Deconstruct(out var matrix);

        Assert.Equal(Rotated.TensorToFrame, matrix);
    }
}
