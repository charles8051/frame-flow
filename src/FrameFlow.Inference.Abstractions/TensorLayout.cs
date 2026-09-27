// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>The memory layout <see cref="ImageToTensor"/> writes a three-channel image in.</summary>
public enum TensorLayout
{
    /// <summary>Channel-first, <c>[1, 3, H, W]</c>: one plane per channel.</summary>
    Nchw,

    /// <summary>Channel-last, <c>[1, H, W, 3]</c>: channels interleaved per pixel.</summary>
    Nhwc,
}
