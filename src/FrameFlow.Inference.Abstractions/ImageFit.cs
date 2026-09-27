// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>How <see cref="ImageToTensor"/> fits a crop whose aspect differs from the tensor's.</summary>
public enum ImageFit
{
    /// <summary>Scale each axis on its own to fill the tensor. Distorts the aspect.</summary>
    Stretch,

    /// <summary>
    /// Scale both axes by the same factor, centre the crop, and fill the rest of the tensor with
    /// <see cref="ImageToTensorOptions.PadValue"/>.
    /// </summary>
    Letterbox,
}
