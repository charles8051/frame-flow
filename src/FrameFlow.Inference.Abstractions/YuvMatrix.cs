// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>The matrix a stage that reads YUV frames converts their samples to RGB with.</summary>
public enum YuvMatrix
{
    /// <summary>
    /// ITU-R BT.601. The default, because it is what FrameFlow's CPU path gets from swscale for a
    /// stream that does not say (#388).
    /// </summary>
    Bt601,

    /// <summary>ITU-R BT.709, which most HD sources are encoded with.</summary>
    Bt709,
}
