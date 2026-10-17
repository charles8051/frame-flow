// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>The matrix a stage that reads YUV frames converts their samples to RGB with.</summary>
public enum YuvMatrix
{
    /// <summary>
    /// ITU-R BT.601. The default, because it is what <c>VideoOperators.ToGpu</c> uploads with. A
    /// stage is not told which matrix a frame was decoded with (#388); the CPU path converts with
    /// the stream's own matrix, and with BT.709 for a stream that does not say (#796).
    /// </summary>
    Bt601,

    /// <summary>ITU-R BT.709, which most HD sources are encoded with.</summary>
    Bt709,
}
