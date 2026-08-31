// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Yolo;

/// <summary>
/// A single detection: a class label, a confidence score, and a bounding
/// box in the source image's pixel coordinates.
/// </summary>
public readonly record struct Detection(
    int ClassId,
    string ClassName,
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height
);
