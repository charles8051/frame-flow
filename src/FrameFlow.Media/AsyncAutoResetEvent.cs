// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// An async auto-reset event with a single latched permit and a bounded wait.
/// <see cref="Set"/> releases one pending <see cref="WaitAsync"/>, or latches a permit so
/// the next wait returns immediately. Each successful wait consumes the permit and re-arms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written for buffer-return backpressure.</b> <c>OpenAlAudioSink</c> waits here for
/// OpenAL to recycle a processed buffer, rather than burning a pooled thread on a
/// <c>Thread.Sleep</c> spin (perf survey A3). The SDL audio sink in ADR-0018 §2 needs the
/// same wait once it takes backpressure from a real device instead of pacing on
/// <c>Task.Delay</c>. It sat in <c>FrameFlow.Audio.OpenAL</c> as an assembly-local sibling
/// of <see cref="AsyncManualResetEvent"/> precisely because that one was out of reach; both
/// live here now, so the next backend finds them rather than writing a third.
/// </para>
/// <para>
/// <b>Auto-reset, not manual.</b> <see cref="AsyncManualResetEvent"/> is a gate: open it and
/// every waiter, present and future, passes until it is closed again. This is a handoff: one
/// signal releases one waiter. They are not substitutable, which is why both exist.
/// </para>
/// <para>
/// <b>Single waiter.</b> One waiter is parked at a time, which is what the backpressure loop
/// needs. A second concurrent <see cref="WaitAsync"/> overwrites the first waiter's
/// registration, and the displaced wait then runs to its timeout instead of being signalled.
/// </para>
/// <para>
/// <b>Missed-wakeup safety.</b> The caller must not rely on the signal alone for correctness.
/// The audio sink re-checks its real <c>_freeBuffers</c> queue under the sink lock on every
/// wake, and the wait is bounded by a timeout. A <see cref="Set"/> that races just ahead of a
/// <see cref="WaitAsync"/> is preserved by the latched permit, and a spurious or timed-out
/// wake simply re-polls — so a buffer can never be lost and the wait can never deadlock if
/// the device stops draining.
/// </para>
/// </remarks>
internal sealed class AsyncAutoResetEvent
{
    private readonly Lock _gate = new();

    // The TCS the current waiter (if any) is parked on. Null when no one is
    // waiting. RunContinuationsAsynchronously so completing under _gate never
    // runs the awaiter's continuation inline while the lock is held.
    private TaskCompletionSource? _waiter;

    // A latched permit for a Set that arrived with no waiter parked. The next
    // WaitAsync consumes it and returns synchronously, so a buffer return that
    // races ahead of the wait is never lost.
    private bool _signaled;

    /// <summary>
    /// Signals that a buffer became available. Releases a pending waiter if one
    /// is parked; otherwise latches a single permit for the next waiter. Idempotent
    /// while a permit is already latched (extra returns collapse — the waiter
    /// re-polls the real queue regardless).
    /// </summary>
    public void Set()
    {
        TaskCompletionSource? toRelease = null;
        lock (_gate)
        {
            if (_waiter is { } w)
            {
                _waiter = null;
                toRelease = w;
            }
            else
            {
                _signaled = true;
            }
        }

        // Complete outside the lock; RunContinuationsAsynchronously already
        // guarantees the continuation does not run inline, but releasing the
        // gate first keeps the signal path allocation/contention minimal.
        toRelease?.TrySetResult();
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the next <see cref="Set"/>,
    /// consuming a latched permit immediately if one is present. Returns when
    /// signaled, when the timeout elapses, or throws if
    /// <paramref name="cancellationToken"/> fires. The timeout makes the wait
    /// self-healing: even a missed signal only delays the caller's re-poll by
    /// the timeout, never strands it.
    /// </summary>
    /// <returns><see langword="true"/> if released by a signal; <see langword="false"/> if the timeout elapsed.</returns>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task waitTask;
        lock (_gate)
        {
            if (_signaled)
            {
                _signaled = false;
                return true;
            }

            // Only one waiter at a time is expected (the single audio worker in
            // the backpressure loop). Re-park onto a fresh TCS each wait.
            _waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waitTask = _waiter.Task;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token
        );

        var delayTask = Task.Delay(Timeout.Infinite, linked.Token);
        var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);

        if (completed == waitTask)
        {
            // Released by Set(). Observe completion (it never faults) and report.
            await waitTask.ConfigureAwait(false);
            return true;
        }

        // Timed out or cancelled. Detach our waiter so a later Set() doesn't
        // complete a stale TCS instead of latching a permit / releasing a real
        // future waiter.
        lock (_gate)
        {
            if (ReferenceEquals(_waiter?.Task, waitTask))
                _waiter = null;
        }

        // Distinguish caller cancellation (throw) from timeout (return false).
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }
}
