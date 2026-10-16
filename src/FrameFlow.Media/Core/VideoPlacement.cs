// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>
/// Where a presenter draws a frame in its bounds so it shows at its display shape and upright
/// (#542), fitted or filling them (#766). Pure: sizes in, rectangles out, in the presenter's own
/// units.
/// </summary>
/// <param name="DisplayX">Left of the area the picture covers, after rotation.</param>
/// <param name="DisplayY">Top of that area.</param>
/// <param name="DisplayWidth">Width of that area: the display width, scaled to the bounds.</param>
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
    /// Places a frame by <paramref name="mode"/>: <see cref="Fit"/> or <see cref="Fill"/> (#766).
    /// </summary>
    public static VideoPlacement Of(
        VideoPlacementMode mode,
        double boundsWidth,
        double boundsHeight,
        int codedWidth,
        int codedHeight,
        SampleAspectRatio sampleAspectRatio,
        VideoRotation rotation) =>
        Place(
            boundsWidth, boundsHeight, codedWidth, codedHeight, sampleAspectRatio, rotation,
            cover: mode == VideoPlacementMode.Fill);

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
        VideoRotation rotation) =>
        Place(boundsWidth, boundsHeight, codedWidth, codedHeight, sampleAspectRatio, rotation, cover: false);

    /// <summary>
    /// Scales a <paramref name="codedWidth"/> x <paramref name="codedHeight"/> frame, at the shape
    /// <paramref name="sampleAspectRatio"/> gives it and turned by <paramref name="rotation"/>, to
    /// cover the bounds (#766). The area overflows the bounds evenly on both sides of one axis, so
    /// its origin is negative on that axis.
    /// </summary>
    public static VideoPlacement Fill(
        double boundsWidth,
        double boundsHeight,
        int codedWidth,
        int codedHeight,
        SampleAspectRatio sampleAspectRatio,
        VideoRotation rotation) =>
        Place(boundsWidth, boundsHeight, codedWidth, codedHeight, sampleAspectRatio, rotation, cover: true);

    /// <summary>
    /// The part of this placement inside a <paramref name="boundsWidth"/> x
    /// <paramref name="boundsHeight"/> view, for the <paramref name="codedWidth"/> x
    /// <paramref name="codedHeight"/> frame it placed: which coded pixels show, and the rectangle
    /// they draw at (#766). A fitted placement shows the whole frame at its own draw rectangle.
    /// </summary>
    public VisibleVideo Visible(double boundsWidth, double boundsHeight, int codedWidth, int codedHeight)
    {
        if (IsEmpty || codedWidth <= 0 || codedHeight <= 0)
            return default;

        // What the bounds cut off each side of the area. A fitted area lands inside the bounds up
        // to rounding, which must not crop a sliver.
        double left = Cut(-DisplayX);
        double top = Cut(-DisplayY);
        double right = Cut(DisplayX + DisplayWidth - boundsWidth);
        double bottom = Cut(DisplayY + DisplayHeight - boundsHeight);
        double width = DisplayWidth - left - right;
        double height = DisplayHeight - top - bottom;
        if (width <= 0 || height <= 0)
            return default;

        // The visible span of the area, 0 to 1 across it, taken back through the turn to the span
        // of the frame as coded. A clockwise quarter turn puts the frame's left edge at the top.
        double u0 = left / DisplayWidth, u1 = 1 - right / DisplayWidth;
        double v0 = top / DisplayHeight, v1 = 1 - bottom / DisplayHeight;
        var rotation = (VideoRotation)(int)Math.Round(RotationRadians * 180 / Math.PI);
        var (s0, s1, t0, t1) = rotation switch
        {
            VideoRotation.Clockwise90 => (v0, v1, 1 - u1, 1 - u0),
            VideoRotation.Clockwise180 => (1 - u1, 1 - u0, 1 - v1, 1 - v0),
            VideoRotation.Clockwise270 => (1 - v1, 1 - v0, u0, u1),
            _ => (u0, u1, v0, v1),
        };

        bool quarter = DisplaySize.IsQuarterTurn(rotation);
        double drawWidth = quarter ? height : width;
        double drawHeight = quarter ? width : height;
        double centreX = DisplayX + left + width / 2;
        double centreY = DisplayY + top + height / 2;

        return new VisibleVideo(
            s0 * codedWidth, t0 * codedHeight, (s1 - s0) * codedWidth, (t1 - t0) * codedHeight,
            centreX - drawWidth / 2, centreY - drawHeight / 2, drawWidth, drawHeight,
            RotationRadians);

        static double Cut(double overflow) => overflow > 1e-9 ? overflow : 0;
    }

    private static VideoPlacement Place(
        double boundsWidth,
        double boundsHeight,
        int codedWidth,
        int codedHeight,
        SampleAspectRatio sampleAspectRatio,
        VideoRotation rotation,
        bool cover)
    {
        if (boundsWidth <= 0 || boundsHeight <= 0 || codedWidth <= 0 || codedHeight <= 0)
            return default;

        // The frame's shape upright and before rotation: the width stretched by the pixel shape.
        double frameWidth = codedWidth * sampleAspectRatio.Value;
        double frameHeight = codedHeight;
        bool quarter = DisplaySize.IsQuarterTurn(rotation);
        double shownWidth = quarter ? frameHeight : frameWidth;
        double shownHeight = quarter ? frameWidth : frameHeight;

        double scaleX = boundsWidth / shownWidth;
        double scaleY = boundsHeight / shownHeight;
        double scale = cover ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
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

/// <summary>
/// The part of a placed frame that shows in the view's bounds (#766). Pure. Filling, it is the
/// crop; fitted, it is the whole frame. A presenter that draws only what shows samples the source
/// rectangle into the draw rectangle and turns that about its centre.
/// </summary>
/// <param name="SourceX">Left of the frame's visible part, in coded pixels.</param>
/// <param name="SourceY">Top of that part, in coded pixels.</param>
/// <param name="SourceWidth">Width of that part, in coded pixels.</param>
/// <param name="SourceHeight">Height of that part, in coded pixels.</param>
/// <param name="DrawX">Left of the rectangle that part draws at before rotation, in view units.</param>
/// <param name="DrawY">Top of that rectangle.</param>
/// <param name="DrawWidth">
/// Width of that rectangle. Turned about its centre by <see cref="RotationRadians"/>, it covers the
/// placement's display area intersected with the bounds.
/// </param>
/// <param name="DrawHeight">Height of that rectangle.</param>
/// <param name="RotationRadians">The clockwise turn to apply about the rectangle's centre, y down.</param>
internal readonly record struct VisibleVideo(
    double SourceX,
    double SourceY,
    double SourceWidth,
    double SourceHeight,
    double DrawX,
    double DrawY,
    double DrawWidth,
    double DrawHeight,
    double RotationRadians)
{
    /// <summary>Whether nothing shows.</summary>
    public bool IsEmpty => DrawWidth <= 0 || DrawHeight <= 0;

    /// <summary>
    /// The draw rectangle's size in physical pixels on a display scaled by
    /// <paramref name="scaling"/>, before rotation, rounded to the nearest pixel: the size of a
    /// surface that holds only what shows, drawn 1:1. Zero when nothing shows.
    /// </summary>
    public (int Width, int Height) PhysicalSize(double scaling) =>
        IsEmpty || !(scaling > 0)
            ? (0, 0)
            : ((int)Math.Round(DrawWidth * scaling), (int)Math.Round(DrawHeight * scaling));
}
