// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Core;

/// <summary>
/// A session's runs in progress and whether it is disposed (#432). Disposing while a run is in
/// progress frees nothing; the run that ends last does. Pure: the session holds this under its
/// lock and frees its native state when a transition says to.
/// </summary>
/// <param name="Runs">Runs in progress.</param>
/// <param name="Disposed">Whether the session has been disposed.</param>
/// <param name="Released">Whether its native state has been freed.</param>
internal readonly record struct SessionUse(int Runs, bool Disposed, bool Released)
{
    /// <summary>A run starts. Refused once the session is disposed.</summary>
    public static (SessionUse State, bool Entered) Enter(SessionUse state) =>
        state.Disposed ? (state, false) : (state with { Runs = state.Runs + 1 }, true);

    /// <summary>A run ends. Frees the session when it was disposed during the run and no other runs.</summary>
    public static (SessionUse State, bool Release) Exit(SessionUse state) =>
        Settle(state with { Runs = Math.Max(0, state.Runs - 1) });

    /// <summary>The session is disposed. Frees it now unless a run is in progress.</summary>
    public static (SessionUse State, bool Release) Dispose(SessionUse state) =>
        state.Disposed ? (state, false) : Settle(state with { Disposed = true });

    private static (SessionUse State, bool Release) Settle(SessionUse state) =>
        state.Disposed && state.Runs == 0 && !state.Released
            ? (state with { Released = true }, true)
            : (state, false);
}
