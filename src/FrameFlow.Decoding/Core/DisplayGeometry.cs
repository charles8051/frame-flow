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
    /// <summary>
    /// The quarter turn a display matrix asks for, from FFmpeg's reading of it: the
    /// counterclockwise degrees <c>av_display_rotation_get</c> returns, negated and rounded as
    /// ffplay does, taken clockwise. An angle not within a degree of a quarter turn, or NaN for no
    /// matrix, is <see cref="VideoRotation.None"/>.
    /// </summary>
    public static VideoRotation RotationOf(double counterclockwiseDegrees)
    {
        if (double.IsNaN(counterclockwiseDegrees) || double.IsInfinity(counterclockwiseDegrees))
            return VideoRotation.None;

        double degrees = -Math.Round(counterclockwiseDegrees);
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
    /// frame's, otherwise the codec parameters', otherwise square, in lowest terms. The first two
    /// are FFmpeg's <c>av_guess_sample_aspect_ratio</c>. The third keeps a frame that lost its
    /// value in agreement with the stream it came from, which reports the codec's.
    /// </summary>
    /// <param name="container">The stream's <c>sample_aspect_ratio</c>.</param>
    /// <param name="frame">The frame's, or before a frame exists the codec parameters'.</param>
    /// <param name="codec">The codec parameters', the last resort.</param>
    public static SampleAspectRatio Resolve(
        (int Num, int Den) container, (int Num, int Den) frame, (int Num, int Den) codec = default)
    {
        if (Reduce(container) is { IsKnown: true } fromContainer)
            return fromContainer;
        if (Reduce(frame) is { IsKnown: true } fromFrame)
            return fromFrame;
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

    private static bool Near(double degrees, double target) => Math.Abs(degrees - target) < 1.0;
}
