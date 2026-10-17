using System.Runtime.InteropServices;
using FrameFlow.Avalonia.Windows.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The D3D11 converter drawing into a ring sized to the view (#767), read back the way the
/// compositor reads it. The frame is a synthetic NV12 texture on a device of the test's own, so the
/// pixels are known: one-pixel vertical stripes, black and white, the finest detail a frame holds.
/// </summary>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class D3D11ConverterViewRingTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    // Studio-range black and white, and neutral chroma.
    private const byte Black = 16;
    private const byte White = 235;
    private const byte Neutral = 128;

    [RequiresD3D11HardwareFact]
    public void AtTheFramesSize_EveryStripeShows()
    {
        using var source = new SyntheticNv12(Width, Height, (x, _) => x % 2 == 0 ? Black : White);
        using var converter = Build(source, new RingSize(Width, Height));
        using var compositor = new D3D11CompositorSide(source.Texture);

        var pixels = Draw(converter, compositor, source);

        // 1:1 the shader reads one texel centre per pixel, as it did before the ring followed the view.
        for (int x = 0; x < 64; x++)
            Assert.Equal(x % 2 == 0 ? 0 : 255, Green(pixels, Width, x, Height / 2));
    }

    [RequiresD3D11HardwareFact]
    public void Shrunk5To1_TheStripesAverageToGrey_InsteadOfAliasing()
    {
        using var source = new SyntheticNv12(Width, Height, (x, _) => x % 2 == 0 ? Black : White);
        using var converter = Build(source, new RingSize(Width, Height));
        using var compositor = new D3D11CompositorSide(source.Texture);
        Draw(converter, compositor, source);

        // Resized for a 384x216 view: the ring alone, with the staging texture and bridge kept.
        using (var retired = converter.TryResizeRing(new RingSize(384, 216)))
            Assert.NotNull(retired);
        Assert.Equal(new PresenterOutput(false, 384, 216), converter.Output);
        var pixels = Draw(converter, compositor, source, 384, 216);

        // Each ring pixel covers five stripes, two or three of them white: 40% to 60% grey. One
        // bilinear tap per pixel lands on a single stripe instead, and the ring alternates black
        // and white, which is the aliasing the taps exist to stop.
        int worst = 0;
        for (int y = 0; y < 216; y += 7)
        {
            for (int x = 0; x < 384; x++)
                worst = Math.Max(worst, Math.Abs(Green(pixels, 384, x, y) - 128));
        }

        Assert.True(worst <= 40, $"a ring pixel is {worst} levels from mid grey");
    }

    [RequiresD3D11HardwareFact]
    public void ACrop_DrawsThePixelsTheWholeFrameHasThere()
    {
        // A pattern that differs in every column and row of the crop.
        using var source = new SyntheticNv12(Width, Height, (x, y) => (byte)(Black + (x * 7 + y * 3) % (White - Black)));
        using var converter = Build(source, new RingSize(Width, Height));
        using var compositor = new D3D11CompositorSide(source.Texture);
        var whole = Draw(converter, compositor, source);

        // The right half, as a view filled with a picture half as wide as the frame shows it.
        converter.SetSource(new SourceRect(960, 0, 960, Height));
        using (converter.TryResizeRing(new RingSize(960, Height)))
        {
        }

        var crop = Draw(converter, compositor, source, 960, Height);

        for (int y = 0; y < Height; y += 97)
        {
            for (int x = 0; x < 960; x += 13)
                Assert.Equal(Green(whole, Width, x + 960, y), Green(crop, 960, x, y));
        }
    }

    private static D3D11Nv12SharedConverter Build(SyntheticNv12 source, RingSize ring) =>
        new(source.Texture, Width, Height, new PresenterOutput(false, ring.Width, ring.Height), NullLogger.Instance);

    private static byte[] Draw(
        D3D11Nv12SharedConverter converter, D3D11CompositorSide compositor, SyntheticNv12 source,
        int width = Width, int height = Height)
    {
        Assert.True(converter.ConvertInto(0, source.Texture, 0));
        return compositor.Read(converter.GetSharedHandle(0), width, height);
    }

    // BGRA: grey is the same in every channel, so green stands for all three.
    private static int Green(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 1];

    /// <summary>
    /// An NV12 frame on a hardware D3D11 device of its own, as a decoder's texture would be: luma
    /// from a function of the pixel, chroma neutral.
    /// </summary>
    private sealed class SyntheticNv12 : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11Texture2D _texture;

        public unsafe SyntheticNv12(int width, int height, Func<int, int, byte> luma)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
                .CheckError();
            _device = device!;

            // NV12 in one buffer: the luma rows, then half as many interleaved chroma rows.
            var data = new byte[width * height * 3 / 2];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    data[y * width + x] = luma(x, y);
            }

            data.AsSpan(width * height).Fill(Neutral);
            fixed (byte* p = data)
            {
                _texture = _device.CreateTexture2D(
                    new Texture2DDescription
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.NV12,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                    },
                    new SubresourceData((nint)p, (uint)width));
            }
        }

        public nint Texture => _texture.NativePointer;

        public void Dispose()
        {
            _texture.Dispose();
            _device.Dispose();
        }
    }
}

/// <summary>
/// Skipped unless a hardware D3D11 device here samples NV12. The Microsoft Basic Render Driver
/// does not count: CI has no GPU, so these tests skip there.
/// </summary>
internal sealed class RequiresD3D11HardwareFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(WhyNot);

    public RequiresD3D11HardwareFactAttribute() => Skip = Reason.Value;

    private static string? WhyNot()
    {
        if (!OperatingSystem.IsWindows())
            return "D3D11 is Windows only.";
        try
        {
            var created = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device);
            if (created.Failure || device is null)
                return "No hardware D3D11 device here.";
            using (device)
            {
                using var dxgi = device.QueryInterface<IDXGIDevice>();
                dxgi.GetAdapter(out var adapter).CheckError();
                using (adapter)
                {
                    if (adapter.Description.VendorId == 0x1414)
                        return "The only D3D11 adapter here is Microsoft's software one.";
                }

                var support = device.CheckFormatSupport(Format.NV12);
                return (support & FormatSupport.ShaderSample) == 0 ? "The D3D11 device does not sample NV12." : null;
            }
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
        {
            return $"D3D11 is not available: {ex.Message}";
        }
    }
}
