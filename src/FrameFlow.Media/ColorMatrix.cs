// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// The YCbCr matrix decoded frames convert to RGB with (#841): the one the stream signals, or a
/// fixed one the caller picks.
/// </summary>
/// <remarks>
/// The choice applies to every conversion of a decoded frame: the software decoder's
/// <see cref="PixelFormat.Bgra32"/> output, a hardware frame's readback, and the D3D11 and D3D12
/// presenters. A hardware frame and a software frame of the same stream therefore convert alike
/// whichever is picked.
/// </remarks>
public enum ColorMatrix
{
    /// <summary>
    /// The matrix the stream signals. A stream that signals none, or one that cannot be converted
    /// with, converts with BT.709 at 720 rows or more and with BT.601 below.
    /// </summary>
    Auto = 0,

    /// <summary>ITU-R BT.601, whatever the stream signals.</summary>
    Bt601,

    /// <summary>ITU-R BT.709, whatever the stream signals.</summary>
    Bt709,

    /// <summary>ITU-R BT.2020 non-constant luminance, whatever the stream signals.</summary>
    Bt2020,
}
