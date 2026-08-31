// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// The canonical writable <see cref="IClockSource"/>. Producers call
/// <see cref="Publish"/> to advance the timeline; consumers observe via the
/// <see cref="IClockSource"/> read surface (<see cref="IClockSource.Latest"/>,
/// <see cref="IClockSource.WaitUntilAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The subject is "live" from construction. There is no
/// activate/deactivate step. Disposing the subject completes all pending
/// waits with <see cref="OperationCanceledException"/>, modelling the
/// "producer has gone away" case as cancellation rather than as a sticky
/// terminal value.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Publish"/>, <see cref="IClockSource.Latest"/>,
/// and <see cref="IClockSource.WaitUntilAsync"/> are all safe to call from
/// any thread; an internal lock serialises the few state mutations
/// (latest value, waiter list).
/// </para>
/// <para>
/// <b>Waiter wakeup.</b> When <see cref="Publish"/> moves the latest value
/// forward past one or more pending waits' targets, those waits are
/// completed inside <see cref="Publish"/> — but on the publisher's thread,
/// continuations are configured to run asynchronously
/// (<see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>) so a
/// long continuation on the consumer side cannot stall the publisher.
/// </para>
/// </remarks>
public sealed class ClockSubject : IClockSource, IDisposable
{
    // Use a private object as the monitor target rather than the
    // .NET 9 System.Threading.Lock type so this assembly continues to
    // build for net8.0.
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = new();
    private TimeSpan _latest;
    private bool _disposed;

    /// <summary>
    /// Initializes a new clock subject with an initial value of
    /// <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public ClockSubject() { }

    /// <summary>
    /// Initializes a new clock subject with the supplied initial value.
    /// </summary>
    public ClockSubject(TimeSpan initial)
    {
        _latest = initial;
    }

    /// <inheritdoc/>
    public TimeSpan Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    /// <summary>
    /// Advances (or sets) the latest published value. Waiters whose target
    /// is at or before <paramref name="value"/> are completed and removed
    /// from the wait list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calling <see cref="Publish"/> on a disposed subject is a no-op —
    /// the producer is best-effort, not authoritative about when consumers
    /// have unsubscribed.
    /// </para>
    /// <para>
    /// The value may move backwards; callers that want strictly-monotonic
    /// timelines should enforce that on the producer side. Backwards
    /// publication does not cancel or rewind any in-flight waits — waits
    /// whose target was previously satisfied have already returned; waits
    /// pending against future targets continue to wait.
    /// </para>
    /// </remarks>
    public void Publish(TimeSpan value)
    {
        List<Waiter>? toComplete = null;

        lock (_gate)
        {
            if (_disposed)
                return;

            _latest = value;

            // Walk the waiter list, collecting any whose target is now
            // satisfied. Walking backwards lets us RemoveAt(i) cheaply
            // without shifting the unvisited prefix.
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Target <= value)
                {
                    (toComplete ??= new List<Waiter>()).Add(_waiters[i]);
                    _waiters.RemoveAt(i);
                }
            }
        }

        if (toComplete is null)
            return;

        // Complete waiters outside the lock. The TCS was configured with
        // RunContinuationsAsynchronously so continuations run on the
        // thread pool, not inline on this thread.
        foreach (var waiter in toComplete)
        {
            waiter.Registration.Dispose();
            waiter.Tcs.TrySetResult();
        }
    }

    /// <inheritdoc/>
    public ValueTask WaitUntilAsync(
        TimeSpan target,
        CancellationToken cancellationToken = default
    )
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        // Fast path: already past the target, no allocation, no list mutation.
        TimeSpan currentLatest;
        lock (_gate)
        {
            if (_disposed)
                return ValueTask.FromException(
                    new ObjectDisposedException(nameof(ClockSubject))
                );

            currentLatest = _latest;
            if (currentLatest >= target)
                return ValueTask.CompletedTask;
        }

        // Slow path: register a waiter under the lock so we don't miss a
        // concurrent Publish that crosses the target between our fast-path
        // read and the registration.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;

        Waiter waiter;
        bool fireImmediately = false;
        lock (_gate)
        {
            if (_disposed)
                return ValueTask.FromException(
                    new ObjectDisposedException(nameof(ClockSubject))
                );

            // Re-check inside the lock — a Publish may have raced our
            // fast-path read and we'd miss the wakeup otherwise.
            if (_latest >= target)
            {
                fireImmediately = true;
            }
            else
            {
                waiter = new Waiter(target, tcs, default);
                _waiters.Add(waiter);
            }
        }

        if (fireImmediately)
            return ValueTask.CompletedTask;

        // Wire cancellation outside the lock to avoid running the
        // CancellationToken's callback list (which may be substantial)
        // under our gate.
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(
                static state =>
                {
                    var (subject, tcs, target) =
                        ((ClockSubject, TaskCompletionSource, TimeSpan))state!;
                    subject.RemoveWaiter(tcs, target);
                    tcs.TrySetCanceled();
                },
                (this, tcs, target)
            );

            // Store the registration so Publish can dispose it when the
            // waiter fires through the success path. We need to update
            // the waiter record in-place; the lookup is by TCS identity.
            UpdateWaiterRegistration(tcs, target, registration);
        }

        return new ValueTask(tcs.Task);
    }

    private void RemoveWaiter(TaskCompletionSource tcs, TimeSpan target)
    {
        lock (_gate)
        {
            for (int i = 0; i < _waiters.Count; i++)
            {
                if (_waiters[i].Target == target && ReferenceEquals(_waiters[i].Tcs, tcs))
                {
                    _waiters.RemoveAt(i);
                    return;
                }
            }
        }
    }

    private void UpdateWaiterRegistration(
        TaskCompletionSource tcs,
        TimeSpan target,
        CancellationTokenRegistration registration
    )
    {
        lock (_gate)
        {
            for (int i = 0; i < _waiters.Count; i++)
            {
                if (_waiters[i].Target == target && ReferenceEquals(_waiters[i].Tcs, tcs))
                {
                    _waiters[i] = new Waiter(target, tcs, registration);
                    return;
                }
            }
            // Waiter was already removed (Publish satisfied it between
            // Add and registration wiring). Dispose the registration
            // since we won't get the chance later.
            registration.Dispose();
        }
    }

    /// <summary>
    /// Disposes the subject. All pending waits transition to the cancelled
    /// state; subsequent <see cref="Publish"/> calls are no-ops; subsequent
    /// <see cref="WaitUntilAsync"/> calls throw
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        List<Waiter>? toCancel;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            toCancel = _waiters.Count > 0 ? new List<Waiter>(_waiters) : null;
            _waiters.Clear();
        }

        if (toCancel is null)
            return;

        foreach (var waiter in toCancel)
        {
            waiter.Registration.Dispose();
            waiter.Tcs.TrySetCanceled();
        }
    }

    private readonly struct Waiter
    {
        public Waiter(
            TimeSpan target,
            TaskCompletionSource tcs,
            CancellationTokenRegistration registration
        )
        {
            Target = target;
            Tcs = tcs;
            Registration = registration;
        }

        public TimeSpan Target { get; }
        public TaskCompletionSource Tcs { get; }
        public CancellationTokenRegistration Registration { get; }
    }
}
