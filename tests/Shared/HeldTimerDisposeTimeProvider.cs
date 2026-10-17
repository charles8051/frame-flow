// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Tests.Shared;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that can hold the thread disposing one of its timers until the
/// test lets it go.
/// </summary>
/// <remarks>
/// A <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> that is cancelled disposes
/// its timer from inside the cancellation callback, on the thread that cancels, after it has queued
/// the delay's continuation to the pool. Holding that dispose keeps the canceller inside the chain
/// of link callbacks while the woken wait runs on, which is where a production timer's dispose
/// leaves it for as long as the dispose takes (#785).
/// </remarks>
internal sealed class HeldTimerDisposeTimeProvider : FakeTimeProvider
{
    private readonly ManualResetEventSlim _release = new();
    private TaskCompletionSource _armed = NewSignal();
    private int _holdNext;

    /// <summary>Completes when the first timer created after this was read is armed.</summary>
    public Task NextTimerArmed => Volatile.Read(ref _armed).Task;

    /// <summary>Completes once a held dispose has begun, and its thread is waiting for <see cref="Release"/>.</summary>
    public TaskCompletionSource DisposeHeld { get; } = NewSignal();

    /// <summary>Holds the next dispose of a timer from this provider until <see cref="Release"/>.</summary>
    public void HoldNextDispose() => Volatile.Write(ref _holdNext, 1);

    /// <summary>Lets a held dispose go on, and any later one.</summary>
    public void Release() => _release.Set();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new HeldTimer(this, base.CreateTimer(callback, state, dueTime, period));
        Interlocked.Exchange(ref _armed, NewSignal()).TrySetResult();
        return timer;
    }

    private void OnDispose()
    {
        if (Interlocked.Exchange(ref _holdNext, 0) == 0)
            return;
        DisposeHeld.TrySetResult();
        _release.Wait();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class HeldTimer(HeldTimerDisposeTimeProvider owner, ITimer inner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            owner.OnDispose();
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            owner.OnDispose();
            return inner.DisposeAsync();
        }
    }
}
