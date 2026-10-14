// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Player.Core;

/// <summary>
/// When a pass lets go of the claims on its sinks (ADR-0085, decision 6): once it is disposed and
/// no run is in flight, whichever comes last. Pure: a state and an event in, a state and whether
/// to release out.
/// </summary>
/// <remarks>
/// A pass holds its claim until it is disposed, not until its run ends, so a sink serves the next
/// pass only once the caller is done with this one. Its dispose does not wait for a run still in
/// flight, and the frames that run sends would reach the sink after the release; so a run that
/// outlasts the dispose releases the claims when it ends instead.
/// </remarks>
/// <param name="Running">Whether a run is in flight.</param>
/// <param name="Disposed">Whether the pass has been disposed.</param>
/// <param name="Released">Whether the claims have been released.</param>
internal readonly record struct PassClaimHold(bool Running, bool Disposed, bool Released)
{
    /// <summary>A pass just built: holding its claims, with no run started.</summary>
    public static PassClaimHold Built => default;

    /// <summary>A run starts. Refused once the pass is disposed.</summary>
    public (PassClaimHold Next, bool Started) StartRun() =>
        Disposed ? (this, false) : (this with { Running = true }, true);

    /// <summary>The run in flight ended, however it ended.</summary>
    public (PassClaimHold Next, bool Release) EndRun() => Settle(this with { Running = false });

    /// <summary>The pass was disposed.</summary>
    public (PassClaimHold Next, bool Release) Dispose() => Settle(this with { Disposed = true });

    private static (PassClaimHold Next, bool Release) Settle(PassClaimHold next) =>
        next is { Disposed: true, Running: false, Released: false }
            ? (next with { Released = true }, true)
            : (next, false);
}
