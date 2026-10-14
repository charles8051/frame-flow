// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Player.Core;

/// <summary>
/// The lifecycle of a pass's run and dispose (ADR-0085, decision 6; #696): which run it lets
/// start, and what its dispose does about a run before it frees the state the run uses and lets go
/// of the claims on its sinks. Pure: a state and an event in, a state and a verdict out.
/// </summary>
/// <remarks>
/// <para>
/// A pass runs once, and never once its dispose has begun. Its dispose cancels a run in flight and
/// waits for it to end before it frees the demux pump pipeline, the decoders and the demux session
/// the run reads from, and releases the claims last, so nothing the run sends reaches a sink after
/// the release. A run that has ended leaves nothing to wait for.
/// </para>
/// <para>
/// A run and a dispose that race each take the shell's lock, in one order or the other, and each
/// order is one sequence of these transitions: a dispose first refuses the run, and a run first is
/// the run the dispose cancels and waits for.
/// </para>
/// </remarks>
/// <param name="Running">Whether a run is in flight.</param>
/// <param name="Ran">Whether a run has started, in flight or ended.</param>
/// <param name="Disposed">Whether the pass's dispose has begun.</param>
internal readonly record struct PassLifecycle(bool Running, bool Ran, bool Disposed)
{
    /// <summary>A pass just built: holding its claims, with no run started.</summary>
    public static PassLifecycle Built => default;

    /// <summary>A run asks to start. Refused once the dispose has begun, and once a run has started.</summary>
    public (PassLifecycle Next, PassRunStart Start) StartRun() =>
        Disposed ? (this, PassRunStart.Disposed)
        : Ran ? (this, PassRunStart.AlreadyRan)
        : (this with { Running = true, Ran = true }, PassRunStart.Started);

    /// <summary>The run in flight ended, however it ended.</summary>
    public PassLifecycle EndRun() => this with { Running = false };

    /// <summary>The pass's dispose begins.</summary>
    public (PassLifecycle Next, PassTeardown Teardown) Dispose() =>
        Disposed ? (this, PassTeardown.Begun)
        : (this with { Disposed = true }, Running ? PassTeardown.AfterTheRun : PassTeardown.Now);
}

/// <summary>Whether a run a pass was asked for starts, and why not.</summary>
internal enum PassRunStart
{
    /// <summary>The run starts.</summary>
    Started,

    /// <summary>A run has started before. A pass runs once.</summary>
    AlreadyRan,

    /// <summary>The pass's dispose has begun.</summary>
    Disposed,
}

/// <summary>What a dispose does before it frees the pass's state and releases its claims.</summary>
internal enum PassTeardown
{
    /// <summary>No run is in flight, so it tears down at once.</summary>
    Now,

    /// <summary>Cancels the run in flight and waits for it to end, then tears down.</summary>
    AfterTheRun,

    /// <summary>Another dispose has begun the teardown. This one waits for it to finish.</summary>
    Begun,
}
