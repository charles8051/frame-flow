// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference;
using FrameFlow.Media;

namespace FrameFlow.Face;

/// <summary>
/// A rectangular region of a source frame, in source pixel coordinates,
/// that BlazeFace runs on. The <see cref="BlazeFacePreprocessor"/>
/// letterboxes it into the model input, and the
/// <see cref="BlazeFacePostprocessor"/> maps the model's outputs back
/// through the same letterbox, so a box decoded from the model lands in the
/// right place on the original frame.
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
    /// Maps a point in the ROI's own normalized <c>[0,1]</c> space to a
    /// source-frame pixel coordinate. This is not the model's input space,
    /// which is letterboxed: use the transform
    /// <see cref="BlazeFacePreprocessor.Preprocess(FrameFlow.Media.IVideoFrame, FaceRoi, Span{float})"/> returns for that.
    /// </summary>
    public (float X, float Y) ToSource(float normalizedX, float normalizedY)
        => (X + normalizedX * Width, Y + normalizedY * Height);

    /// <summary>The ROI as an unrotated crop.</summary>
    internal RotatedRect ToCrop() => RotatedRect.FromBounds(X, Y, Width, Height);
}
