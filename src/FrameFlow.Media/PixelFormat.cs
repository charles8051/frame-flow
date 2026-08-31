// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Pixel format of a decoded video frame.
/// </summary>
public enum PixelFormat
{
    /// <summary>32-bit BGRA, 8 bits per channel. Default software decode output.</summary>
    Bgra32,

    /// <summary>32-bit RGBA, 8 bits per channel.</summary>
    Rgba32,

    /// <summary>Planar YUV 4:2:0. Native FFmpeg output before swscale conversion.</summary>
    Yuv420P,

    /// <summary>NV12 semi-planar YUV 4:2:0. Common hardware decoder output.</summary>
    Nv12,

    /// <summary>
    /// YUYV 4:2:2 packed. Byte order Y0 U0 Y1 V0 per 2-pixel macro. Native
    /// output of many USB webcams. Single contiguous plane; stride is
    /// <c>Width * 2</c>.
    /// </summary>
    Yuyv422,

    /// <summary>
    /// UYVY 4:2:2 packed. Byte order U0 Y0 V0 Y1 per 2-pixel macro. Common
    /// on capture cards (BMD, AVerMedia, etc.) and broadcast equipment.
    /// Single contiguous plane; stride is <c>Width * 2</c>.
    /// </summary>
    Uyvy422,
}
