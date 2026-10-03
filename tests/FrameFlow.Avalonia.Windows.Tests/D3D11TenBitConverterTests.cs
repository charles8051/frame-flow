using System.Reflection;
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
/// The D3D11 converter on 10-bit frames (#559). D3D11VA decodes VP9 profile 2 into P010, which the
/// converter views as R16 and R16G16 planes and scales with P010's studio levels. Before, it viewed
/// the P010 staging texture as R8 and R8G8, and D3D11 refused the views.
/// </summary>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class D3D11TenBitConverterTests
{
    private const string TenBit = "test-video-vp9-yuv420p10.webm";

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D11Va, TenBit)]
    public async Task AP010Frame_IsConvertedToItsBt709Colours()
    {
        var frames = await Decode.FramesAsync(TenBit, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1);
        try
        {
            var first = (GpuVideoFrame)frames[0];
            Assert.Equal(PixelFormat.P010, first.Format);
            Assert.True(first.TryGetD3D11Texture(out nint firstTexture, out _, out _));
            using var converter = new D3D11Nv12SharedConverter(
                firstTexture, first.Width, first.Height, PresenterOutput.Shader(first.Width, first.Height), NullLogger.Instance);
            using var compositor = new D3D11CompositorSide(firstTexture);
            Assert.Equal(PixelFormat.P010, converter.InputFormat);

            for (int i = 0; i < frames.Count; i++)
            {
                var frame = (GpuVideoFrame)frames[i];
                Assert.True(frame.TryGetD3D11Texture(out nint texture, out int slice, out _));
                int buffer = i % D3D11Nv12SharedConverter.BufferCount;
                Assert.True(converter.ConvertInto(buffer, texture, slice));

                var presented = compositor.Read(converter.GetSharedHandle(buffer), frame.Width, frame.Height);
                AssertClose(Reference(P010Image.Read(frame)), presented, $"frame {i}");
            }
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D11Va, TenBit)]
    public void ATextureNeitherNv12NorP010_IsRefusedWhenTheConverterIsBuilt()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
            .CheckError();
        using (device)
        {
            using var texture = device!.CreateTexture2D(new Texture2DDescription
            {
                Width = 64,
                Height = 64,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });

            var refused = Assert.Throws<NotSupportedException>(() => new D3D11Nv12SharedConverter(
                texture.NativePointer, 64, 64, PresenterOutput.Shader(64, 64), NullLogger.Instance));
            Assert.Contains("NV12 and P010", refused.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The shader's arithmetic on the CPU, written out rather than taken from <see cref="YuvLevels"/>:
    /// a 16-bit sample read as <c>sample / 65535</c>, P010 studio range (luma 64 to 940 and chroma
    /// 64 to 960 in 10-bit codes, in the high bits), luma at its own sample, chroma bilinear with
    /// its samples centred on their 2x2 blocks, BT.709, rounded to 8 bits. BGRA.
    /// </summary>
    private static byte[] Reference(P010Image image)
    {
        int width = image.Width, height = image.Height;
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double luma = (image.Luma[y * width + x] / 65535.0 - 64 * 64 / 65535.0) * (65535.0 / (876 * 64));
                double u = (Chroma(image, x, y, 0) / 65535.0 - 512 * 64 / 65535.0) * (65535.0 / (896 * 64));
                double v = (Chroma(image, x, y, 1) / 65535.0 - 512 * 64 / 65535.0) * (65535.0 / (896 * 64));

                int at = (y * width + x) * 4;
                bgra[at + 0] = Unorm(luma + 1.8556 * u);
                bgra[at + 1] = Unorm(luma - 0.1873 * u - 0.4681 * v);
                bgra[at + 2] = Unorm(luma + 1.5748 * v);
                bgra[at + 3] = 255;
            }
        }

        return bgra;
    }

    private static double Chroma(P010Image image, int x, int y, int component)
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

    /// <summary>Within 2 of 255 everywhere and 0.1 on average, as the D3D12 converter's test allows.</summary>
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

    /// <summary>A hardware frame's P010 samples, read back to the CPU through FFmpeg.</summary>
    private sealed class P010Image
    {
        private static readonly PropertyInfo NativeAvFrame = typeof(GpuVideoFrame).GetProperty(
            "NativeAvFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;

        [DllImport("avutil-61", ExactSpelling = true)]
        private static extern nint av_frame_alloc();

        [DllImport("avutil-61", ExactSpelling = true)]
        private static extern void av_frame_free(ref nint frame);

        [DllImport("avutil-61", ExactSpelling = true)]
        private static extern int av_hwframe_transfer_data(nint destination, nint source, int flags);

        private P010Image(int width, int height, ushort[] luma, ushort[] chroma)
        {
            Width = width;
            Height = height;
            Luma = luma;
            Chroma = chroma;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>16-bit luma samples, the 10-bit code in the high bits, tightly packed.</summary>
        public ushort[] Luma { get; }

        /// <summary>Interleaved Cb and Cr at half resolution in each direction, tightly packed.</summary>
        public ushort[] Chroma { get; }

        public int ChromaWidth => (Width + 1) / 2;

        public int ChromaHeight => (Height + 1) / 2;

        /// <remarks><c>AVFrame.data[8]</c> is at offset 0 and <c>linesize[8]</c> at 64.</remarks>
        public static unsafe P010Image Read(GpuVideoFrame frame)
        {
            nint source = (nint)NativeAvFrame.GetValue(frame)!;
            nint cpu = av_frame_alloc();
            try
            {
                int result = av_hwframe_transfer_data(cpu, source, 0);
                if (result < 0)
                    throw new InvalidOperationException($"av_hwframe_transfer_data failed: {result}.");
                byte** data = (byte**)cpu;
                int* linesize = (int*)(cpu + 64);
                int width = frame.Width, height = frame.Height;
                int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
                var luma = new ushort[width * height];
                var chroma = new ushort[chromaWidth * chromaHeight * 2];
                for (int y = 0; y < height; y++)
                    new ReadOnlySpan<ushort>(data[0] + y * linesize[0], width).CopyTo(luma.AsSpan(y * width));
                for (int y = 0; y < chromaHeight; y++)
                {
                    new ReadOnlySpan<ushort>(data[1] + y * linesize[1], chromaWidth * 2)
                        .CopyTo(chroma.AsSpan(y * chromaWidth * 2));
                }

                return new P010Image(width, height, luma, chroma);
            }
            finally
            {
                av_frame_free(ref cpu);
            }
        }
    }
}
