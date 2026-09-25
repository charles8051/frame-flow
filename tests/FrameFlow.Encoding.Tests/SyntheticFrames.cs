
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
    internal static IVideoFrame CreateBgra(int width, int height, int frameIndex, int fps = 30) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            width,
            height,
            TimeSpan.FromSeconds(frameIndex / (double)fps),
            TimeSpan.FromSeconds(1.0 / fps),
            frameIndex,
            static (planes, frameIndex) =>
            {
                var px = planes.Y;
                for (int y = 0; y < planes.Height; y++)
                {
                    int row = y * planes.StrideY;
                    for (int x = 0; x < planes.Width; x++)
                    {
                        int i = row + (x * 4);
                        px[i + 0] = (byte)((x + (frameIndex * 4)) & 0xFF); // B
                        px[i + 1] = (byte)((y + (frameIndex * 2)) & 0xFF); // G
                        px[i + 2] = (byte)((x + y + (frameIndex * 6)) & 0xFF); // R
                        px[i + 3] = 0xFF; // A
                    }
                }
            }
        );
}
