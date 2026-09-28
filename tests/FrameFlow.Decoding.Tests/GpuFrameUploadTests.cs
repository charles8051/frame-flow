using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// <see cref="GpuFrameUpload"/> puts a CPU frame on a device as an NV12 <see cref="GpuVideoFrame"/>
/// that reads back as the original (#293). Runs alone: it reads the process-wide copy counts.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class GpuFrameUploadTests(FfmpegBootstrapFixture fixture) : IClassFixture<FfmpegBootstrapFixture>
{
    // Only a gate: a device of the backend initialises and decodes here.
    private const string Fixture = "test-video-h264-yuv420p.mp4";

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, Fixture)]
    public void OnD3D11Va_AnUploadedFrame_IsOnTheDevice_AndReadsBackAsTheOriginal() =>
        UploadedFrame_IsOnTheDevice_AndReadsBackAsTheOriginal(HardwareDecodeBackendKind.D3D11Va);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, Fixture)]
    public void OnD3D12Va_AnUploadedFrame_IsOnTheDevice_AndReadsBackAsTheOriginal() =>
        UploadedFrame_IsOnTheDevice_AndReadsBackAsTheOriginal(HardwareDecodeBackendKind.D3D12Va);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Cuda, Fixture)]
    public void OnCuda_AnUploadedFrame_IsOnTheDevice_AndReadsBackAsTheOriginal() =>
        UploadedFrame_IsOnTheDevice_AndReadsBackAsTheOriginal(HardwareDecodeBackendKind.Cuda);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, Fixture)]
    public void AnOddSizedFrame_KeepsItsSize()
    {
        _ = fixture;
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        var upload = new GpuFrameUpload(device);
        using var original = Gradient(65, 49);

        using var uploaded = upload.Upload(original);
        using var readBack = uploaded.ReadbackToCpuBgra32();

        Assert.Equal((65, 49), (uploaded.Width, uploaded.Height));
        AssertClose(original, readBack);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, Fixture)]
    public void ANewSize_TakesANewPool_AndFramesOfTheOldOneStayValid()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        var upload = new GpuFrameUpload(device);
        using var large = Gradient(64, 48);
        using var small = Gradient(32, 16);

        using var first = upload.Upload(large);
        using var second = upload.Upload(small);

        Assert.NotEqual(first.HwFramesContext, second.HwFramesContext);
        AssertClose(large, first.ReadbackToCpuBgra32());
        AssertClose(small, second.ReadbackToCpuBgra32());
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, Fixture)]
    public void EveryFormatACpuFrameHolds_Uploads()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        var upload = new GpuFrameUpload(device);
        PixelFormat[] formats =
            [PixelFormat.Bgra32, PixelFormat.Rgba32, PixelFormat.Yuv420P, PixelFormat.Nv12, PixelFormat.Yuyv422, PixelFormat.Uyvy422];

        foreach (var format in formats)
        {
            using var frame = CpuVideoFrame.Create(format, 32, 16, TimeSpan.Zero, TimeSpan.Zero, 0, static (planes, _) =>
            {
                planes.Y.Fill(128);
                planes.U.Fill(128);
                planes.V.Fill(128);
            });

            using var uploaded = upload.Upload(frame);

            Assert.Equal((PixelFormat.Nv12, 32, 16), (uploaded.Format, uploaded.Width, uploaded.Height));
        }
    }

    private static void UploadedFrame_IsOnTheDevice_AndReadsBackAsTheOriginal(HardwareDecodeBackendKind backend)
    {
        using var device = HardwareDevice.Create(backend);
        var upload = new GpuFrameUpload(device);
        using var original = Gradient(64, 48);
        var before = FrameCopyMetrics.Snapshot();

        using var uploaded = upload.Upload(original);

        var made = FrameCopyMetrics.Snapshot().Since(before);
        Assert.Equal(1, made[FrameCopySite.UploadConvert]);
        Assert.Equal(1, made[FrameCopySite.Upload]);
        Assert.Equal(backend, uploaded.Backend);
        Assert.Equal(PixelFormat.Nv12, uploaded.Format);
        Assert.Equal(FrameMemoryDomain.Gpu, uploaded.MemoryDomain);
        Assert.Equal((64, 48), (uploaded.Width, uploaded.Height));
        Assert.Equal(original.Pts, uploaded.Pts);
        Assert.Equal(device.ContextPointer, uploaded.HwDeviceContext);
        switch (backend)
        {
            case HardwareDecodeBackendKind.D3D11Va:
                Assert.True(uploaded.TryGetD3D11Texture(out _, out _, out _));
                break;
            case HardwareDecodeBackendKind.D3D12Va:
                Assert.True(uploaded.TryGetD3D12Texture(out _, out _, out _, out _));
                break;
        }

        using var readBack = uploaded.ReadbackToCpuBgra32();
        AssertClose(original, readBack);
    }

    /// <summary>
    /// Smooth ramps in every channel, so the round trip through NV12's shared chroma costs only
    /// its rounding.
    /// </summary>
    private static CpuVideoFrame Gradient(int width, int height) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32, width, height, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), 0,
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

    /// <summary>BGRA to NV12 and back, on a smooth image: within a few levels everywhere.</summary>
    private static void AssertClose(CpuVideoFrame expected, CpuVideoFrame actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        var e = expected.AsCpu()!.Value;
        var a = actual.AsCpu()!.Value;
        int worst = 0;
        long total = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            var eRow = e.PlaneY.Span.Slice(y * e.StrideY, expected.Width * 4);
            var aRow = a.PlaneY.Span.Slice(y * a.StrideY, expected.Width * 4);
            for (int i = 0; i < eRow.Length; i++)
            {
                if (i % 4 == 3)
                    continue;
                int difference = Math.Abs(eRow[i] - aRow[i]);
                worst = Math.Max(worst, difference);
                total += difference;
            }
        }

        double mean = (double)total / (expected.Width * expected.Height * 3);
        Assert.True(worst <= 12 && mean < 3, $"worst |difference| {worst}, mean {mean:F2}");
    }
}
