// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Describes the format of a video stream, used to notify sinks of format changes.
/// </summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Format">Pixel format of the decoded frames.</param>
public sealed record VideoFormatInfo(int Width, int Height, PixelFormat Format);
