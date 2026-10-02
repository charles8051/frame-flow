// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>
/// The size a frame shows at once its pixel shape and rotation are applied (#542). Pure.
/// </summary>
internal static class DisplaySize
{
    /// <summary>
    /// <paramref name="width"/> scaled by <paramref name="sampleAspectRatio"/>, with the height
    /// kept, as players stretch an anamorphic frame. A quarter or three-quarter turn swaps the two.
    /// </summary>
    public static (int Width, int Height) Of(
        int width, int height, SampleAspectRatio sampleAspectRatio, VideoRotation rotation)
    {
        int scaled = sampleAspectRatio.IsSquare
            ? width
            : (int)Math.Round(width * sampleAspectRatio.Value, MidpointRounding.AwayFromZero);
        return IsQuarterTurn(rotation) ? (height, scaled) : (scaled, height);
    }

    /// <summary>
    /// The pixel shape that shows a frame scaled from <paramref name="fromWidth"/> x
    /// <paramref name="fromHeight"/> to <paramref name="toWidth"/> x <paramref name="toHeight"/> at
    /// the shape it had: <paramref name="sampleAspectRatio"/> times the change in the width-to-height
    /// ratio of the grid, in lowest terms. Square when that is square.
    /// </summary>
    public static SampleAspectRatio ScaledSampleAspectRatio(
        SampleAspectRatio sampleAspectRatio, int fromWidth, int fromHeight, int toWidth, int toHeight)
    {
        if (fromWidth <= 0 || fromHeight <= 0 || toWidth <= 0 || toHeight <= 0)
            return SampleAspectRatio.Square;
        var known = sampleAspectRatio.IsKnown ? sampleAspectRatio : SampleAspectRatio.Square;
        long numerator = (long)known.Numerator * fromWidth * toHeight;
        long denominator = (long)known.Denominator * fromHeight * toWidth;
        long divisor = GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
        if (numerator == denominator)
            return SampleAspectRatio.Square;
        if (numerator <= int.MaxValue && denominator <= int.MaxValue)
            return new SampleAspectRatio((int)numerator, (int)denominator);
        return Approximate((double)numerator / denominator);
    }

    // A ratio whose exact terms overflow an int, to four decimal places.
    private static SampleAspectRatio Approximate(double value)
    {
        long numerator = (long)Math.Round(value * 10_000);
        long divisor = GreatestCommonDivisor(numerator, 10_000);
        return new SampleAspectRatio((int)(numerator / divisor), (int)(10_000 / divisor));
    }

    private static long GreatestCommonDivisor(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    /// <summary>Whether <paramref name="rotation"/> swaps width and height.</summary>
    public static bool IsQuarterTurn(VideoRotation rotation) =>
        rotation is VideoRotation.Clockwise90 or VideoRotation.Clockwise270;
}
