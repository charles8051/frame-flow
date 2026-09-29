// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>What <see cref="ImageToTensor"/> writes where a crop reaches past the frame's edge.</summary>
public enum ImageBorder
{
    /// <summary>The nearest edge pixel, repeated outwards.</summary>
    Replicate,

    /// <summary>
    /// <see cref="ImageToTensorOptions.PadValue"/>, as in a letterbox bar, at every tensor pixel whose
    /// centre lands outside the frame. A pixel whose centre is inside still reads its neighbours
    /// with the edge repeated.
    /// </summary>
    Pad,
}
