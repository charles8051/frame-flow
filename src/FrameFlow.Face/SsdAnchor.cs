// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Face;

/// <summary>
/// One SSD prior box in normalized <c>[0,1]</c> model-input coordinates.
/// BlazeFace's regressor outputs are offsets <i>relative to these
/// anchors</i>, so the anchor table is required to decode a raw model
/// output into a box — it is as load-bearing as the weights themselves.
/// </summary>
/// <remarks>
/// With <see cref="SsdAnchorOptions.FixedAnchorSize"/> (the BlazeFace
/// setting), every anchor has <see cref="Width"/> = <see cref="Height"/> =
/// 1, so only the centres vary. The width/height fields are kept for
/// fidelity to MediaPipe's generator and for any future non-fixed model.
/// </remarks>
public readonly record struct SsdAnchor(float XCenter, float YCenter, float Width, float Height);
