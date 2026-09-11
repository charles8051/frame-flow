// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia.Threading;

namespace FrameFlow.Avalonia;

/// <summary>
/// Reactive helpers for marshalling FrameFlow observables onto the Avalonia
/// UI thread.
/// </summary>
/// <remarks>
/// Delegate-based subscription lives in <c>FrameFlow.Playback</c>, as
/// <c>PlaybackObservableExtensions.Subscribe</c>. Declaring a second
/// <c>Subscribe(IObservable&lt;T&gt;, Action&lt;T&gt;)</c> here made the call
/// ambiguous (CS0121) in any file importing both namespaces, which every
/// Avalonia consumer does.
/// </remarks>
public static class AvaloniaObservableExtensions
{
    /// <summary>
    /// Returns an observable whose <c>OnNext</c>, <c>OnError</c>, and
    /// <c>OnCompleted</c> notifications are posted to the Avalonia UI thread
    /// via <see cref="Dispatcher.UIThread"/>. Subscribers can update UI state
    /// directly without writing per-call <c>Dispatcher.UIThread.Post</c>
    /// boilerplate.
    /// </summary>
    /// <typeparam name="T">Observable element type.</typeparam>
    /// <param name="source">The source observable (typically a player event stream).</param>
    /// <returns>An observable that delivers notifications on the UI thread.</returns>
    /// <remarks>
    /// Notifications are posted at <see cref="DispatcherPriority.Background"/>
    /// so they never preempt input handling or rendering.
    /// </remarks>
    public static IObservable<T> ObserveOnUiThread<T>(this IObservable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new UiThreadObservable<T>(source);
    }

    private sealed class UiThreadObservable<T>(IObservable<T> source) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            return source.Subscribe(new UiThreadObserver<T>(observer));
        }
    }

    private sealed class UiThreadObserver<T>(IObserver<T> inner) : IObserver<T>
    {
        public void OnCompleted() =>
            Dispatcher.UIThread.Post(inner.OnCompleted, DispatcherPriority.Background);

        public void OnError(Exception error) =>
            Dispatcher.UIThread.Post(() => inner.OnError(error), DispatcherPriority.Background);

        public void OnNext(T value) =>
            Dispatcher.UIThread.Post(() => inner.OnNext(value), DispatcherPriority.Background);
    }
}
