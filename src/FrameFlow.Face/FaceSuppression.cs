// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Face;

/// <summary>How <see cref="BlazeFacePostprocessor"/> merges detections of the same face.</summary>
public enum FaceSuppression
{
    /// <summary>Keep the highest-scoring detection and drop those that overlap it.</summary>
    Hard,

    /// <summary>
    /// Replace the highest-scoring detection and those that overlap it with their score-weighted
    /// average box and keypoints, keeping the highest score. MediaPipe's face detector does this.
    /// </summary>
    Weighted,
}
