using System.Buffers;
using FrameFlow.Media;

namespace FrameFlow.Face.Tests;

/// <summary>
/// Test helpers for building small in-memory BGRA32 CPU frames without a
/// decoder, so the preprocessor can be exercised on known pixels.
/// </summary>
internal static class FaceTestFrames
{
    /// <summary>
    /// A <paramref name="width"/>×<paramref name="height"/> BGRA32 frame
    /// painted by <paramref name="paint"/>, which returns the (B,G,R,A)
    /// bytes for each pixel.
    /// </summary>
    public static CpuVideoFrame Bgra(
        int width,
        int height,
        Func<int, int, (byte B, byte G, byte R, byte A)> paint)
    {
        int stride = width * 4;
        var bytes = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (b, g, r, a) = paint(x, y);
                int o = y * stride + x * 4;
                bytes[o + 0] = b;
                bytes[o + 1] = g;
                bytes[o + 2] = r;
                bytes[o + 3] = a;
            }
        }

        return new CpuVideoFrame(
            new ArrayMemoryOwner(bytes),
            width,
            height,
            stride,
            PixelFormat.Bgra32,
            presentationTime: TimeSpan.Zero);
    }

    /// <summary>A solid-colour BGRA32 frame.</summary>
    public static CpuVideoFrame SolidBgra(int width, int height, byte b, byte g, byte r, byte a = 255)
        => Bgra(width, height, (_, _) => (b, g, r, a));

    private sealed class ArrayMemoryOwner(byte[] array) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => array;

        public void Dispose() { }
    }
}
