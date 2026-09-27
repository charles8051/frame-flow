using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Core;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>Whether a device tensor fits the model input it is bound to. Pure.</summary>
public sealed class DeviceInputFitTests
{
    private static readonly long[] Model = [1, 3, 48, 64];

    [Fact]
    public void TheSameTypeAndShape_Fits() =>
        Assert.Null(DeviceInputFit.Mismatch(Model, TensorElementType.Float, TensorElementType.Float, Tensor(1, 3, 48, 64)));

    [Fact]
    public void ADynamicDimension_TakesAnySize() =>
        Assert.Null(DeviceInputFit.Mismatch([-1, 3, 48, 64], TensorElementType.Float, TensorElementType.Float, Tensor(4, 3, 48, 64)));

    [Theory]
    [InlineData(1, 3, 48, 65)]
    [InlineData(1, 48, 64, 3)]
    [InlineData(3, 48, 64, 1)]
    public void AFixedDimensionThatDiffers_DoesNotFit(int n, int c, int h, int w)
    {
        string? mismatch = DeviceInputFit.Mismatch(Model, TensorElementType.Float, TensorElementType.Float, Tensor(n, c, h, w));

        Assert.NotNull(mismatch);
        Assert.Contains("[1, 3, 48, 64]", mismatch);
    }

    [Fact]
    public void AnotherRank_DoesNotFit() =>
        Assert.NotNull(DeviceInputFit.Mismatch(Model, TensorElementType.Float, TensorElementType.Float, Tensor(3, 48, 64)));

    [Fact]
    public void AnotherElementType_DoesNotFit() =>
        Assert.NotNull(DeviceInputFit.Mismatch(Model, TensorElementType.Float, TensorElementType.Float16, Tensor(1, 3, 48, 64)));

    private static DeviceTensor Tensor(params int[] dims) =>
        new(DeviceTensorKind.D3D12, Buffer: 1, Device: 2, new TensorShape(dims), DType.Float32, ReadyFence: 0, ReadyValue: 0);
}
