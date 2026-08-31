using FrameFlow.Media;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// Shared helper for packing an <see cref="IVideoFrame"/>'s Y plane into a
/// tightly-packed byte buffer for capture. Used by both
/// <see cref="CapturingVideoSink"/> (live capture) and
/// <see cref="ReferenceDecoder"/> (reference capture) so the two sides
/// produce byte-identical <see cref="VideoCapture"/> records for the same
/// underlying frame.
/// </summary>
/// <remarks>
/// The packed model intentionally drops chroma for planar formats. Tests
/// that need chroma fidelity can extend the model with a planar variant.
/// </remarks>
internal static class FramePacker
{
    public static VideoCapture Pack(IVideoFrame frame)
    {
        var cpu = frame.AsCpu();
        if (cpu is null)
        {
            // Non-CPU frame in a CPU-only test path — capture metadata only.
            return new VideoCapture(
                Pts: frame.Pts,
                Duration: frame.Duration,
                Width: frame.Width,
                Height: frame.Height,
                Format: frame.Format,
                Pixels: Array.Empty<byte>()
            );
        }

        var data = cpu.Value;
        var bytesPerRow = data.Width * BytesPerPixel(frame.Format);
        var pixels = new byte[bytesPerRow * data.Height];

        for (int y = 0; y < data.Height; y++)
        {
            var srcRow = data.PlaneY.Slice(y * data.StrideY, bytesPerRow);
            srcRow.Span.CopyTo(pixels.AsSpan(y * bytesPerRow));
        }

        return new VideoCapture(
            Pts: frame.Pts,
            Duration: frame.Duration,
            Width: frame.Width,
            Height: frame.Height,
            Format: frame.Format,
            Pixels: pixels
        );
    }

    public static int BytesPerPixel(PixelFormat format) =>
        format switch
        {
            PixelFormat.Bgra32 or PixelFormat.Rgba32 => 4,
            // Planar YUV (Yuv420P) and semi-planar (Nv12): Y plane is
            // 1 byte/pixel. Chroma is dropped; a chroma-aware capture
            // variant can be added when a test needs it.
            PixelFormat.Yuv420P or PixelFormat.Nv12 => 1,
            _ => 1,
        };
}
