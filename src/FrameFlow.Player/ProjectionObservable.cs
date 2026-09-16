// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Player;

/// <summary>
/// Adapter that projects an <c>IObservable&lt;TSource&gt;</c> into
/// an <c>IObservable&lt;TTarget&gt;</c> via a selector. Replaces the
/// equivalent <c>System.Reactive.Linq.Observable.Select</c> call
/// without pulling in the package — keeps the dependency surface
/// small for FrameFlow consumers.
/// </summary>
internal sealed class ProjectionObservable<TSource, TTarget> : IObservable<TTarget>
{
    private readonly IObservable<TSource> _source;
    private readonly Func<TSource, TTarget> _selector;

    public ProjectionObservable(IObservable<TSource> source, Func<TSource, TTarget> selector)
    {
        _source = source;
        _selector = selector;
    }

    public IDisposable Subscribe(IObserver<TTarget> observer) =>
        _source.Subscribe(new ProjectingObserver(observer, _selector));

    private sealed class ProjectingObserver(
        IObserver<TTarget> downstream,
        Func<TSource, TTarget> selector
    ) : IObserver<TSource>
    {
        public void OnCompleted() => downstream.OnCompleted();

        public void OnError(Exception error) => downstream.OnError(error);

        public void OnNext(TSource value) => downstream.OnNext(selector(value));
    }
}
