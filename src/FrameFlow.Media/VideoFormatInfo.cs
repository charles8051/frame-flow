// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;

namespace FrameFlow.Media;

/// <summary>
/// Describes the format of a video stream, used to notify sinks of format changes.
/// </summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Format">Pixel format of the decoded frames.</param>
public sealed record VideoFormatInfo(int Width, int Height, PixelFormat Format)
{
    /// <summary>The shape of the frames' pixels. Square unless the source says otherwise.</summary>
    public SampleAspectRatio SampleAspectRatio { get; init; } = SampleAspectRatio.Square;

    /// <summary>How far the frames turn clockwise to display upright.</summary>
    public VideoRotation Rotation { get; init; }

    /// <summary>The width to show a frame at: <see cref="Width"/> scaled by the pixel shape, then rotated.</summary>
    public int DisplayWidth => DisplaySize.Of(Width, Height, SampleAspectRatio, Rotation).Width;

    /// <summary>The height to show a frame at, after rotation.</summary>
    public int DisplayHeight => DisplaySize.Of(Width, Height, SampleAspectRatio, Rotation).Height;
}
