// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A sink whose claim is a claim on the sinks it presents to, such as decision 8's fan-out
/// (ADR-0085). <see cref="SinkClaimSet"/> claims its targets in its place, flattened through
/// nested forwarding sinks, so every claim on several sinks takes the sinks that hold state in one
/// global order.
/// </summary>
/// <remarks>
/// <para>
/// Internal, because the order is global only if every claimant flattens the same way, so the
/// flattening stays inside the library.
/// </para>
/// <para>
/// The forwarding sink's own <see cref="IClaimableSink.TryClaim"/> claims
/// <see cref="ClaimTargets"/> through <see cref="SinkClaimSet"/>. The
/// <see cref="IClaimableSink.ClaimOrder"/> it reports is its own, from a
/// <see cref="SinkClaimGate"/> or the same sequence, and a claim set never uses it. A forwarding
/// sink that guards state of its own lists a claimable for that state among its targets.
/// </para>
/// </remarks>
internal interface IClaimForwardingSink : IClaimableSink
{
    /// <summary>
    /// The claimable sinks a claim on this one takes, fixed at construction. They may forward
    /// again; a set of forwarding sinks fixed at construction cannot form a cycle.
    /// </summary>
    IReadOnlyList<IClaimableSink> ClaimTargets { get; }
}
