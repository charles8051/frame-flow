using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Inference.D3D12;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// A tensor the D3D12 stage writes reaching a DirectML session on the same device with no copy
/// (#427). The model negates its input, so each output is exact against the tensor read back.
/// </summary>
public sealed class DeviceInputTests
{
    private const string Clip = "test-video-h264-yuv420p.mp4";

    private static readonly ImageToTensorOptions Options = new(64, 48);

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task TheStagesTensor_IsTheModelsInput_OnTheStagesQueue()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 3);
        try
        {
            using var gpu = new DeviceAndQueue((GpuVideoFrame)frames[0]);
            using var stage = gpu.Stage(Options);
            using var session = DmlInferenceSession.OnDevice(
                OnnxModel.Negate(1, 3, Options.Height, Options.Width), gpu.Device.NativePointer, gpu.Queue.NativePointer);

            AssertEachWriteReachesTheModel(stage, session, frames);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    /// <summary>
    /// The session's queue is not the stage's, so only the wait on the tensor's ready fence orders
    /// the model after the write. This passes with the wait removed too, because the stage's write
    /// is done before DirectML reads; <c>ReadyWaitsTests</c> pins which waits a run makes.
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task TheStagesTensor_IsTheModelsInput_FromAnotherQueue()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 3);
        try
        {
            using var gpu = new DeviceAndQueue((GpuVideoFrame)frames[0]);
            using var stage = gpu.Stage(Options);
            using var sessionQueue = gpu.Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
            using var session = DmlInferenceSession.OnDevice(
                OnnxModel.Negate(1, 3, Options.Height, Options.Width), gpu.Device.NativePointer, sessionQueue.NativePointer);

            AssertEachWriteReachesTheModel(stage, session, frames);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task CanBind_TakesOnlyAD3D12BufferOnTheSessionsDevice()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 1);
        try
        {
            using var gpu = new DeviceAndQueue((GpuVideoFrame)frames[0]);
            using var stage = gpu.Stage(Options);
            byte[] model = OnnxModel.Negate(1, 3, Options.Height, Options.Width);
            using var session = DmlInferenceSession.OnDevice(model, gpu.Device.NativePointer, gpu.Queue.NativePointer);
            stage.Write((GpuVideoFrame)frames[0], RotatedRect.FromBounds(0, 0, 320, 240));
            var tensor = stage.DeviceTensor;
            using var warp = WarpDevice();

            Assert.True(session.CanBind(tensor));
            Assert.False(session.CanBind(tensor with { Device = warp.NativePointer }));
            Assert.False(session.CanBind(tensor with { Kind = (DeviceTensorKind)99 }));
            Assert.False(session.CanBind(tensor with { Buffer = 0 }));

            // A session on a device ONNX Runtime made binds nothing, and says so when asked to.
            using var ortOwned = new DmlInferenceSession(model);
            using var pool = new CpuTensorPool();
            using var output = pool.Rent<float>(new TensorShape(1, 3, Options.Height, Options.Width));
            Assert.False(ortOwned.CanBind(tensor));
            Assert.Throws<InvalidOperationException>(() => ortOwned.Run(
                new Dictionary<string, DeviceTensor> { ["x"] = tensor },
                new Dictionary<string, ICpuTensor> { ["y"] = output }));
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task OnDevice_RefusesAQueueFromAnotherDevice()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 1);
        try
        {
            using var gpu = new DeviceAndQueue((GpuVideoFrame)frames[0]);
            using var warp = WarpDevice();
            using var warpQueue = warp.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));

            var error = Assert.Throws<ArgumentException>(() => DmlInferenceSession.OnDevice(
                OnnxModel.Negate(1, 3, Options.Height, Options.Width), gpu.Device.NativePointer, warpQueue.NativePointer));
            Assert.Equal("commandQueue", error.ParamName);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    /// <summary>
    /// Writes each frame through a different crop, so consecutive tensors differ and a stale input
    /// shows, then runs the model on the tensor in place and compares with the tensor read back.
    /// </summary>
    private static void AssertEachWriteReachesTheModel(D3D12ImageToTensor stage, IDeviceInputSession session, List<IVideoFrame> frames)
    {
        using var pool = new CpuTensorPool();
        float[]? previous = null;
        for (int i = 0; i < frames.Count; i++)
        {
            stage.Write((GpuVideoFrame)frames[i], RotatedRect.FromBounds(i * 40, i * 20, 200 + i * 40, 150 + i * 20));
            var tensor = stage.DeviceTensor;
            Assert.True(session.CanBind(tensor));

            using var output = pool.Rent<float>(tensor.Shape);
            session.Run(
                new Dictionary<string, DeviceTensor> { ["x"] = tensor },
                new Dictionary<string, ICpuTensor> { ["y"] = output });

            float[] written = stage.ReadBack();
            float[] seen = MemoryMarshal.Cast<byte, float>(output.Bytes.Span).ToArray();
            Assert.Equal(written.Select(v => -v), seen);
            if (previous is not null)
                Assert.NotEqual(previous, written);
            previous = written;
        }
    }

    private static ID3D12Device WarpDevice()
    {
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug: false);
        using var adapter = factory.EnumWarpAdapter<IDXGIAdapter>();
        Vortice.Direct3D12.D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).CheckError();
        return device!;
    }
}
