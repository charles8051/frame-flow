// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>The range a frame's YUV samples cover.</summary>
public enum YuvRange
{
    /// <summary>
    /// Luma 16 to 235 and chroma 16 to 240 in 8-bit terms, scaled with the bit depth (64 to 940
    /// and 64 to 960 at 10 bits). The default, and the usual case for video.
    /// </summary>
    Limited,

    /// <summary>Every code the bit depth has: 0 to 255 at 8 bits, 0 to 1023 at 10.</summary>
    Full,
}
