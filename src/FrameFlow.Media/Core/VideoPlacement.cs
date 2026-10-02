// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>
/// Where a presenter draws a frame in its bounds so it shows at its display shape and upright
/// (#542). Pure: sizes in, rectangles out, in the presenter's own units.
/// </summary>
/// <param name="DisplayX">Left of the area the picture covers, after rotation.</param>
/// <param name="DisplayY">Top of that area.</param>
/// <param name="DisplayWidth">Width of that area: the display width, fitted to the bounds.</param>
/// <param name="DisplayHeight">Height of that area.</param>
/// <param name="DrawX">Left of the frame's rectangle before rotation, centred on the area.</param>
/// <param name="DrawY">Top of that rectangle.</param>
/// <param name="DrawWidth">
/// Width of that rectangle. A quarter turn swaps it with <see cref="DrawHeight"/> relative to the
/// area, so the rectangle turned about its centre covers the area exactly.
/// </param>
/// <param name="DrawHeight">Height of that rectangle.</param>
/// <param name="RotationRadians">The clockwise turn to apply about the rectangle's centre, y down.</param>
internal readonly record struct VideoPlacement(
    double DisplayX,
    double DisplayY,
    double DisplayWidth,
    double DisplayHeight,
    double DrawX,
    double DrawY,
    double DrawWidth,
    double DrawHeight,
    double RotationRadians)
{
    /// <summary>Whether there is nothing to draw: no bounds, or no frame size.</summary>
    public bool IsEmpty => DisplayWidth <= 0 || DisplayHeight <= 0;

    /// <summary>
    /// Fits a <paramref name="codedWidth"/> x <paramref name="codedHeight"/> frame into the bounds,
    /// letterboxed, at the shape <paramref name="sampleAspectRatio"/> gives it and turned by
    /// <paramref name="rotation"/>. The bars split evenly on both sides.
    /// </summary>
    public static VideoPlacement Fit(
        double boundsWidth,
        double boundsHeight,
        int codedWidth,
        int codedHeight,
        SampleAspectRatio sampleAspectRatio,
        VideoRotation rotation)
    {
        if (boundsWidth <= 0 || boundsHeight <= 0 || codedWidth <= 0 || codedHeight <= 0)
            return default;

        // The frame's shape upright and before rotation: the width stretched by the pixel shape.
        double frameWidth = codedWidth * sampleAspectRatio.Value;
        double frameHeight = codedHeight;
        bool quarter = DisplaySize.IsQuarterTurn(rotation);
        double shownWidth = quarter ? frameHeight : frameWidth;
        double shownHeight = quarter ? frameWidth : frameHeight;

        double scale = Math.Min(boundsWidth / shownWidth, boundsHeight / shownHeight);
        double width = shownWidth * scale;
        double height = shownHeight * scale;
        double x = (boundsWidth - width) / 2;
        double y = (boundsHeight - height) / 2;

        double drawWidth = quarter ? height : width;
        double drawHeight = quarter ? width : height;
        double centreX = x + width / 2;
        double centreY = y + height / 2;

        return new VideoPlacement(
            x, y, width, height,
            centreX - drawWidth / 2, centreY - drawHeight / 2, drawWidth, drawHeight,
            (int)rotation * Math.PI / 180);
    }
}
