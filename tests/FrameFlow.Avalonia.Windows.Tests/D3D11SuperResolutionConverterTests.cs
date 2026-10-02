using System.Runtime.InteropServices;
using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Decoding;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The D3D11 converter's video processor mode (#560), read the way the compositor reads the ring:
/// opened by its shared handle on another device on the same adapter, under the keyed mutex.
/// </summary>
/// <remarks>
/// Each test compares the video processor's ring with the shader's from the same decoded frame.
/// With the driver's extension off, the two differ only in how they filter. The video processor
/// reconstructs chroma differently at sharp colour edges (up to 188/255 there on the test pattern,
/// 2.6 on average, against 0.2 for luma), so the comparison is of what a colour-space mistake
/// would change: luma, and each channel's average. A BT.601 matrix or full-range input moves both.
/// </remarks>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class D3D11SuperResolutionConverterTests
{
    private const string Small = "test-video-h264-yuv420p.mp4"; // 320x240
    private const string FullHd = "test-1080p-h264-aac.mp4";

    [RequiresNvidiaD3D11VaFact(Small)]
    public async Task TheVideoProcessor_WithoutTheExtension_MatchesTheShader_AtTheFramesSize()
    {
        var frames = await Decode.FramesAsync(Small, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1);
        try
        {
            var frame = (GpuVideoFrame)frames[0];
            var (shader, processor) = Convert(
                frame,
                PresenterOutput.Shader(frame.Width, frame.Height),
                new PresenterOutput(true, frame.Width, frame.Height),
                extension: false);

            AssertSameColours(shader, processor);
        }
        finally
        {
            foreach (var f in frames)
                f.Dispose();
        }
    }

    [RequiresNvidiaD3D11VaFact(Small)]
    public async Task TheVideoProcessor_FillsARingAtTheOutputSize()
    {
        var frames = await Decode.FramesAsync(Small, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1);
        try
        {
            var frame = (GpuVideoFrame)frames[0];
            var size = new PresenterOutput(false, frame.Width * 2, frame.Height * 2);
            var (shader, processor) = Convert(frame, size, size with { SuperResolution = true }, extension: false);

            Assert.Equal(size.Width * size.Height * 4, processor.Length);
            AssertSameColours(shader, processor);
        }
        finally
        {
            foreach (var f in frames)
                f.Dispose();
        }
    }

    /// <summary>
    /// Whether the driver upscaled is only visible in the pixels: with the extension on and the
    /// upscale active, the output differs from plain scaling; inactive, it is identical. It is
    /// active only in a local session with the NVIDIA App's Super Resolution setting on, so the
    /// test runs only when <c>FRAMEFLOW_EXPECT_DRIVER_VSR=1</c> says the machine is set up for it.
    /// </summary>
    [RequiresNvidiaD3D11VaFact(FullHd, driverUpscale: true)]
    public async Task WithTheExtension_TheDriverChangesTheUpscaledPicture()
    {
        var frames = await Decode.FramesAsync(FullHd, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1);
        try
        {
            var frame = (GpuVideoFrame)frames[0];
            var output = new PresenterOutput(true, 3840, 2160);
            var plain = ConvertOne(frame, output, extension: false);
            var upscaled = ConvertOne(frame, output, extension: true);

            var (worst, mean) = Difference(plain, upscaled);
            Assert.True(mean > 0.1, $"the driver left the picture as plain scaling made it: mean {mean:F3}/255, worst {worst}");
        }
        finally
        {
            foreach (var f in frames)
                f.Dispose();
        }
    }

    private static (byte[] First, byte[] Second) Convert(
        GpuVideoFrame frame, PresenterOutput first, PresenterOutput second, bool extension) =>
        (ConvertOne(frame, first, extension), ConvertOne(frame, second, extension));

    private static byte[] ConvertOne(GpuVideoFrame frame, PresenterOutput output, bool extension)
    {
        Assert.True(frame.TryGetD3D11Texture(out nint texture, out int slice, out _));
        using var converter = new D3D11Nv12SharedConverter(
            texture, frame.Width, frame.Height, output, NullLogger.Instance, extension);
        Assert.False(converter.SuperResolutionFailed, "the video processor failed to set up");
        Assert.Equal(output, converter.Output);

        Assert.True(converter.ConvertInto(0, texture, slice));
        Assert.False(converter.SuperResolutionFailed, "the blit failed");

        using var compositor = new CompositorSide(texture);
        return compositor.Read(converter.GetSharedHandle(0), output.Width, output.Height);
    }

    /// <summary>
    /// Luma within 0.5/255 on average and each channel's average within 0.5/255: what a wrong matrix
    /// or range moves, and what different chroma filters do not.
    /// </summary>
    private static void AssertSameColours(byte[] shader, byte[] processor)
    {
        Assert.Equal(shader.Length, processor.Length);
        int pixels = shader.Length / 4;
        double lumaTotal = 0;
        var bias = new double[3];
        for (int i = 0; i < shader.Length; i += 4)
        {
            lumaTotal += Math.Abs(Luma(shader, i) - Luma(processor, i));
            for (int c = 0; c < 3; c++)
                bias[c] += processor[i + c] - shader[i + c];
        }

        double luma = lumaTotal / pixels;
        string channels = string.Join(", ", bias.Select(b => (b / pixels).ToString("F3")));
        Assert.True(
            luma < 0.5 && bias.All(b => Math.Abs(b / pixels) < 0.5),
            $"luma |difference| {luma:F3}/255 on average; channel bias B, G, R: {channels}");

        static double Luma(byte[] bgra, int at) => 0.0722 * bgra[at] + 0.7152 * bgra[at + 1] + 0.2126 * bgra[at + 2];
    }

    private static (int Worst, double Mean) Difference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int worst = 0;
        long total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (i % 4 == 3)
                continue; // alpha
            int d = Math.Abs(a[i] - b[i]);
            worst = Math.Max(worst, d);
            total += d;
        }

        return (worst, total / (a.Length * 0.75));
    }

    /// <summary>
    /// A D3D11 device on the decoder's adapter that opens a ring buffer by its shared handle, as the
    /// compositor does, takes key 1, copies it out and hands key 0 back.
    /// </summary>
    private sealed class CompositorSide : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;

        public CompositorSide(nint decodeTexture)
        {
            Marshal.AddRef(decodeTexture);
            using var texture = new ID3D11Texture2D(decodeTexture);
            using var dxgi = texture.Device.QueryInterface<IDXGIDevice>();
            dxgi.GetAdapter(out var adapter).CheckError();
            using (adapter)
            {
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
                    .CheckError();
                _device = device!;
            }

            _context = _device.ImmediateContext;
        }

        public unsafe byte[] Read(nint sharedHandle, int width, int height)
        {
            using var shown = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
            using var mutex = shown.QueryInterface<IDXGIKeyedMutex>();
            using var staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            // WAIT_TIMEOUT is a success code, which Vortice's AcquireSync does not surface.
            Assert.Equal(0, AcquireSync(mutex, 1, 5000));
            _context.CopyResource(staging, shown);
            mutex.ReleaseSync(0);

            var mapped = _context.Map(staging, 0, MapMode.Read);
            try
            {
                var pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, width * 4)
                        .CopyTo(pixels.AsSpan(y * width * 4));
                }

                return pixels;
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }

        public void Dispose()
        {
            _context.Dispose();
            _device.Dispose();
        }

        /// <summary><c>IDXGIKeyedMutex::AcquireSync</c>, vtable slot 8, for its HRESULT.</summary>
        private static unsafe int AcquireSync(IDXGIKeyedMutex mutex, ulong key, uint milliseconds)
        {
            nint* vtable = *(nint**)mutex.NativePointer;
            var acquire = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)vtable[8];
            return acquire(mutex.NativePointer, key, milliseconds);
        }
    }
}

/// <summary>
/// Skipped unless D3D11VA decodes <c>clip</c> on hardware here, on an NVIDIA adapter. With
/// <c>driverUpscale</c>, also skipped unless <c>FRAMEFLOW_EXPECT_DRIVER_VSR=1</c> says the driver's
/// super resolution is on and the session is local.
/// </summary>
internal sealed class RequiresNvidiaD3D11VaFactAttribute : FactAttribute
{
    public RequiresNvidiaD3D11VaFactAttribute(string clip, bool driverUpscale = false)
    {
        Skip = new RequiresHardwareDecodeFactAttribute(HardwareDecodeBackendKind.D3D11Va, clip).Skip;
        if (Skip is not null)
            return;

        var frames = Decode.FramesAsync(clip, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1)
            .GetAwaiter().GetResult();
        try
        {
            var frame = (GpuVideoFrame)frames[0];
            frame.TryGetD3D11Texture(out nint texture, out _, out _);
            if (D3D11Nv12SharedConverter.AdapterVendorOf(texture) != SuperResolutionPolicy.NvidiaVendorId)
            {
                Skip = "D3D11VA decodes on an adapter that is not NVIDIA's.";
                return;
            }
        }
        finally
        {
            foreach (var f in frames)
                f.Dispose();
        }

        if (driverUpscale && Environment.GetEnvironmentVariable("FRAMEFLOW_EXPECT_DRIVER_VSR") != "1")
        {
            Skip = "Set FRAMEFLOW_EXPECT_DRIVER_VSR=1 on an RTX GPU, in a local session, with Super Resolution on "
                + "in the NVIDIA App.";
        }
    }
}
