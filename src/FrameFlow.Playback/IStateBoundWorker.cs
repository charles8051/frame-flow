// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// A background worker whose lifetime is bound to a state machine state.
/// Created on state entry, started immediately, stopped on state exit,
/// then disposed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartAsync"/> returns the worker's loop: its task ends when
/// the loop does. The binding holds it until then, so a worker runs no
/// loop of its own that the binding cannot wait for (ADR-0086, decision 1).
/// The cancellation token provided to <see cref="StartAsync"/> is
/// cancelled when the owning state is exited.
/// </para>
/// <para>
/// See ADR-0026 §1 for the design rationale.
/// </para>
/// </remarks>
internal interface IStateBoundWorker : IAsyncDisposable
{
    /// <summary>
    /// Runs the worker's loop until the token is cancelled. The returned task
    /// is the loop, and ends when it does.
    /// The token is cancelled when the owning state is exited.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cooperatively stop execution. Called after the cancellation token
    /// has been signalled. Implementations should drain any pending work
    /// and return promptly. The binding passes a token that is never
    /// cancelled: it waits for this method and for the loop
    /// <see cref="StartAsync"/> returned, however long they take, and
    /// disposes the worker only once both have ended (ADR-0086, decision 3).
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}
