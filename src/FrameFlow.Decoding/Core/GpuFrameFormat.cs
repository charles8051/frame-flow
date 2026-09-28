// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Media;

namespace FrameFlow.Decoding.Core;

/// <summary>
/// The <see cref="PixelFormat"/> a hardware frame reports, from its pool's <c>sw_format</c>
/// (#430). Pure.
/// </summary>
internal static class GpuFrameFormat
{
    /// <summary>
    /// The format for <paramref name="softwareFormat"/>, an <c>AVPixelFormat</c>, or
    /// <see langword="null"/> when <see cref="PixelFormat"/> has no member for it.
    /// </summary>
    public static PixelFormat? From(int softwareFormat) =>
        softwareFormat switch
        {
            (int)AVPixelFormat.AV_PIX_FMT_NV12 => PixelFormat.Nv12,
            (int)AVPixelFormat.AV_PIX_FMT_P010LE => PixelFormat.P010,
            _ => null,
        };
}
