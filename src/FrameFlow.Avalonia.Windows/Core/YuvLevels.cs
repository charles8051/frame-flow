// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Avalonia.Windows.Core;

/// <summary>How a decode texture stores its YUV samples.</summary>
internal enum YuvSampleFormat
{
    /// <summary>NV12: 8-bit samples, read through a UNORM view as <c>code / 255</c>.</summary>
    Nv12,

    /// <summary>P010: 10-bit codes in the high bits of 16-bit samples, read as <c>code · 64 / 65535</c>.</summary>
    P010,
}

/// <summary>
/// How a presenter's pixel shader turns a sample, read through a UNORM view, into studio-range
/// luma and chroma: luma is <c>(sample - YOffset) · YScale</c>, from 0 at black to 1 at white, and
/// chroma is <c>(sample - COffset) · CScale</c>, from -0.5 to 0.5. Pure: a sample format in, four
/// values out. The D3D11 and D3D12 presenters share it (#559).
/// </summary>
/// <remarks>Laid out as the D3D11 shader's constant buffer reads it.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct YuvLevels(float YOffset, float YScale, float COffset, float CScale)
{
    public static YuvLevels For(YuvSampleFormat samples)
    {
        // A code's value as the shader reads it, and the studio-range levels in codes.
        var (codeValue, black, luma, neutral, chroma) = samples switch
        {
            YuvSampleFormat.Nv12 => (1.0 / 255, 16.0, 219.0, 128.0, 224.0),
            YuvSampleFormat.P010 => (64.0 / 65535, 64.0, 876.0, 512.0, 896.0),
            _ => throw new ArgumentOutOfRangeException(nameof(samples), samples, "Undefined sample format."),
        };

        return new YuvLevels(
            YOffset: (float)(black * codeValue),
            YScale: (float)(1 / (luma * codeValue)),
            COffset: (float)(neutral * codeValue),
            CScale: (float)(1 / (chroma * codeValue)));
    }
}
