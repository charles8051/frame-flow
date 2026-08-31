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
/// Implementations should return promptly from <see cref="StartAsync"/>,
/// spawning any long-running loops internally. The cancellation token
/// provided to <see cref="StartAsync"/> is cancelled when the owning
/// state is exited.
/// </para>
/// <para>
/// See ADR-0026 §1 for the design rationale.
/// </para>
/// </remarks>
internal interface IStateBoundWorker : IAsyncDisposable
{
    /// <summary>
    /// Begin execution. The implementation should start its internal loop
    /// and return promptly. Long-running work should be spawned internally.
    /// The token is cancelled when the owning state is exited.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cooperatively stop execution. Called after the cancellation token
    /// has been signalled. Implementations should drain any pending work
    /// and return within a reasonable time. If this method does not return
    /// within the shutdown timeout, the binding will abandon the task and
    /// proceed with disposal.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}
