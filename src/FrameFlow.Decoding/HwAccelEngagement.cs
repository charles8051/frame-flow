namespace FrameFlow.Decoding;

/// <summary>
/// Decides whether a bound hardware backend is actually decoding, from the pixel
/// format of a frame it produced.
/// </summary>
/// <remarks>
/// <para>
/// Binding and engaging are two events, and only the first one is checkable at open
/// time. <c>TryBindSingle</c> returns a binding once <c>av_hwdevice_ctx_create</c>,
/// <c>avcodec_alloc_context3</c>, <c>avcodec_parameters_to_context</c> and
/// <c>avcodec_open2</c> have all succeeded. FFmpeg decides whether the hwaccel can
/// handle a particular stream later, in the <c>get_format</c> callback on the first
/// decoded frame, and falls back to software by returning a software pixel format.
/// </para>
/// <para>
/// A device that opens and is then refused per stream is ordinary. A Vulkan device
/// without <c>VK_KHR_video_decode_queue</c> opens fine and cannot decode; so does a
/// VAAPI device present without the codec's entrypoint. Before this check the
/// snapshot reported the bound backend in both cases, so a run measuring the
/// software path was attributed to hardware.
/// </para>
/// </remarks>
internal static class HwAccelEngagement
{
    /// <summary>
    /// Whether a frame in <paramref name="frameFormat"/> came from the hardware
    /// backend whose format is <paramref name="hwPixelFormat"/>.
    /// </summary>
    /// <param name="frameFormat">
    /// The decoded frame's <c>AVFrame.format</c>. Negative when FFmpeg left it unset.
    /// </param>
    /// <param name="hwPixelFormat">
    /// The bound backend's hardware pixel format, or a negative value when no
    /// hardware backend is bound.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the frame is on the hardware surface.
    /// <see langword="false"/> when it is a software frame, or when there is no
    /// hardware backend to have produced it.
    /// </returns>
    internal static bool IsHardwareFrame(int frameFormat, int hwPixelFormat) =>
        hwPixelFormat >= 0 && frameFormat == hwPixelFormat;
}
