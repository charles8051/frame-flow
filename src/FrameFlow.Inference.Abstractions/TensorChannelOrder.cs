// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>The order of the colour channels in a tensor <see cref="ImageToTensor"/> writes.</summary>
public enum TensorChannelOrder
{
    /// <summary>Red, green, blue.</summary>
    Rgb,

    /// <summary>Blue, green, red.</summary>
    Bgr,
}
