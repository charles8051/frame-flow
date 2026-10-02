// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// How far a frame turns clockwise to display upright, as a source's display matrix says (#542).
/// Each value is the angle in degrees.
/// </summary>
public enum VideoRotation
{
    /// <summary>The frame displays as coded.</summary>
    None = 0,

    /// <summary>A quarter turn clockwise. A phone held upright usually records this.</summary>
    Clockwise90 = 90,

    /// <summary>A half turn.</summary>
    Clockwise180 = 180,

    /// <summary>Three quarter turns clockwise, a quarter turn counterclockwise.</summary>
    Clockwise270 = 270,
}
