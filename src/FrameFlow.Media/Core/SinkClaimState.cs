// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>What a try-claim on a sink came to.</summary>
internal enum SinkClaimOutcome
{
    /// <summary>The claim was taken, and the claimant holds the sink.</summary>
    Taken = 0,

    /// <summary>Another claimant holds the sink. Nothing changed.</summary>
    Held = 1,

    /// <summary>The sink is disposed and takes no claim. Nothing changed.</summary>
    Disposed = 2,
}

/// <summary>
/// Who holds a sink, and whether the sink is disposed (ADR-0085, decision 6). Pure: a state and an
/// event in, a state out. <see cref="SinkClaimGate"/> is the shell that holds it.
/// </summary>
/// <remarks>
/// Each claim taken gets the next number. A lease keeps the number of the claim it took and
/// releases by it, so a lease released after its claim has ended cannot end a later one. Closing
/// the sink refuses new claims and leaves a standing claim to its lease.
/// </remarks>
/// <param name="Claim">The number of the claim taken last, from 1; 0 before the first.</param>
/// <param name="Claimant">
/// Who holds the sink, as their claim described them; <see langword="null"/> when nobody does.
/// </param>
/// <param name="Closed">Whether the sink is disposed.</param>
internal readonly record struct SinkClaimState(long Claim, string? Claimant, bool Closed)
{
    /// <summary>A sink nobody has claimed, not disposed.</summary>
    public static SinkClaimState Open => default;

    /// <summary>Whether a claimant holds the sink.</summary>
    public bool IsHeld => Claimant is not null;

    /// <summary>
    /// <paramref name="claimant"/> tries to claim the sink. It takes the next claim number when the
    /// sink is free and not disposed.
    /// </summary>
    public (SinkClaimState Next, SinkClaimOutcome Outcome) TryClaim(string claimant)
    {
        if (Closed)
            return (this, SinkClaimOutcome.Disposed);
        if (IsHeld)
            return (this, SinkClaimOutcome.Held);
        return (this with { Claim = Claim + 1, Claimant = claimant }, SinkClaimOutcome.Taken);
    }

    /// <summary>Whether <paramref name="claim"/> is the claim that holds the sink.</summary>
    public bool Holds(long claim) => IsHeld && Claim == claim;

    /// <summary>
    /// The lease of <paramref name="claim"/> is released. The sink is free afterwards if that claim
    /// held it; otherwise nothing changes.
    /// </summary>
    public SinkClaimState Release(long claim) => Holds(claim) ? this with { Claimant = null } : this;

    /// <summary>The sink is disposed. A claim that holds it stands until its lease is released.</summary>
    public SinkClaimState Close() => this with { Closed = true };
}
