// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// How a video view places the picture in its bounds when their shapes differ (#766). Either way
/// the picture keeps its display shape and is centred.
/// </summary>
public enum VideoPlacementMode
{
    /// <summary>The whole picture shows, with bars on the sides it does not reach.</summary>
    Fit = 0,

    /// <summary>The picture covers the bounds, and the overflow is cut off.</summary>
    Fill = 1,
}
