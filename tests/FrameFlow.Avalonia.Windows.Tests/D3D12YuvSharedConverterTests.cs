using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// What the D3D12VA presenter puts in its ring (#429), read the way the compositor reads it: the
/// ring buffer opened by its shared handle on another D3D11 device, under the keyed mutex.
/// </summary>
public sealed class D3D12YuvSharedConverterTests
{
    // 320x240 H.264, BT.601 in the stream; the presenter converts as BT.709 whatever the stream says.
    private const string Clip = "test-video-h264-yuv420p.mp4";

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task EachBuffer_HoldsItsFramesBt709Conversion()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 4);
        try
        {
            var first = (GpuVideoFrame)frames[0];
            Assert.True(first.TryGetD3D12Texture(out nint texture, out _, out _, out _));
            using var converter = new D3D12YuvSharedConverter(texture, first.Width, first.Height, NullLogger.Instance);
            using var compositor = new CompositorSide(texture);
            var before = FrameCopyMetrics.Snapshot();

            for (int i = 0; i < frames.Count; i++)
            {
                var frame = (GpuVideoFrame)frames[i];
                int buffer = i % D3D12YuvSharedConverter.BufferCount;
                Assert.True(converter.ConvertInto(buffer, frame));

                var expected = Reference(Nv12Image.Read(frame));
                var presented = compositor.Read(converter.GetSharedHandle(buffer), frame.Width, frame.Height);
                AssertClose(expected, presented, $"frame {i}");
            }

            Assert.False(converter.IsDeviceLost);

            // One conversion on D3D12 and one copy across to D3D11 per frame (#435).
            var made = FrameCopyMetrics.Snapshot().Since(before);
            Assert.Equal(frames.Count, made[FrameCopySite.PresenterGpuConvert]);
            Assert.Equal(frames.Count, made[FrameCopySite.PresenterGpuCopy]);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    /// <summary>
    /// The GPU reads the decode texture after <see cref="D3D12YuvSharedConverter.ConvertInto"/>
    /// returns, and the presenter disposes its frame straight after, so the converter keeps the
    /// frame until a later conversion sees that its draw has finished.
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task AFrameDisposedAfterConversion_IsHeldUntilTheGpuHasReadIt()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 2);
        var (first, second) = ((GpuVideoFrame)frames[0], (GpuVideoFrame)frames[1]);
        Assert.True(first.TryGetD3D12Texture(out nint texture, out _, out _, out _));
        var converter = new D3D12YuvSharedConverter(texture, first.Width, first.Height, NullLogger.Instance);
        using var compositor = new CompositorSide(texture);

        Assert.True(converter.ConvertInto(0, first));
        first.Dispose();
        Assert.True(IsAlive(first), "the first frame was freed while its draw could still read it");

        // The compositor's copy waits for the converter's, which signals the fence after the first
        // draw: once this returns, that draw is finished.
        compositor.Read(converter.GetSharedHandle(0), first.Width, first.Height);

        Assert.True(converter.ConvertInto(1, second));
        second.Dispose();
        Assert.False(IsAlive(first), "the first frame outlived its draw");
        Assert.True(IsAlive(second));

        converter.Dispose();
        Assert.False(IsAlive(second), "disposing the converter kept the last frame");
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task FramesFromOneDecoder_ReportTheConvertersDevice()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 2);
        try
        {
            Assert.True(((GpuVideoFrame)frames[0]).TryGetD3D12Texture(out nint first, out _, out _, out _));
            Assert.True(((GpuVideoFrame)frames[1]).TryGetD3D12Texture(out nint second, out _, out _, out _));
            using var converter = new D3D12YuvSharedConverter(first, frames[0].Width, frames[0].Height, NullLogger.Instance);

            Assert.Equal(converter.SourceDevicePointer, D3D12YuvSharedConverter.DeviceIdentity(second));
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    private static bool IsAlive(GpuVideoFrame frame) => frame.TryGetD3D12Texture(out _, out _, out _, out _);

    /// <summary>
    /// The shader's arithmetic on the CPU: luma at its own sample, since the output is frame-sized;
    /// chroma bilinear with its samples centred on their 2x2 blocks, clamped to the frame's last
    /// sample; BT.709 studio range; rounded to 8 bits. BGRA.
    /// </summary>
    private static byte[] Reference(Nv12Image image)
    {
        int width = image.Width, height = image.Height;
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double luma = (image.Luma[y * width + x] / 255.0 - 16.0 / 255) * (255.0 / 219);
                double u = (Chroma(image, x, y, 0) / 255.0 - 128.0 / 255) * (255.0 / 224);
                double v = (Chroma(image, x, y, 1) / 255.0 - 128.0 / 255) * (255.0 / 224);

                int at = (y * width + x) * 4;
                bgra[at + 0] = Unorm(luma + 1.8556 * u);
                bgra[at + 1] = Unorm(luma - 0.1873 * u - 0.4681 * v);
                bgra[at + 2] = Unorm(luma + 1.5748 * v);
                bgra[at + 3] = 255;
            }
        }

        return bgra;
    }

    private static double Chroma(Nv12Image image, int x, int y, int component)
    {
        int columns = image.ChromaWidth, rows = image.ChromaHeight;
        double px = Math.Min((x + 0.5) / 2, columns - 0.5) - 0.5;
        double py = Math.Min((y + 0.5) / 2, rows - 0.5) - 0.5;
        int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
        double fx = px - x0, fy = py - y0;

        double Sample(int cx, int cy) =>
            image.Chroma[(Math.Clamp(cy, 0, rows - 1) * columns + Math.Clamp(cx, 0, columns - 1)) * 2 + component];

        double top = Sample(x0, y0) + (Sample(x0 + 1, y0) - Sample(x0, y0)) * fx;
        double bottom = Sample(x0, y0 + 1) + (Sample(x0 + 1, y0 + 1) - Sample(x0, y0 + 1)) * fx;
        return top + (bottom - top) * fy;
    }

    private static byte Unorm(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>
    /// Within 2 of 255 everywhere, and 0.1 on average (1 and 0.004 measured on a discrete NVIDIA GPU): the sampler's filter weights and the
    /// render target's rounding are the hardware's, not the reference's doubles.
    /// </summary>
    private static void AssertClose(byte[] expected, byte[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        int worst = 0;
        long total = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int difference = Math.Abs(expected[i] - actual[i]);
            worst = Math.Max(worst, difference);
            total += difference;
        }

        double mean = (double)total / expected.Length;
        Assert.True(worst <= 2 && mean < 0.1, $"{what}: worst |difference| {worst}/255, mean {mean:F3}/255");
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
            using var resource = new ID3D12Resource(decodeTexture);
            using var device12 = resource.GetDevice<ID3D12Device>();
            long luid = device12.AdapterLuid;
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>(
                new Vortice.Luid((uint)(luid & 0xFFFFFFFF), (int)(luid >> 32)));
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
                .CheckError();
            _device = device!;
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
