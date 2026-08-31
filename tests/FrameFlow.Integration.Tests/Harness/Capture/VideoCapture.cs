using FrameFlow.Media;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// One <see cref="IVideoFrame"/>'s worth of pixel data retained by
/// <see cref="CapturingVideoSink"/>. The pixel bytes are a heap copy
/// owned by the capture record so they survive the pool frame's
/// return to <see cref="CpuFramePool"/>.
/// </summary>
/// <param name="Pts">Frame presentation timestamp.</param>
/// <param name="Duration">Nominal frame duration (1 / fps).</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Format">Pixel format reported by the decoder.</param>
/// <param name="Pixels">
/// Packed pixel bytes, tightly packed at <c>Width × bytes-per-pixel</c>
/// stride. The capture copies the Y plane only for now — packed
/// formats round-trip; planar formats keep the Y plane and discard
/// chroma. Good enough for SSIM-class invariants; expand to full
/// planes if a test needs them.
/// </param>
internal readonly record struct VideoCapture(
    TimeSpan Pts,
    TimeSpan Duration,
    int Width,
    int Height,
    PixelFormat Format,
    byte[] Pixels
);
