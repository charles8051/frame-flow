// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Encode-direction additions to the <c>libavutil</c> P/Invoke surface
/// (ADR-0040): input-frame buffer allocation and writability for the encoder's
/// reusable YUV420P source frame.
/// </summary>
/// <remarks>
/// Partial extension of <see cref="FFAvUtil"/>. Targets FFmpeg 7.x
/// (libavutil-59). Timestamp rescaling (<c>av_rescale_q</c>) and frame
/// lifecycle (<c>av_frame_alloc</c> / <c>av_frame_free</c> /
/// <c>av_frame_unref</c>) are declared in the existing
/// <c>FFAvUtil_Phase03.cs</c> and reused by the encode path.
/// </remarks>
internal static partial class FFAvUtil
{
    /// <summary>
    /// Allocates new buffers for an <c>AVFrame</c> whose <c>format</c>,
    /// <c>width</c>, and <c>height</c> (video) have already been set. Fills the
    /// <c>data</c> and <c>linesize</c> arrays. Used to back the encoder's
    /// reusable YUV420P source frame.
    /// </summary>
    /// <param name="frame">The frame to allocate buffers for.</param>
    /// <param name="align">
    /// Required buffer-size and stride alignment, or 0 to let FFmpeg pick an
    /// alignment appropriate for the current CPU.
    /// </param>
    /// <returns>0 on success; a negative AVERROR code on error.</returns>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_frame_get_buffer(nint frame, int align);

    /// <summary>
    /// Ensures the frame's data is writable, copying to a fresh buffer if the
    /// existing one is shared (reference count &gt; 1). Call before writing new
    /// pixels into a reused frame so an in-flight encoder reference is not
    /// clobbered.
    /// </summary>
    /// <returns>0 on success; a negative AVERROR code on error.</returns>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_frame_make_writable(nint frame);
}
