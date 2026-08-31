// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Face;

/// <summary>
/// A single face detection: a confidence score, a bounding box, and the
/// six <see cref="FaceKeypoint"/> landmarks — all in the source image's
/// pixel coordinates. Analogous to <c>FrameFlow.Yolo.Detection</c>, but
/// carries keypoints instead of a class label (BlazeFace is single-class:
/// "face").
/// </summary>
/// <remarks>
/// The keypoint array always has exactly six entries, indexed by
/// <see cref="FaceKeypoint"/>. Use <see cref="Keypoint"/> to read one by
/// name rather than indexing the array with a raw integer.
/// </remarks>
public readonly record struct FaceDetection(
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height,
    IReadOnlyList<FaceKeypoint2D> Keypoints
)
{
    /// <summary>Number of landmarks BlazeFace regresses per face.</summary>
    public const int KeypointCount = 6;

    /// <summary>Reads the pixel position of a named landmark.</summary>
    public FaceKeypoint2D Keypoint(FaceKeypoint which) => Keypoints[(int)which];
}

/// <summary>
/// A single facial landmark in source-image pixel coordinates.
/// </summary>
public readonly record struct FaceKeypoint2D(float X, float Y);
