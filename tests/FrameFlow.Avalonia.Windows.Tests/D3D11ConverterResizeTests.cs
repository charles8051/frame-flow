using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Decoding;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The D3D11 converter resized in place for a playlist item of another size. A resized converter
/// has to present a frame exactly as a converter built for that frame does, when the decode device
/// changes along with the size, and in both directions. The clips are decoded one after the other,
/// so each has its own decode device, as consecutive playlist items do.
/// </summary>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class D3D11ConverterResizeTests
{
    private const string Small = "test-video-h264-yuv420p.mp4"; // 320x240
    private const string Large = "test-1080p-h264-aac.mp4"; // 1920x1080
    private const string TenBit = "test-video-vp9-yuv420p10.webm"; // P010 on D3D11VA

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D11Va, Large)]
    public async Task AResizedConverter_PresentsWhatAConverterBuiltForTheFrameDoes()
    {
        var small = await FirstFrameAsync(Small);
        var large = await FirstFrameAsync(Large);
        try
        {
            Assert.NotEqual(small.Device, large.Device);
            using var compositor = new D3D11CompositorSide(small.Texture);
            using var converter = Build(small);
            var smallAsBuilt = Present(converter, compositor, 0, small);

            using (var replaced = converter.TryResize(large.Texture, large.Frame.Width, large.Frame.Height))
                Assert.NotNull(replaced);
            Assert.Equal((large.Frame.Width, large.Frame.Height), (converter.Width, converter.Height));
            Assert.Equal(PresenterOutput.Shader(large.Frame.Width, large.Frame.Height), converter.Output);
            Assert.Equal(large.Device, converter.SourceDevicePointer);

            var largeResized = Present(converter, compositor, 1, large);
            using (var built = Build(large))
                AssertSame(Present(built, compositor, 0, large), largeResized, "up to 1920x1080");

            using (var replaced = converter.TryResize(small.Texture, small.Frame.Width, small.Frame.Height))
                Assert.NotNull(replaced);
            AssertSame(smallAsBuilt, Present(converter, compositor, 2, small), "back down to 320x240");
        }
        finally
        {
            small.Frame.Dispose();
            large.Frame.Dispose();
        }
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D11Va, TenBit)]
    public async Task AFrameInAnotherSampleFormat_IsNotResizedFor()
    {
        var large = await FirstFrameAsync(Large);
        var tenBit = await FirstFrameAsync(TenBit);
        try
        {
            Assert.Equal(PixelFormat.P010, tenBit.Frame.Format);
            using var compositor = new D3D11CompositorSide(large.Texture);
            using var converter = Build(large);
            var before = Present(converter, compositor, 0, large);

            Assert.Null(converter.TryResize(tenBit.Texture, tenBit.Frame.Width, tenBit.Frame.Height));

            Assert.Equal((large.Frame.Width, large.Frame.Height), (converter.Width, converter.Height));
            AssertSame(before, Present(converter, compositor, 1, large), "after the refused resize");
        }
        finally
        {
            large.Frame.Dispose();
            tenBit.Frame.Dispose();
        }
    }

    [RequiresNvidiaD3D11VaFact(Small)]
    public async Task TheVideoProcessorMode_IsNotResized()
    {
        var small = await FirstFrameAsync(Small);
        var large = await FirstFrameAsync(Large);
        try
        {
            var output = new PresenterOutput(true, small.Frame.Width * 2, small.Frame.Height * 2);
            using var converter = new D3D11Nv12SharedConverter(
                small.Texture, small.Frame.Width, small.Frame.Height, output, NullLogger.Instance);
            Assert.Equal(output, converter.Output);

            Assert.Null(converter.TryResize(large.Texture, large.Frame.Width, large.Frame.Height));
            Assert.Equal((small.Frame.Width, small.Frame.Height), (converter.Width, converter.Height));
        }
        finally
        {
            small.Frame.Dispose();
            large.Frame.Dispose();
        }
    }

    private sealed record Decoded(GpuVideoFrame Frame, nint Texture, int Slice, nint Device);

    private static async Task<Decoded> FirstFrameAsync(string clip)
    {
        var frames = await Decode.FramesAsync(clip, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1);
        var frame = (GpuVideoFrame)frames[0];
        Assert.True(frame.TryGetD3D11Texture(out nint texture, out int slice, out nint device));
        return new Decoded(frame, texture, slice, device);
    }

    private static D3D11Nv12SharedConverter Build(Decoded decoded) =>
        new(
            decoded.Texture,
            decoded.Frame.Width,
            decoded.Frame.Height,
            PresenterOutput.Shader(decoded.Frame.Width, decoded.Frame.Height),
            NullLogger.Instance);

    private static byte[] Present(D3D11Nv12SharedConverter converter, D3D11CompositorSide compositor, int buffer, Decoded decoded)
    {
        Assert.True(converter.ConvertInto(buffer, decoded.Texture, decoded.Slice));
        return compositor.Read(converter.GetSharedHandle(buffer), decoded.Frame.Width, decoded.Frame.Height);
    }

    private static void AssertSame(byte[] expected, byte[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        int differing = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                differing++;
        }

        Assert.True(differing == 0, $"{what}: {differing} of {expected.Length} bytes differ");
    }
}
