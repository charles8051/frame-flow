// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback.Core;

/// <summary>The hardware decode backends a video decoder tries first, and why.</summary>
/// <param name="Preferred">
/// The backends to try first, in order. Empty for the platform default order. The decoder tries
/// the backends not in it afterwards, in the platform default order.
/// </param>
/// <param name="Reason">Why, for a log line.</param>
/// <param name="IsDefault">True when nothing asked for an order, so there is nothing to log.</param>
internal readonly record struct DecodeBackendDecision(
    IReadOnlyList<HardwareDecodeBackendKind> Preferred,
    string Reason,
    bool IsDefault);

/// <summary>
/// Decides which hardware decode backends a player's or a pass's video decoder tries first (#532).
/// Pure: the caller's request, the sink's preference and where frames go in, a decision out.
/// </summary>
/// <remarks>
/// A borrowed device fixes the backend, so it wins. Then a caller's request. Then the sink's
/// preference, but only when hardware frames stay on the GPU: a sink that prefers a backend
/// prefers its frames, and a path that downloads them is better served by the platform default,
/// which puts the backends that read back fastest first.
/// </remarks>
internal static class DecodeBackendOrder
{
    public static DecodeBackendDecision Decide(
        IReadOnlyList<HardwareDecodeBackendKind> requested,
        IReadOnlyList<HardwareDecodeBackendKind> sinkPreferred,
        bool framesStayOnGpu,
        HardwareDecodeBackendKind? borrowedDevice)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(sinkPreferred);

        if (borrowedDevice is { } device)
        {
            // The decoder ignores an order when it borrows a device. Say so only when an order was
            // asked for and is being set aside.
            return new DecodeBackendDecision(
                [],
                $"the borrowed device fixes it at {device}",
                IsDefault: requested.Count == 0 && sinkPreferred.Count == 0);
        }

        if (requested.Count > 0)
        {
            return new DecodeBackendDecision(
                requested, $"{Names(requested)} first, from WithPreferredBackends", IsDefault: false);
        }

        if (sinkPreferred.Count == 0)
            return new DecodeBackendDecision([], "the platform default", IsDefault: true);

        return framesStayOnGpu
            ? new DecodeBackendDecision(
                sinkPreferred, $"{Names(sinkPreferred)} first, from the video sink", IsDefault: false)
            : new DecodeBackendDecision(
                [],
                $"the platform default, since frames are downloaded; the video sink prefers "
                    + $"{Names(sinkPreferred)} only for frames it receives on the GPU",
                IsDefault: false);
    }

    private static string Names(IReadOnlyList<HardwareDecodeBackendKind> backends) =>
        string.Join(", ", backends);
}
