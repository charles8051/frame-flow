using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using Vortice.Direct3D12;

namespace FrameFlow.Inference.D3D12.Tests;

/// <summary>The decoder's device and a compute queue on it, for building the stage.</summary>
internal sealed class DeviceAndQueue : IDisposable
{
    public DeviceAndQueue(GpuVideoFrame frame)
    {
        Assert.True(frame.TryGetD3D12Texture(out nint texture, out _, out _, out _));
        Marshal.AddRef(texture);
        using var resource = new ID3D12Resource(texture);
        Device = resource.GetDevice<ID3D12Device>();
        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
    }

    /// <summary>The device a <see cref="HardwareDevice"/> decodes on, and a compute queue on it.</summary>
    public DeviceAndQueue(HardwareDevice device)
    {
        Assert.True(device.TryGetD3D12Device(out nint pointer));
        Marshal.AddRef(pointer);
        Device = new ID3D12Device(pointer);
        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
    }

    public ID3D12Device Device { get; }

    public ID3D12CommandQueue Queue { get; }

    public D3D12ImageToTensor Stage(ImageToTensorOptions options) =>
        new(Device.NativePointer, Queue.NativePointer, options);

    public void Dispose()
    {
        Queue.Dispose();
        Device.Dispose();
    }
}
