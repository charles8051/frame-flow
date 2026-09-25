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
        => CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            width,
            height,
            TimeSpan.Zero,
            TimeSpan.Zero,
            paint,
            static (planes, paint) =>
            {
                var px = planes.Y;
                for (int y = 0; y < planes.Height; y++)
                {
                    for (int x = 0; x < planes.Width; x++)
                    {
                        var (b, g, r, a) = paint(x, y);
                        int o = y * planes.StrideY + x * 4;
                        px[o + 0] = b;
                        px[o + 1] = g;
                        px[o + 2] = r;
                        px[o + 3] = a;
                    }
                }
            });

    /// <summary>A solid-colour BGRA32 frame.</summary>
    public static CpuVideoFrame SolidBgra(int width, int height, byte b, byte g, byte r, byte a = 255)
        => Bgra(width, height, (_, _) => (b, g, r, a));
}
