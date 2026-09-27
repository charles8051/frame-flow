using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Core;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>Which GPU waits a device-input run makes before DirectML reads. Pure.</summary>
public sealed class ReadyWaitsTests
{
    [Fact]
    public void EachFence_IsWaitedOnceAtTheHighestValueAnInputNeeds()
    {
        var waits = ReadyWaits.For([Tensor(fence: 10, value: 7), Tensor(fence: 20, value: 1), Tensor(fence: 10, value: 3)]);

        Assert.Equal([(10, 7UL), (20, 1UL)], waits.OrderBy(w => w.Fence).Select(w => (w.Fence, w.Value)));
    }

    [Fact]
    public void AnInputWithNoFence_IsAlreadyWritten()
    {
        Assert.Empty(ReadyWaits.For([Tensor(fence: 0, value: 5)]));
    }

    private static DeviceTensor Tensor(nint fence, ulong value) =>
        new(DeviceTensorKind.D3D12, Buffer: 1, Device: 2, new TensorShape(1, 3, 2, 2), DType.Float32, fence, value);
}
