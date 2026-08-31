// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Minimal subscription helpers for the <see cref="IObservable{T}"/> streams
/// exposed by <see cref="IPlaybackController"/>. These overloads let consumers
/// subscribe with simple delegates instead of implementing <see cref="IObserver{T}"/>
/// or pulling in System.Reactive for the common case.
/// </summary>
/// <remarks>
/// This is intentionally a minimal API — it covers delegate-based next/error
/// handling. Consumers who need filtering, throttling, or composition should
/// reference System.Reactive directly and use its native extensions on the
/// same <see cref="IObservable{T}"/> sources.
/// </remarks>
public static class PlaybackObservableExtensions
{
    /// <summary>
    /// Subscribes a delegate to the observable. The returned disposable removes
    /// the subscription when disposed.
    /// </summary>
    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onNext);
        return source.Subscribe(new DelegateObserver<T>(onNext, onError: null));
    }

    /// <summary>
    /// Subscribes separate next and error delegates to the observable.
    /// The returned disposable removes the subscription when disposed.
    /// </summary>
    public static IDisposable Subscribe<T>(
        this IObservable<T> source,
        Action<T> onNext,
        Action<Exception> onError
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onNext);
        ArgumentNullException.ThrowIfNull(onError);
        return source.Subscribe(new DelegateObserver<T>(onNext, onError));
    }

    private sealed class DelegateObserver<T>(Action<T> onNext, Action<Exception>? onError)
        : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnError(Exception error) => onError?.Invoke(error);

        public void OnCompleted() { }
    }
}
