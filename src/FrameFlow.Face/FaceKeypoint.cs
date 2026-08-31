// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Face;

/// <summary>
/// The six facial landmarks BlazeFace regresses alongside each detection
/// box, in the model's output order. The indices are load-bearing — the
/// postprocessor reads keypoint <c>k</c> from output columns
/// <c>4 + 2k</c> (x) and <c>5 + 2k</c> (y), so this enum doubles as the
/// column map. Downstream head-pose geometry selects keypoints by name
/// rather than magic index.
/// </summary>
/// <remarks>
/// "Tragion" is the notch just above the ear canal — the two ear
/// tragions plus the eyes give the widest horizontal span on the face,
/// which is what makes a yaw estimate from these six points tractable.
/// </remarks>
public enum FaceKeypoint
{
    /// <summary>Subject's right eye (image-left for a face looking at the camera).</summary>
    RightEye = 0,

    /// <summary>Subject's left eye (image-right).</summary>
    LeftEye = 1,

    /// <summary>Nose tip.</summary>
    Nose = 2,

    /// <summary>Mouth centre.</summary>
    Mouth = 3,

    /// <summary>Subject's right ear tragion.</summary>
    RightEarTragion = 4,

    /// <summary>Subject's left ear tragion.</summary>
    LeftEarTragion = 5,
}
