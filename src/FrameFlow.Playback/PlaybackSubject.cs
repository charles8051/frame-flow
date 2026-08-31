// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;

namespace FrameFlow.Playback;

/// <summary>
/// Minimal thread-safe subject that implements <see cref="IObservable{T}"/>
/// without pulling in System.Reactive. Supports concurrent subscribe/unsubscribe
/// and fan-out via <see cref="OnNext"/>. Per D006 — lightweight observable for
/// playback event streams.
/// </summary>
/// <typeparam name="T">The type of notification value.</typeparam>
internal sealed class PlaybackSubject<T> : IObservable<T>, IDisposable
{
    private readonly ConcurrentDictionary<Guid, IObserver<T>> _observers = new();
    private volatile bool _disposed;

    /// <summary>
    /// Subscribe an observer to this subject.
    /// Returns an <see cref="IDisposable"/> that removes the subscription when disposed.
    /// </summary>
    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = Guid.NewGuid();
        _observers.TryAdd(key, observer);
        return new Subscription(this, key);
    }

    /// <summary>
    /// Push a value to all current observers. Observers that throw are silently removed.
    /// </summary>
    public void OnNext(T value)
    {
        if (_disposed)
            return;

        foreach (var kvp in _observers)
        {
            try
            {
                kvp.Value.OnNext(value);
            }
            catch
            {
                // Remove faulting observers to prevent repeated failures.
                _observers.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Signal completion to all observers and clear subscriptions.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var kvp in _observers)
        {
            try
            {
                kvp.Value.OnCompleted();
            }
            catch
            { /* best-effort */
            }
        }

        _observers.Clear();
    }

    private sealed class Subscription(PlaybackSubject<T> subject, Guid key) : IDisposable
    {
        public void Dispose() => subject._observers.TryRemove(key, out _);
    }
}
