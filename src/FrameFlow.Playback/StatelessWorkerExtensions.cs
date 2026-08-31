// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Stateless;

namespace FrameFlow.Playback;

/// <summary>
/// Extension methods for binding <see cref="IStateBoundWorker"/> lifecycle
/// to Stateless state machine states per ADR-0026.
/// </summary>
internal static class StatelessWorkerExtensions
{
    /// <summary>
    /// Binds a worker's lifecycle to a state: entering the state creates and starts
    /// the worker via a <see cref="WorkerBinding{TWorker}"/>; exiting the state
    /// stops and disposes it.
    /// </summary>
    /// <typeparam name="TState">The state machine's state type.</typeparam>
    /// <typeparam name="TTrigger">The state machine's trigger type.</typeparam>
    /// <typeparam name="TWorker">
    /// The concrete worker type implementing <see cref="IStateBoundWorker"/>.
    /// </typeparam>
    /// <param name="config">The state configuration to attach entry/exit actions to.</param>
    /// <param name="factory">
    /// Factory that creates a fresh worker instance on each state entry.
    /// </param>
    /// <param name="onError">
    /// Optional callback invoked when the worker faults with a non-cancellation exception.
    /// </param>
    /// <param name="shutdownTimeout">
    /// Maximum time to wait for cooperative shutdown before abandoning the task.
    /// </param>
    /// <param name="logger">Optional logger for structured lifecycle diagnostics.</param>
    /// <returns>The state configuration for chaining.</returns>
    public static StateMachine<TState, TTrigger>.StateConfiguration BindWorker<
        TState,
        TTrigger,
        TWorker
    >(
        this StateMachine<TState, TTrigger>.StateConfiguration config,
        Func<TWorker> factory,
        Func<TWorker, Exception, Task>? onError = null,
        TimeSpan? shutdownTimeout = null,
        ILogger? logger = null
    )
        where TWorker : IStateBoundWorker
    {
        var binding = new WorkerBinding<TWorker>(factory, onError, shutdownTimeout, logger);
        return config
            .OnEntryAsync(() => binding.StartAsync())
            .OnExitAsync(() => binding.StopAsync());
    }
}
