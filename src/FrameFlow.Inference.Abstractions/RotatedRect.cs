// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Inference;

/// <summary>
/// A rectangle in frame pixel coordinates, rotated about its centre. It is the region
/// <see cref="ImageToTensor"/> samples into a model input.
/// </summary>
/// <param name="CenterX">The centre's x, in frame pixels.</param>
/// <param name="CenterY">The centre's y, in frame pixels.</param>
/// <param name="Width">The width along the rectangle's own x axis, in frame pixels.</param>
/// <param name="Height">The height along the rectangle's own y axis, in frame pixels.</param>
/// <param name="Rotation">
/// Radians, clockwise as the frame is displayed (y points down). The rectangle's x axis points
/// along <c>(cos θ, sin θ)</c> in the frame. The tensor is written upright relative to the
/// rectangle, so a subject tilted by θ comes out level.
/// </param>
public readonly record struct RotatedRect(
    float CenterX,
    float CenterY,
    float Width,
    float Height,
    float Rotation = 0f)
{
    /// <summary>An axis-aligned rectangle from its top-left corner and size.</summary>
    public static RotatedRect FromBounds(float x, float y, float width, float height)
        => new(x + width / 2f, y + height / 2f, width, height);

    /// <summary>The whole of <paramref name="frame"/>.</summary>
    public static RotatedRect Whole(IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return FromBounds(0, 0, frame.Width, frame.Height);
    }
}
