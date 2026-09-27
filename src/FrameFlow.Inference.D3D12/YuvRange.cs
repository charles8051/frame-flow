// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.D3D12;

/// <summary>The range a frame's YUV samples cover.</summary>
public enum YuvRange
{
    /// <summary>Luma 16 to 235 and chroma 16 to 240, in 8-bit terms. The default, and the usual case for video.</summary>
    Limited,

    /// <summary>The whole 0 to 255 range.</summary>
    Full,
}
