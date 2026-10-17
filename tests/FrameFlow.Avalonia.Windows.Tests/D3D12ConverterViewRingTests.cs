using System.Runtime.InteropServices;
using FrameFlow.Avalonia.Windows.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The D3D12 converter drawing into a ring sized to the view (#774), read back the way the
/// compositor reads it. The frame is a synthetic NV12 texture on a D3D12 device of the test's own,
/// so the pixels are known: one-pixel vertical stripes, black and white, the finest detail a frame
/// holds. The same cases as the D3D11 converter's (#767).
/// </summary>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class D3D12ConverterViewRingTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    // Studio-range black and white, and neutral chroma.
    private const byte Black = 16;
    private const byte White = 235;
    private const byte Neutral = 128;

    [RequiresD3D12HardwareFact]
    public void AtTheFramesSize_EveryStripeShows()
    {
        using var source = new SyntheticD3D12Nv12(Width, Height, (x, _) => x % 2 == 0 ? Black : White);
        using var converter = Build(source, new RingSize(Width, Height));
        using var compositor = new D3D11CompositorSide(source.D3D11Texture);

        var pixels = Draw(converter, compositor, source);

        // 1:1 the shader reads one texel centre per pixel, as it did before the ring followed the view.
        for (int x = 0; x < 64; x++)
            Assert.Equal(x % 2 == 0 ? 0 : 255, Green(pixels, Width, x, Height / 2));
    }

    [RequiresD3D12HardwareFact]
    public void Shrunk5To1_TheStripesAverageToGrey_InsteadOfAliasing()
    {
        using var source = new SyntheticD3D12Nv12(Width, Height, (x, _) => x % 2 == 0 ? Black : White);
        using var converter = Build(source, new RingSize(Width, Height));
        using var compositor = new D3D11CompositorSide(source.D3D11Texture);
        Draw(converter, compositor, source);

        // Resized for a 384x216 view: the draw target and the ring, with the pipeline kept.
        using (var retired = converter.TryResizeRing(new RingSize(384, 216)))
            Assert.NotNull(retired);
        Assert.Equal(new RingSize(384, 216), converter.Ring);
        var pixels = Draw(converter, compositor, source, 384, 216);

        // Each ring pixel covers five stripes, two or three of them white: 40% to 60% grey. One
        // bilinear tap per pixel lands on a single stripe instead, and the ring alternates black
        // and white.
        int worst = 0;
        for (int y = 0; y < 216; y += 7)
        {
            for (int x = 0; x < 384; x++)
                worst = Math.Max(worst, Math.Abs(Green(pixels, 384, x, y) - 128));
        }

        Assert.True(worst <= 40, $"a ring pixel is {worst} levels from mid grey");
    }

    [RequiresD3D12HardwareFact]
    public void ACrop_DrawsThePixelsTheWholeFrameHasThere()
    {
        // A pattern that differs in every column and row of the crop.
        using var source = new SyntheticD3D12Nv12(Width, Height, (x, y) => (byte)(Black + (x * 7 + y * 3) % (White - Black)));
        using var converter = Build(source, new RingSize(Width, Height));
        using var compositor = new D3D11CompositorSide(source.D3D11Texture);
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

    [RequiresD3D12HardwareFact]
    public void Shrunk_AnAlignedTexture_IsReadOnlyWhereTheFrameIs()
    {
        // A D3D12VA 1080p frame decodes into 1088 rows. Here the eight below the frame are white and
        // the frame is black. The area filter places the ring on the frame's rows, not the texture's.
        using var source = new SyntheticD3D12Nv12(Width, 1088, (_, y) => y < Height ? Black : White);
        using var converter = Build(source, new RingSize(384, 216));
        using var compositor = new D3D11CompositorSide(source.D3D11Texture);

        var pixels = Draw(converter, compositor, source, 384, 216);

        // The bottom ring row covers the frame's last five rows; past them are the white ones.
        for (int x = 0; x < 384; x += 11)
            Assert.Equal(0, Green(pixels, 384, x, 215));
    }

    private static D3D12YuvSharedConverter Build(SyntheticD3D12Nv12 source, RingSize ring) =>
        new(source.Texture, Width, Height, ring, NullLogger.Instance);

    private static byte[] Draw(
        D3D12YuvSharedConverter converter, D3D11CompositorSide compositor, SyntheticD3D12Nv12 source,
        int width = Width, int height = Height)
    {
        Assert.True(converter.ConvertInto(0, source.Texture, source.Fence, SyntheticD3D12Nv12.Written, () => new Held()));
        return compositor.Read(converter.GetSharedHandle(0), width, height);
    }

    // BGRA: grey is the same in every channel, so green stands for all three.
    private static int Green(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 1];

    /// <summary>What the converter holds for a draw: nothing, since the test owns its texture.</summary>
    private sealed class Held : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// An NV12 texture on a D3D12 device of its own, with the fence a decoder signals once the
    /// texture is written, as a D3D12VA frame has: luma from a function of the pixel, chroma
    /// neutral. A D3D11 device on the same adapter writes it and signals the fence.
    /// </summary>
    private sealed class SyntheticD3D12Nv12 : IDisposable
    {
        /// <summary>The value <see cref="Fence"/> reaches once the texture is written.</summary>
        public const ulong Written = 1;

        private readonly ID3D11Device _device11;
        private readonly ID3D11Texture2D _texture11;
        private readonly ID3D12Device _device12;
        private readonly ID3D12Resource _texture12;
        private readonly ID3D12Fence _fence;

        public unsafe SyntheticD3D12Nv12(int width, int height, Func<int, int, byte> luma)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
                .CheckError();
            _device11 = device!;
            using (var dxgi = _device11.QueryInterface<IDXGIDevice>())
            {
                dxgi.GetAdapter(out var adapter).CheckError();
                using (adapter)
                    D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out _device12!).CheckError();
            }

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
                _texture11 = _device11.CreateTexture2D(
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
                        MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
                    },
                    new Vortice.Direct3D11.SubresourceData((nint)p, (uint)width));
            }

            using (var resource = _texture11.QueryInterface<IDXGIResource1>())
            using (var handle = new SafeFileHandle(
                resource.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null), ownsHandle: true))
                _texture12 = _device12.OpenSharedHandle<ID3D12Resource>(handle.DangerousGetHandle());

            // The D3D11 device signals the fence once its write of the texture is done.
            _fence = _device12.CreateFence(0, Vortice.Direct3D12.FenceFlags.Shared);
            using (var device5 = _device11.QueryInterface<ID3D11Device5>())
            using (var handle = new SafeFileHandle(_device12.CreateSharedHandle(_fence, null, null!), ownsHandle: true))
            using (var fence11 = device5.OpenSharedFence<ID3D11Fence>(handle.DangerousGetHandle()))
            using (var context = _device11.ImmediateContext.QueryInterface<ID3D11DeviceContext4>())
            {
                context.Signal(fence11, Written);
                context.Flush();
            }
        }

        /// <summary>The texture as the D3D12 converter takes it: an <c>ID3D12Resource*</c>.</summary>
        public nint Texture => _texture12.NativePointer;

        /// <summary>The texture's <c>ID3D12Fence*</c>.</summary>
        public nint Fence => _fence.NativePointer;

        /// <summary>The texture on its D3D11 device, for <see cref="D3D11CompositorSide"/> to find the adapter.</summary>
        public nint D3D11Texture => _texture11.NativePointer;

        public void Dispose()
        {
            _fence.Dispose();
            _texture12.Dispose();
            _device12.Dispose();
            _texture11.Dispose();
            _device11.Dispose();
        }
    }
}

/// <summary>
/// Skipped unless a hardware adapter here runs both D3D12 and a D3D11 device that samples NV12.
/// CI has no GPU, so these tests skip there.
/// </summary>
internal sealed class RequiresD3D12HardwareFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(WhyNot);

    public RequiresD3D12HardwareFactAttribute() => Skip = Reason.Value;

    private static string? WhyNot()
    {
        if (new RequiresD3D11HardwareFactAttribute().Skip is { } d3d11)
            return d3d11;
        try
        {
            var created = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device);
            if (created.Failure || device is null)
                return "No hardware D3D11 device here.";
            using (device)
            using (var dxgi = device.QueryInterface<IDXGIDevice>())
            {
                dxgi.GetAdapter(out var adapter).CheckError();
                using (adapter)
                {
                    if (D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device12).Failure || device12 is null)
                        return "The hardware adapter here has no D3D12 device.";
                    device12.Dispose();
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
        {
            return $"D3D12 is not available: {ex.Message}";
        }
    }
}
