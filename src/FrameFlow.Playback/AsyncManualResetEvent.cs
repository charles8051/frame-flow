// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// An async-friendly manual reset event. When set (open), <see cref="WaitAsync"/>
/// returns immediately. When reset (closed), <see cref="WaitAsync"/> blocks until
/// <see cref="Set"/> is called. Used as the pause gate for long-lived worker tasks
/// per ADR-0022.
/// </summary>
internal sealed class AsyncManualResetEvent
{
    private volatile TaskCompletionSource<bool> _tcs;

    /// <summary>
    /// Creates a new event in the specified initial state.
    /// </summary>
    /// <param name="initiallySet">
    /// When <see langword="true"/>, the gate starts open (waiters return immediately).
    /// When <see langword="false"/>, the gate starts closed (waiters block).
    /// </param>
    public AsyncManualResetEvent(bool initiallySet = false)
    {
        _tcs = CreateTcs();
        if (initiallySet)
        {
            _tcs.TrySetResult(true);
        }
    }

    /// <summary>Whether the gate is currently set (open).</summary>
    public bool IsSet => _tcs.Task.IsCompleted;

    /// <summary>
    /// Opens the gate. All current and future waiters return immediately until
    /// <see cref="Reset"/> is called.
    /// </summary>
    public void Set() => _tcs.TrySetResult(true);

    /// <summary>
    /// Closes the gate. Subsequent <see cref="WaitAsync"/> calls will block until
    /// <see cref="Set"/> is called. Already-waiting callers remain blocked.
    /// </summary>
    public void Reset()
    {
        // Only replace with a new TCS if the current one is already completed.
        // This avoids replacing a TCS that waiters are still blocking on.
        var current = _tcs;
        if (current.Task.IsCompleted)
        {
            Interlocked.CompareExchange(ref _tcs, CreateTcs(), current);
        }
    }

    /// <summary>
    /// Waits for the gate to be set (open). Returns immediately if already set.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancellation token. Throws <see cref="OperationCanceledException"/> if
    /// cancelled while waiting.
    /// </param>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var task = _tcs.Task;
        if (task.IsCompleted)
        {
            return Task.CompletedTask;
        }

        // Register cancellation so the caller can be interrupted while waiting.
        return WaitWithCancellationAsync(task, cancellationToken);
    }

    private static async Task WaitWithCancellationAsync(
        Task gateTask,
        CancellationToken cancellationToken
    )
    {
        var cancelTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var reg = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            cancelTcs
        );

        var completed = await Task.WhenAny(gateTask, cancelTcs.Task).ConfigureAwait(false);
        if (completed == cancelTcs.Task)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static TaskCompletionSource<bool> CreateTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
