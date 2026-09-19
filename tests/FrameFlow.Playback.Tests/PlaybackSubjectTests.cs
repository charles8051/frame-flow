namespace FrameFlow.Playback.Tests;

/// <summary>
/// <see cref="PlaybackSubject{T}"/>'s fan-out contract. Every observable on
/// <c>IPlaybackController</c> publishes through it, and several of them are raised in pairs — an
/// item-scoped event followed by the bare one, sharing a payload. That pairing is only safe if one
/// observer cannot take the other event down with it.
/// </summary>
/// <remarks>
/// These were written to close a review finding that claimed the subject "forwards observer
/// callbacks synchronously without isolating observer exceptions". It isolates them per observer.
/// The behaviour predates the paired events, so this file pins what they rely on rather than
/// adding it.
/// </remarks>
public sealed class PlaybackSubjectTests
{
    [Fact]
    public void AThrowingObserver_DoesNotStopTheOthers_AndDoesNotEscape()
    {
        using var subject = new PlaybackSubject<int>();
        var seen = new List<int>();

        using var bad = subject.Subscribe(new ThrowingObserver<int>());
        using var good = subject.Subscribe(new CollectingObserver<int>(seen.Add));

        // No try/catch here on purpose: an escaping exception fails the test, which is the claim
        // under examination.
        subject.OnNext(1);

        Assert.Equal([1], seen);
    }

    [Fact]
    public void AThrowingObserver_IsEvictedRatherThanRetried()
    {
        // The other half of the behaviour, and the part worth knowing about: a subscriber that
        // throws once is silently unsubscribed for the life of the subject. It is deliberate — the
        // alternative is calling a known-faulting observer on every notification — but a consumer
        // whose handler throws once stops receiving with no signal that it did.
        using var subject = new PlaybackSubject<int>();
        var calls = 0;

        using var bad = subject.Subscribe(
            new CollectingObserver<int>(_ =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            })
        );

        subject.OnNext(1);
        subject.OnNext(2);
        subject.OnNext(3);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void APairedFanOut_DeliversTheSecondEvenWhenTheFirstsObserverThrows()
    {
        // The shape the item-scoped events use: raise the item event, then the bare one, on one
        // thread with no isolation of its own. The controller relies on OnNext swallowing, so this
        // pins it at the shape rather than at the primitive.
        using var itemScoped = new PlaybackSubject<string>();
        using var bare = new PlaybackSubject<string>();
        var bareSeen = new List<string>();

        using var thrower = itemScoped.Subscribe(new ThrowingObserver<string>());
        using var watcher = bare.Subscribe(new CollectingObserver<string>(bareSeen.Add));

        itemScoped.OnNext("item");
        bare.OnNext("bare");

        Assert.Equal(["bare"], bareSeen);
    }

    private sealed class ThrowingObserver<T> : IObserver<T>
    {
        public void OnNext(T value) => throw new InvalidOperationException("observer boom");

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }

    private sealed class CollectingObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }
}
