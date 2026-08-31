// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Face;

/// <summary>
/// A rectangular region of a source frame, in source pixel coordinates,
/// that BlazeFace runs on. It is the single mapping shared by the
/// <see cref="BlazeFacePreprocessor"/> (which crops + stretches it to the
/// model input) and the <see cref="BlazeFacePostprocessor"/> (which maps
/// the model's normalized <c>[0,1]</c> outputs back into it) — so a box
/// decoded from the model lands in the right place on the original frame.
/// </summary>
/// <remarks>
/// In the gaze pipeline the ROI is the tracked person's box (optionally
/// its upper portion), letting BlazeFace search only where a face can be
/// rather than the whole frame. <see cref="Full"/> covers the entire
/// frame for the whole-image case.
/// </remarks>
public readonly record struct FaceRoi(float X, float Y, float Width, float Height)
{
    /// <summary>The whole frame as a ROI.</summary>
    public static FaceRoi Full(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new FaceRoi(0, 0, frame.Width, frame.Height);
    }

    /// <summary>
    /// Maps a point in the model's normalized <c>[0,1]</c> input space to
    /// a source-frame pixel coordinate. With the preprocessor's stretched
    /// resize this is exactly linear.
    /// </summary>
    public (float X, float Y) ToSource(float normalizedX, float normalizedY)
        => (X + normalizedX * Width, Y + normalizedY * Height);
}
