// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// The shape of one coded pixel, as width to height: 1:1 for square pixels, 32:27 for a 720x480
/// frame meant to be shown at 16:9 (#542).
/// </summary>
/// <param name="Numerator">The pixel's relative width.</param>
/// <param name="Denominator">The pixel's relative height.</param>
/// <remarks>
/// A term that is zero or negative means the source did not say. Such a ratio is not
/// <see cref="IsKnown"/> and is treated as square.
/// </remarks>
public readonly record struct SampleAspectRatio(int Numerator, int Denominator)
{
    /// <summary>Square pixels, 1:1.</summary>
    public static SampleAspectRatio Square { get; } = new(1, 1);

    /// <summary>Whether both terms are positive.</summary>
    public bool IsKnown => Numerator > 0 && Denominator > 0;

    /// <summary>Whether the pixels are square, which an unknown ratio is taken to be.</summary>
    public bool IsSquare => !IsKnown || Numerator == Denominator;

    /// <summary>The ratio as a number, or 1 when it is not known.</summary>
    public double Value => IsKnown ? (double)Numerator / Denominator : 1.0;

    /// <inheritdoc />
    public override string ToString() => $"{Numerator}:{Denominator}";
}
