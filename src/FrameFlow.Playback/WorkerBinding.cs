// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.ExceptionServices;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Playback;

/// <summary>
/// Manages the lifecycle of an <see cref="IStateBoundWorker"/> bound to a
/// state machine state. Guarantees: no double-start, a stop that waits for the
/// worker to end, a concurrent stop that waits for the first, disposal ordering,
/// and error callback invocation.
/// </summary>
/// <typeparam name="TWorker">
/// The concrete worker type implementing <see cref="IStateBoundWorker"/>.
/// </typeparam>
/// <remarks>
/// <para>
/// Each start creates a worker and runs it on a fresh <see cref="WorkScope"/>, which holds its
/// loop until the loop ends. A start while a worker is running or stopping is refused. A stop
/// cancels the worker, calls <see cref="IStateBoundWorker.StopAsync"/>, waits for the loop to end
/// however long that takes, and only then disposes the worker (ADR-0086, decision 3). After
/// <c>warningInterval</c> it logs once which worker has not ended. A stop made while another is
/// under way waits for it and ends as it did. Once a stop completes, a fresh
/// <see cref="StartAsync"/> cycle may begin.
/// </para>
/// <para>
/// See ADR-0026 §2 for the binding's design, and ADR-0086 decision 3, which replaces its bounded
/// wait and the abandon after it, and its compare-and-swap that returned a second stop at once.
/// </para>
/// </remarks>
internal sealed partial class WorkerBinding<TWorker>
    where TWorker : IStateBoundWorker
{
    private readonly Func<TWorker> _factory;
    private readonly Func<TWorker, Exception, Task>? _onError;
    private readonly TimeSpan _warningInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    // The cycle running or stopping, if any. Read and written under _gate. A cycle stays here until
    // its stop has ended, so a start meanwhile is refused and a second stop joins the first.
    private readonly object _gate = new();
    private Cycle? _cycle;

    /// <summary>Whether a worker is running or stopping: from a start until its stop has ended.</summary>
    internal bool IsRunning
    {
        get
        {
            lock (_gate)
                return _cycle is not null;
        }
    }

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
    /// <param name="warningInterval">
    /// How long a stop waits for the worker before it logs once that the worker has not ended.
    /// The stop goes on waiting. Defaults to <see cref="SlowWorkWarning.DefaultInterval"/>.
    /// </param>
    /// <param name="timeProvider">Measures <paramref name="warningInterval"/>. Defaults to the system's.</param>
    /// <param name="logger">
    /// Optional logger for structured lifecycle diagnostics.
    /// </param>
    public WorkerBinding(
        Func<TWorker> factory,
        Func<TWorker, Exception, Task>? onError = null,
        TimeSpan? warningInterval = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null
    )
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _onError = onError;
        _warningInterval = warningInterval ?? SlowWorkWarning.DefaultInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Creates a worker via the factory and starts its loop on a fresh scope, which holds the loop
    /// until it ends.
    /// </summary>
    /// <returns>
    /// A task that completes once the worker's <see cref="IStateBoundWorker.StartAsync"/> has been
    /// called on the scope and has returned its loop, so what the worker sets up before its first
    /// wait, such as the ticker's timer, is in place when the caller goes on, as it was when the
    /// binding called it on the caller's thread. A completed task when the binding is already
    /// running or stopping (double-start guard).
    /// </returns>
    public Task StartAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_cycle is not null)
            {
                LogDoubleStartGuarded(_logger);
                return Task.CompletedTask;
            }

            var worker = _factory();
            var scope = new WorkScope();
            scope.TryStart(
                WorkerTypeName,
                ct =>
                {
                    var loop = RunWorkerAsync(worker, ct);
                    started.TrySetResult();
                    return loop;
                }
            );
            _cycle = new Cycle(worker, scope);
        }

        LogWorkerStarted(_logger);
        return started.Task;
    }

    /// <summary>
    /// Stops the worker: cancels its token, calls <see cref="IStateBoundWorker.StopAsync"/>, waits
    /// for its loop to end with no bound, disposes the worker, and lets a new cycle start.
    /// </summary>
    /// <remarks>
    /// A call made while a stop is under way waits for that stop and ends as it did. A call with
    /// nothing running returns at once. What the stop's steps throw, and what <c>onError</c> threw,
    /// is thrown once the worker is disposed and the binding is idle.
    /// </remarks>
    public Task StopAsync()
    {
        Cycle? cycle;
        lock (_gate)
            cycle = _cycle;

        return cycle is null ? Task.CompletedTask : cycle.Stop.RunAsync(() => StopCycleAsync(cycle)).AsTask();
    }

    /// <summary>The stop the first <see cref="StopAsync"/> of a cycle runs.</summary>
    private async ValueTask StopCycleAsync(Cycle cycle)
    {
        List<Exception> failures = [];
        try
        {
            await SlowWorkWarning
                .AwaitAsync(
                    EndLoopAsync(cycle, failures),
                    _warningInterval,
                    _timeProvider,
                    () => LogWorkerNotEnded(_logger, _warningInterval)
                )
                .ConfigureAwait(false);

            // Disposed only once its loop has ended.
            try
            {
                await cycle.Worker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cycle, cycle))
                    _cycle = null;
            }
        }

        LogWorkerStopped(_logger);
        switch (failures)
        {
            case []:
                break;
            case [var one]:
                ExceptionDispatchInfo.Throw(one);
                break;
            default:
                throw new AggregateException(failures);
        }
    }

    /// <summary>
    /// Cancels the worker's loop, asks the worker to stop, and waits for the loop to end. Adds to
    /// <paramref name="failures"/> what the worker's stop threw, and what <c>onError</c> threw.
    /// </summary>
    private static async Task EndLoopAsync(Cycle cycle, List<Exception> failures)
    {
        // The scope's stop cancels the loop at once; it is awaited once the worker has been asked.
        var ended = cycle.Scope.StopAsync(CancellationToken.None);

        try
        {
            await cycle.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        foreach (var fault in await ended.ConfigureAwait(false))
            failures.Add(fault.Error);
    }

    /// <summary>
    /// Runs the worker's loop. Its cancellation ends it quietly. Any other fault is logged and goes
    /// to the <c>onError</c> callback, and what that callback throws is the loop's end, which the
    /// stop throws.
    /// </summary>
    private async Task RunWorkerAsync(TWorker worker, CancellationToken ct)
    {
        try
        {
            await worker.StartAsync(ct).ConfigureAwait(false);
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
                await _onError(worker, ex).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A started worker, the scope that holds its loop, and its stop, which runs once and which
    /// every other stop of the cycle waits for.
    /// </summary>
    private sealed record Cycle(TWorker Worker, WorkScope Scope)
    {
        public DisposeOnce Stop { get; } = new();
    }
}
