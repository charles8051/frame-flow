// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Source-generated P/Invoke declarations for <c>libswscale</c>.
/// </summary>
/// <remarks>
/// Phase 03 surface: pixel-format conversion context allocation, pixel conversion,
/// and context cleanup.
/// All pointer parameters are <see cref="nint"/>; raw pointer values must not escape
/// outside <c>FrameFlow.Native</c> and <c>FrameFlow.Decoding</c> (ADR-0005).
/// Targets FFmpeg 7.x (libswscale-8).
/// </remarks>
internal static partial class FFSwScale
{
    /// <summary>
    /// Allocates and initialises a scaling/conversion context that converts pixels
    /// from <paramref name="srcW"/>×<paramref name="srcH"/> in
    /// <paramref name="srcFormat"/> to <paramref name="dstW"/>×<paramref name="dstH"/>
    /// in <paramref name="dstFormat"/>.
    /// Returns the context pointer on success, or <see cref="nint.Zero"/> on failure.
    /// Caller must free with <see cref="sws_freeContext"/>.
    /// </summary>
    /// <param name="srcFormat">Source pixel format (FFmpeg <c>AVPixelFormat</c> integer).</param>
    /// <param name="dstFormat">Destination pixel format (FFmpeg <c>AVPixelFormat</c> integer).</param>
    /// <param name="flags">Algorithm flags; use <c>SWS_BILINEAR</c> (2) for a good default.</param>
    [LibraryImport("swscale")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint sws_getContext(
        int srcW,
        int srcH,
        int srcFormat,
        int dstW,
        int dstH,
        int dstFormat,
        int flags,
        nint srcFilter,
        nint dstFilter,
        nint param
    );

    /// <summary>
    /// Converts a source plane array to the destination plane array using the
    /// previously allocated scaling context.
    /// </summary>
    /// <param name="ctx">The conversion context from <see cref="sws_getContext"/>.</param>
    /// <param name="srcSlice">Pointer to an array of source plane pointers.</param>
    /// <param name="srcStride">Pointer to an array of source plane strides (bytes per row).</param>
    /// <param name="srcSliceY">Top row of the source slice (usually 0).</param>
    /// <param name="srcSliceH">Height of the source slice in rows.</param>
    /// <param name="dst">Pointer to an array of destination plane pointers.</param>
    /// <param name="dstStride">Pointer to an array of destination plane strides.</param>
    /// <returns>The number of output rows written, or a negative value on error.</returns>
    [LibraryImport("swscale")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial int sws_scale(
        nint ctx,
        byte** srcSlice,
        int* srcStride,
        int srcSliceY,
        int srcSliceH,
        byte** dst,
        int* dstStride
    );

    /// <summary>
    /// Frees the scaling context. Safe to call with a <see cref="nint.Zero"/> pointer.
    /// </summary>
    [LibraryImport("swscale")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void sws_freeContext(nint ctx);

    // -------------------------------------------------------------------------
    // AVPixelFormat constants used in Phase 03
    // -------------------------------------------------------------------------

    /// <summary>AV_PIX_FMT_YUV420P — planar YUV 4:2:0, most common software decode output.</summary>
    internal const int AvPixFmtYuv420P = 0;

    /// <summary>AV_PIX_FMT_YUVJ420P — full-range variant of YUV 4:2:0.</summary>
    internal const int AvPixFmtYuvj420P = 12;

    /// <summary>AV_PIX_FMT_NV12 — semi-planar YUV 4:2:0 (common hardware decoder output).</summary>
    internal const int AvPixFmtNv12 = 23;

    /// <summary>AV_PIX_FMT_BGRA — packed BGRA 8:8:8:8, output format used by FrameFlow.</summary>
    internal const int AvPixFmtBgra = 28;

    /// <summary>AV_PIX_FMT_RGBA — packed RGBA 8:8:8:8.</summary>
    internal const int AvPixFmtRgba = 26;

    /// <summary>AV_PIX_FMT_YUYV422 — packed YUYV 4:2:2 (Y0 U0 Y1 V0). Common USB webcam output.</summary>
    internal const int AvPixFmtYuyv422 = 1;

    /// <summary>
    /// AV_PIX_FMT_UYVY422 — packed UYVY 4:2:2 (U0 Y0 V0 Y1). Common capture-card
    /// output. NOTE: enum 17 is <c>AV_PIX_FMT_BGR8</c>, not UYVY — the correct
    /// FFmpeg 7.x value is <b>15</b>. Getting this wrong renders camera frames
    /// with visible luma but rainbow-scrambled chroma (swscale interprets the
    /// 2-byte UYVY surface as 1-byte BGR8 and reads sideways through it).
    /// </summary>
    internal const int AvPixFmtUyvy422 = 15;

    /// <summary>SWS_BILINEAR — bilinear interpolation flag for <see cref="sws_getContext"/>.</summary>
    internal const int SwsBilinear = 2;
}
