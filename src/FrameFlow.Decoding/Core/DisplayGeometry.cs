// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding.Core;

/// <summary>
/// How a decoded frame is meant to be shown, from what the container and the codec say (#542).
/// Pure: the raw FFmpeg values in, a <see cref="SampleAspectRatio"/> or a <see cref="VideoRotation"/>
/// out.
/// </summary>
internal static class DisplayGeometry
{
    private const double Fixed16 = 65536.0;

    /// <summary>
    /// The quarter turn a display matrix asks for, read as FFmpeg's own player reads it: the
    /// angle of the matrix's first column, rounded to a degree, taken clockwise. An angle that is
    /// not within a degree of a quarter turn, a degenerate matrix, or a short one is
    /// <see cref="VideoRotation.None"/>. A mirroring matrix is read for its rotation only.
    /// </summary>
    /// <param name="matrix">
    /// The nine values of <c>AV_PKT_DATA_DISPLAYMATRIX</c>, row-major; the first two columns are
    /// 16.16 fixed point.
    /// </param>
    public static VideoRotation RotationOf(ReadOnlySpan<int> matrix)
    {
        if (matrix.Length < 9)
            return VideoRotation.None;

        // av_display_rotation_get returns the counterclockwise angle; ffplay negates it. This is
        // that negation, computed directly.
        double scaleX = Math.Sqrt(Square(matrix[0] / Fixed16) + Square(matrix[3] / Fixed16));
        double scaleY = Math.Sqrt(Square(matrix[1] / Fixed16) + Square(matrix[4] / Fixed16));
        if (scaleX == 0 || scaleY == 0)
            return VideoRotation.None;

        double degrees = Math.Round(
            Math.Atan2(matrix[1] / Fixed16 / scaleY, matrix[0] / Fixed16 / scaleX) * 180 / Math.PI);
        double clockwise = degrees - 360 * Math.Floor(degrees / 360 + 0.9 / 360);

        return clockwise switch
        {
            _ when Near(clockwise, 90) => VideoRotation.Clockwise90,
            _ when Near(clockwise, 180) => VideoRotation.Clockwise180,
            _ when Near(clockwise, 270) => VideoRotation.Clockwise270,
            _ => VideoRotation.None,
        };
    }

    /// <summary>
    /// The pixel shape to show a frame with: the container's when it gives one, otherwise the
    /// codec's or the frame's, otherwise square. This is FFmpeg's
    /// <c>av_guess_sample_aspect_ratio</c>, reduced to lowest terms.
    /// </summary>
    /// <param name="container">The stream's <c>sample_aspect_ratio</c>.</param>
    /// <param name="codec">The frame's, or before a frame exists the codec parameters'.</param>
    public static SampleAspectRatio Resolve((int Num, int Den) container, (int Num, int Den) codec)
    {
        if (Reduce(container) is { IsKnown: true } fromContainer)
            return fromContainer;
        return Reduce(codec) is { IsKnown: true } fromCodec ? fromCodec : SampleAspectRatio.Square;
    }

    private static SampleAspectRatio Reduce((int Num, int Den) ratio)
    {
        if (ratio.Num <= 0 || ratio.Den <= 0)
            return default;
        int divisor = GreatestCommonDivisor(ratio.Num, ratio.Den);
        return new SampleAspectRatio(ratio.Num / divisor, ratio.Den / divisor);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    private static double Square(double value) => value * value;

    private static bool Near(double degrees, double target) => Math.Abs(degrees - target) < 1.0;
}
