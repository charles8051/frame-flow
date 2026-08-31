using System.Buffers;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// Builds synthetic BGRA32 <see cref="IVideoFrame"/>s with per-frame-varying
/// content, for driving the encoder without a real decode source.
/// </summary>
internal static class SyntheticFrames
{
    /// <summary>
    /// Creates a one-shot BGRA32 frame filled with a moving gradient so the
    /// encoder sees genuine inter-frame change (not a degenerate constant
    /// image).
    /// </summary>
    internal static IVideoFrame CreateBgra(int width, int height, int frameIndex, int fps = 30)
    {
        int stride = width * 4;
        int size = stride * height;
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(size);
        Span<byte> px = owner.Memory.Span;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + (x * 4);
                px[i + 0] = (byte)((x + (frameIndex * 4)) & 0xFF); // B
                px[i + 1] = (byte)((y + (frameIndex * 2)) & 0xFF); // G
                px[i + 2] = (byte)((x + y + (frameIndex * 6)) & 0xFF); // R
                px[i + 3] = 0xFF; // A
            }
        }

        var pts = TimeSpan.FromSeconds(frameIndex / (double)fps);
        var duration = TimeSpan.FromSeconds(1.0 / fps);
        return new CpuVideoFrame(owner, width, height, stride, PixelFormat.Bgra32, pts, duration);
    }

    /// <summary>Creates a synthetic frame wrapped as a substrate <see cref="VideoFrameRef"/>.</summary>
    internal static VideoFrameRef CreateBgraRef(int width, int height, int frameIndex, int fps = 30) =>
        new(CreateBgra(width, height, frameIndex, fps));
}
