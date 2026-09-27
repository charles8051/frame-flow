// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// How <see cref="ImageToTensor"/> reads the frame at a tensor pixel. Both map the tensor pixel's
/// centre into the frame, and both repeat the edge pixel for a position outside the frame.
/// </summary>
public enum ImageSampling
{
    /// <summary>The frame pixel the position falls in.</summary>
    Nearest,

    /// <summary>The four frame pixels around the position, weighted by distance.</summary>
    Bilinear,
}
