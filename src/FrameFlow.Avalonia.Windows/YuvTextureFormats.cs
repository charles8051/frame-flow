// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Avalonia.Windows.Core;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// The plane views a presenter's shader reads a decode texture through, by the texture's format.
/// The D3D11 and D3D12 converters share it (#559).
/// </summary>
internal static class YuvTextureFormats
{
    /// <exception cref="NotSupportedException">The texture is neither NV12 nor P010.</exception>
    public static (YuvSampleFormat Samples, Format Luma, Format Chroma) For(Format texture) =>
        texture switch
        {
            Format.NV12 => (YuvSampleFormat.Nv12, Format.R8_UNorm, Format.R8G8_UNorm),
            Format.P010 => (YuvSampleFormat.P010, Format.R16_UNorm, Format.R16G16_UNorm),
            var other => throw new NotSupportedException($"The frame's texture is {other}; NV12 and P010 are supported."),
        };
}
