using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// The inference operator's two routes on real hardware (#436): a D3D12VA frame written by the
/// D3D12 stage and bound in place into a DirectML session on the decoder's device, and a CPU frame
/// written by <see cref="ImageToTensor"/> into the same session. The model negates its input, so
/// each result is exact against the tensor written.
/// </summary>
public sealed class OperatorRouteTests
{
    private const string Clip = "test-video-h264-yuv420p.mp4";

    private static readonly ImageToTensorOptions Options = new(64, 48);

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task AD3D12VaFrame_TakesTheDeviceRoute_AndACpuFrameTheHostRoute()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 3);
        try
        {
            using var gpu = new DeviceAndQueue((GpuVideoFrame)frames[0]);
            using var stage = gpu.Stage(Options);
            using var session = DmlInferenceSession.OnDevice(
                OnnxModel.Negate(1, 3, Options.Height, Options.Width), gpu.Device.NativePointer, gpu.Queue.NativePointer);
            var runner = new InferenceRunner<float[]>(new NegateModel(session), stage);

            foreach (var frame in frames)
            {
                Assert.True(stage.CanWrite(frame));
                var result = runner.Run(frame);

                Assert.Equal(InferencePath.Device, result.Path);
                Assert.Equal(frame.Timestamp, result.Timestamp);
                Assert.Equal(stage.ReadBack().Select(v => -v), result.Result);
            }

            using var cpu = ((GpuVideoFrame)frames[0]).ReadbackToCpuBgra32();
            Assert.False(stage.CanWrite(cpu));
            var host = runner.Run(cpu);
            var expected = new float[Options.ElementCount];
            ImageToTensor.Write(cpu, RotatedRect.Whole(cpu), Options, expected);

            Assert.Equal(InferencePath.Host, host.Path);
            Assert.Equal(expected.Select(v => -v), host.Result);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    private sealed class NegateModel(DmlInferenceSession session) : IImageModel<float[]>
    {
        public IInferenceSession Session => session;

        public string InputName => "x";

        public ImageToTensorOptions Input => Options;

        public RotatedRect CropFor(IVideoFrame frame) => RotatedRect.Whole(frame);

        public float[] Decode(IReadOnlyDictionary<string, ICpuTensor> outputs, TensorTransform transform, IVideoFrame frame) =>
            MemoryMarshal.Cast<byte, float>(outputs["y"].Bytes.Span).ToArray();
    }
}
