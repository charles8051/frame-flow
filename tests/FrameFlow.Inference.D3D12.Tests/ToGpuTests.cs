using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using FrameFlow.Media;
using FrameFlow.Video;

namespace FrameFlow.Inference.D3D12.Tests;

/// <summary>
/// <see cref="VideoOperators.ToGpu"/> puts a CPU frame on a D3D12VA device where the D3D12 stage
/// reads it as it reads a decoded frame (#293).
/// </summary>
public sealed class ToGpuTests
{
    // Only a gate: a D3D12VA device initialises and decodes here.
    private const string Clip = "test-video-h264-yuv420p.mp4";

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task AnUploadedCpuFrame_IsReadByTheD3D12Stage_AsItsOwnSamples()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        var node = VideoOperators.ToGpu("to-gpu", device);
        using var cpu = Gradient(320, 240);

        using var uploaded = Assert.IsType<GpuVideoFrame>(await node.Body(cpu, CancellationToken.None));

        // What the surface holds is the CPU frame, to NV12's rounding.
        using (var readBack = uploaded.ReadbackToCpuBgra32())
            Assert.True(MeanDifference(cpu, readBack) < 3, $"mean |difference| {MeanDifference(cpu, readBack):F2}");

        // And the stage reads that surface as it reads a decoded one.
        using var gpu = new DeviceAndQueue(device);
        var options = new ImageToTensorOptions(64, 48) { Sampling = ImageSampling.Nearest };
        var crop = RotatedRect.FromBounds(0, 0, 320, 240);
        using var stage = gpu.Stage(options);
        Assert.True(stage.CanWrite(uploaded));
        stage.Write(uploaded, crop);

        var expected = D3D12ImageToTensorTests.Reference(
            Nv12Image.Read(uploaded), D3D12ImageToTensorTests.Constants(options, crop, uploaded), options.ElementCount);
        D3D12ImageToTensorTests.AssertClose(expected, stage.ReadBack(), 1e-3f, "the uploaded frame");
    }

    /// <summary>
    /// The upload is BT.601 studio range: white is luma 235, black 16, and pure red 81, where
    /// BT.709 would give 63.
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task TheUploadIsBt601StudioRange()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        var node = VideoOperators.ToGpu("to-gpu", device);

        Assert.Equal(235, await CentreLumaAsync(node, blue: 255, green: 255, red: 255), 1.0);
        Assert.Equal(16, await CentreLumaAsync(node, blue: 0, green: 0, red: 0), 1.0);
        Assert.Equal(81, await CentreLumaAsync(node, blue: 0, green: 0, red: 255), 1.0);
    }

    private static async Task<double> CentreLumaAsync(
        OperatorNode<IVideoFrame, IVideoFrame> node, byte blue, byte green, byte red)
    {
        using var solid = CpuVideoFrame.Create(
            PixelFormat.Bgra32, 32, 32, TimeSpan.Zero, TimeSpan.Zero, (blue, green, red),
            static (planes, colour) =>
            {
                for (int at = 0; at < planes.Y.Length; at += 4)
                {
                    planes.Y[at + 0] = colour.blue;
                    planes.Y[at + 1] = colour.green;
                    planes.Y[at + 2] = colour.red;
                    planes.Y[at + 3] = 255;
                }
            });
        using var uploaded = Assert.IsType<GpuVideoFrame>(await node.Body(solid, CancellationToken.None));
        var samples = Nv12Image.Read(uploaded);
        return samples.Luma[16 * samples.Width + 16];
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task AFrameOnTheDevice_IsForwarded_AndOneOnAnotherDevice_IsRefused()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        using var other = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        var toDevice = VideoOperators.ToGpu("to-gpu", device);
        var toOther = VideoOperators.ToGpu("to-other", other);
        using var cpu = Gradient(64, 48);
        using var uploaded = Assert.IsType<GpuVideoFrame>(await toDevice.Body(cpu, CancellationToken.None));

        Assert.Same(uploaded, await toDevice.Body(uploaded, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await toOther.Body(uploaded, CancellationToken.None));
        Assert.Equal(
            FrameDomainRule.Accepting(FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D12, emits: FrameMemoryDomains.D3D12),
            toDevice.Domains);
    }

    private static double MeanDifference(CpuVideoFrame expected, CpuVideoFrame actual)
    {
        var e = expected.AsCpu()!.Value;
        var a = actual.AsCpu()!.Value;
        long total = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            var eRow = e.PlaneY.Span.Slice(y * e.StrideY, expected.Width * 4);
            var aRow = a.PlaneY.Span.Slice(y * a.StrideY, expected.Width * 4);
            for (int i = 0; i < eRow.Length; i++)
                total += Math.Abs(eRow[i] - aRow[i]);
        }

        return (double)total / (expected.Width * expected.Height * 4);
    }

    private static CpuVideoFrame Gradient(int width, int height) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32, width, height, TimeSpan.Zero, TimeSpan.FromMilliseconds(40), 0,
            (planes, _) =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int at = y * planes.StrideY + x * 4;
                        planes.Y[at + 0] = (byte)(40 + 150 * x / width);
                        planes.Y[at + 1] = (byte)(60 + 120 * y / height);
                        planes.Y[at + 2] = (byte)(200 - 100 * (x + y) / (width + height));
                        planes.Y[at + 3] = 255;
                    }
                }
            });
}
