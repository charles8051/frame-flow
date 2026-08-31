// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace FrameFlow.Playback;

/// <summary>
/// Manages the lifecycle of an <see cref="IStateBoundWorker"/> bound to a
/// state machine state. Guarantees: no double-start, no double-stop,
/// shutdown timeout, disposal ordering, and error callback invocation.
/// </summary>
/// <typeparam name="TWorker">
/// The concrete worker type implementing <see cref="IStateBoundWorker"/>.
/// </typeparam>
/// <remarks>
/// <para>
/// Uses a three-state Interlocked CAS machine: 0 = idle, 1 = running,
/// 2 = stopping. This is lock-free and re-entrant safe — after
/// <see cref="StopAsync"/> completes, state resets to 0 allowing a
/// fresh <see cref="StartAsync"/> cycle.
/// </para>
/// <para>
/// See ADR-0026 §2 for the canonical design and rationale.
/// </para>
/// </remarks>
internal sealed partial class WorkerBinding<TWorker>
    where TWorker : IStateBoundWorker
{
    private const int StateIdle = 0;
    private const int StateRunning = 1;
    private const int StateStopping = 2;

    private readonly Func<TWorker> _factory;
    private readonly Func<TWorker, Exception, Task>? _onError;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private TWorker? _worker;
    private int _state;

    /// <summary>
    /// Initializes a new <see cref="WorkerBinding{TWorker}"/>.
    /// </summary>
    /// <param name="factory">
    /// Factory that creates a fresh worker instance on each start cycle.
    /// </param>
    /// <param name="onError">
    /// Optional callback invoked when the worker faults with a non-cancellation
    /// exception. Receives the worker instance and the exception.
    /// </param>
    /// <param name="shutdownTimeout">
    /// Maximum time to wait for cooperative shutdown before abandoning the task.
    /// Defaults to 5 seconds.
    /// </param>
    /// <param name="logger">
    /// Optional logger for structured lifecycle diagnostics.
    /// </param>
    public WorkerBinding(
        Func<TWorker> factory,
        Func<TWorker, Exception, Task>? onError = null,
        TimeSpan? shutdownTimeout = null,
        ILogger? logger = null
    )
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _onError = onError;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        _logger = logger;
    }

    /// <summary>
    /// Creates a worker via the factory, starts it, and launches the
    /// background runner. Returns synchronously — the worker's long-running
    /// loop executes on a fire-and-forget task.
    /// </summary>
    /// <returns><see cref="Task.CompletedTask"/> on success, or immediately
    /// if the binding is already running (double-start guard).</returns>
    public Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _state, StateRunning, StateIdle) != StateIdle)
        {
            LogDoubleStartGuarded(_logger);
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _worker = _factory();

        // Fire-and-forget — NOT awaited. The worker's loop runs independently.
        _runningTask = RunWorkerAsync(_worker, _cts.Token);

        LogWorkerStarted(_logger);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cooperatively stops the worker: cancels the token, calls
    /// <see cref="IStateBoundWorker.StopAsync"/>, awaits the running task
    /// with timeout, disposes the worker and CTS, and resets state to idle.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _state, StateStopping, StateRunning) != StateRunning)
            return; // not running — nothing to stop

        var cts = _cts;
        var task = _runningTask;
        var worker = _worker;

        if (cts is null || worker is null)
        {
            Interlocked.Exchange(ref _state, StateIdle);
            return;
        }

        // Signal cancellation to the worker's StartAsync token.
        await cts.CancelAsync();

        // Cooperative stop with shutdown timeout.
        using var shutdownCts = new CancellationTokenSource(_shutdownTimeout);
        try
        {
            await worker.StopAsync(shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timed out — worker is uncooperative.
            LogShutdownTimeout(_logger, _shutdownTimeout);
        }

        // Await the running task (should be completed or near-complete).
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(_shutdownTimeout);
            }
            catch (TimeoutException)
            {
                LogShutdownTimeout(_logger, _shutdownTimeout);
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation tear-down.
            }
        }

        // Dispose worker first, then CTS — per ADR-0026 disposal ordering.
        await worker.DisposeAsync();
        cts.Dispose();

        _cts = null;
        _runningTask = null;
        _worker = default;

        Interlocked.Exchange(ref _state, StateIdle);
        LogWorkerStopped(_logger);
    }

    /// <summary>
    /// Wraps <see cref="IStateBoundWorker.StartAsync"/> in error handling.
    /// Cancellation exceptions are swallowed; all others invoke the
    /// <c>onError</c> callback.
    /// </summary>
    private async Task RunWorkerAsync(TWorker worker, CancellationToken ct)
    {
        try
        {
            await worker.StartAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — cancellation token was signalled.
        }
        catch (Exception ex)
        {
            LogWorkerError(_logger, ex);

            if (_onError is not null)
            {
                await _onError(worker, ex);
            }
        }
    }
}
