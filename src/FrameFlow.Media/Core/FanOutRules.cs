// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>
/// What a <see cref="FanOutVideoSink"/> derives from its targets, and how it reports what they
/// threw (ADR-0085, decision 8). Pure: values in, a value out. The sink is the shell that reads
/// its targets, calls them and throws.
/// </summary>
internal static class FanOutRules
{
    /// <summary>
    /// The domains the fan-out takes: those every target takes, each target's being its
    /// transform's when it has one. <see cref="FrameMemoryDomains.None"/> when they share none.
    /// </summary>
    public static FrameMemoryDomains AcceptedDomains(IEnumerable<FrameMemoryDomains> eachTarget) =>
        eachTarget.Aggregate(FrameMemoryDomains.Any, static (all, one) => all & one);

    /// <summary>
    /// The positions of the targets that take domains <paramref name="accepted"/> leaves out: the
    /// targets the fan-out narrows.
    /// </summary>
    public static IReadOnlyList<int> Narrowed(
        IReadOnlyList<FrameMemoryDomains> eachTarget,
        FrameMemoryDomains accepted
    ) => [.. Enumerable.Range(0, eachTarget.Count).Where(i => (eachTarget[i] & ~accepted) != 0)];

    /// <summary>
    /// The most frames the fan-out keeps: the sum of what the targets keep, or
    /// <see langword="null"/> when any of them is unbounded. The targets hold the same frames, so
    /// the sum counts some twice, which is the safe direction for a pool sized from it.
    /// </summary>
    public static int? MaxHeldFrames(IEnumerable<int?> eachTarget)
    {
        var sum = 0;
        foreach (var held in eachTarget)
        {
            if (held is not { } kept)
                return null;
            sum += kept;
        }
        return sum;
    }

    /// <summary>The first non-empty list of preferred backends among the targets, in order.</summary>
    public static IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends(
        IEnumerable<IReadOnlyList<HardwareDecodeBackendKind>> eachTarget
    ) => eachTarget.FirstOrDefault(static backends => backends.Count > 0) ?? [];

    /// <summary>
    /// Whether a transform's <paramref name="output"/> keeps the format of the frame it was made
    /// from, so the format the target was told stays true: the same dimensions, pixel format, pixel
    /// shape and rotation, which is everything <see cref="VideoFormatInfo"/> carries.
    /// </summary>
    public static bool KeepsFormat(IVideoFrame incoming, IVideoFrame output) =>
        output.Width == incoming.Width
        && output.Height == incoming.Height
        && output.Format == incoming.Format
        && output.SampleAspectRatio == incoming.SampleAspectRatio
        && output.Rotation == incoming.Rotation;

    /// <summary>
    /// The one exception a call to every target ends in, given what they threw in order, or
    /// <see langword="null"/> when none threw.
    /// </summary>
    /// <remarks>
    /// One exception is thrown as it is. Several are thrown together as an
    /// <see cref="AggregateException"/>, in target order, unless every one of them is a
    /// cancellation: then the first is thrown as it is, so a present cancelled at every target
    /// reads as cancelled to a caller that catches <see cref="OperationCanceledException"/>.
    /// </remarks>
    public static Exception? Failure(IReadOnlyList<Exception> thrown) =>
        thrown.Count switch
        {
            0 => null,
            1 => thrown[0],
            _ when thrown.All(static ex => ex is OperationCanceledException) => thrown[0],
            _ => new AggregateException(
                $"{thrown.Count} of the fan-out's targets failed. Every target was called.",
                thrown
            ),
        };
}
