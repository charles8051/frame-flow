// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Playback.Core;

/// <summary>Whether a video decoder hands its path GPU frames, and why.</summary>
/// <param name="Yield">True when hardware-decoded frames stay on the GPU.</param>
/// <param name="Reason">Why, for a log line.</param>
internal readonly record struct HardwareFrameDecision(bool Yield, string Reason);

/// <summary>
/// Decides whether a player's or a pass's video decoder keeps hardware-decoded frames on the GPU
/// (#294). Pure: the caller's request and what a copy of the path says in, a decision out.
/// </summary>
/// <remarks>
/// A request wins. Without one, frames stay on the GPU when every node they reach has said it
/// takes GPU frames and the path holds a bounded number of them, since a hardware pool is sized
/// from that bound (ADR-0081). A node or sink that says nothing is taken to read pixels on the
/// CPU, so the default never hands one a frame it cannot read.
/// </remarks>
internal static class HardwareFrameChoice
{
    /// <summary>
    /// Whether the path needs a copy built before the decoder opens: to decide, or to size the
    /// pool for a request. Not when frames are downloaded anyway.
    /// </summary>
    public static bool NeedsProbe(bool? requested, bool hardwareDecodeDisabled) =>
        requested ?? !hardwareDecodeDisabled;

    /// <param name="requested">The caller's <c>WithHardwareFrames</c>, or null when it gave none.</param>
    /// <param name="hardwareDecodeDisabled">True when the decoder never decodes on hardware.</param>
    /// <param name="undeclared">
    /// The first node that GPU frames reach and that has not said it takes them, from the path's
    /// check with an undeclared node taking CPU frames; null when there is none.
    /// </param>
    /// <param name="budget">The most frames the path holds.</param>
    public static HardwareFrameDecision Decide(
        bool? requested,
        bool hardwareDecodeDisabled,
        FrameDomainMismatch? undeclared,
        FrameBudget? budget)
    {
        if (requested is { } explicitly)
        {
            return new HardwareFrameDecision(
                explicitly,
                explicitly ? "WithHardwareFrames(true)" : "WithHardwareFrames(false)");
        }

        if (hardwareDecodeDisabled)
            return new HardwareFrameDecision(false, "hardware decode is disabled");
        if (undeclared is not null)
            return new HardwareFrameDecision(false, $"'{undeclared.Node}' has not said it takes GPU frames");
        if (budget is { UnboundedHolder: { } holder })
            return new HardwareFrameDecision(false, $"'{holder}' holds frames without a bound");
        return new HardwareFrameDecision(true, "every node on the video path takes GPU frames");
    }
}
