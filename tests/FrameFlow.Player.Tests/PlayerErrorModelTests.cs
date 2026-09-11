using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Covers the ADR-0069 error model on <see cref="IMediaPlayer"/>: transport
/// commands answer in <see cref="Result"/>, and the structured error reaches
/// the caller intact.
/// </summary>
/// <remarks>
/// The wrapper used to translate every failed <see cref="Result"/> into an
/// <see cref="InvalidOperationException"/> whose message read
/// <c>"{op} failed: {Category} — {Message}"</c>. That discarded
/// <see cref="ErrorCategory"/> as a value: a caller who wanted to tell a
/// non-seekable source from a disposed player had to parse the string. These
/// tests pin that the category, the message and the inner exception all survive
/// the layer.
/// </remarks>
public sealed class PlayerErrorModelTests
{
    private static readonly PlaybackError Refusal = new(
        ErrorCategory.InvalidOperation,
        "Cannot seek a non-seekable source.",
        new NotSupportedException("underlying")
    );

    [Fact]
    public async Task Transport_ReturnsOk_WhenTheControllerSucceeds()
    {
        await using var player = NewPlayer(new StubController());

        Assert.True((await player.PlayAsync()).IsSuccess);
        Assert.True((await player.PauseAsync()).IsSuccess);
        Assert.True((await player.SeekAsync(TimeSpan.FromSeconds(1))).IsSuccess);
        Assert.True((await player.SetRepeatModeAsync(RepeatMode.One)).IsSuccess);
    }

    [Fact]
    public async Task SeekAsync_ReturnsTheRefusal_RatherThanThrowing()
    {
        await using var player = NewPlayer(new StubController { Failure = Refusal });

        var result = await player.SeekAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.IsSuccess);
        Assert.Same(Refusal, result.Error);
    }

    [Fact]
    public async Task Transport_KeepsTheCategoryAsAValue_NotAFormattedString()
    {
        await using var player = NewPlayer(new StubController { Failure = Refusal });

        var result = await player.PlayAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, result.Error.Category);
        Assert.Equal("Cannot seek a non-seekable source.", result.Error.Message);
        Assert.IsType<NotSupportedException>(result.Error.Inner);
    }

    [Theory]
    [InlineData(nameof(IMediaPlayer.PlayAsync))]
    [InlineData(nameof(IMediaPlayer.PauseAsync))]
    [InlineData(nameof(IMediaPlayer.SeekAsync))]
    [InlineData(nameof(IMediaPlayer.SetRepeatModeAsync))]
    public async Task EveryTransportCommand_ReportsRefusalTheSameWay(string command)
    {
        await using var player = NewPlayer(new StubController { Failure = Refusal });

        var result = command switch
        {
            nameof(IMediaPlayer.PlayAsync) => await player.PlayAsync(),
            nameof(IMediaPlayer.PauseAsync) => await player.PauseAsync(),
            nameof(IMediaPlayer.SeekAsync) => await player.SeekAsync(TimeSpan.Zero),
            _ => await player.SetRepeatModeAsync(RepeatMode.All),
        };

        Assert.False(result.IsSuccess);
        Assert.Same(Refusal, result.Error);
    }

    [Fact]
    public async Task ErrorOccurred_ForwardsTheControllersStream()
    {
        var controller = new StubController();
        await using var player = NewPlayer(controller);

        PlaybackError? seen = null;
        using var subscription = player.ErrorOccurred.Subscribe(new Capture(e => seen = e));

        // A mid-playback failure, not the answer to any command: the channel
        // IMediaPlayer had no way to expose before ADR-0069.
        controller.RaiseError(Refusal);

        Assert.Same(Refusal, seen);
    }

    private static MediaPlayerCore NewPlayer(IPlaybackController controller) =>
        new(controller, audioSink: null, ownedProvider: null, NullLogger.Instance);

    // ── Doubles ──────────────────────────────────────────────────────────────

    private sealed class Capture(Action<PlaybackError> onNext) : IObserver<PlaybackError>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(PlaybackError value) => onNext(value);
    }

    /// <summary>
    /// A controller that answers every transport command with
    /// <see cref="Failure"/> when one is set, and can push an out-of-band error
    /// onto <see cref="ErrorOccurred"/>.
    /// </summary>
    private sealed class StubController : IPlaybackController
    {
        private readonly SubjectObservable<PlaybackError> _errors = new();

        public PlaybackError? Failure { get; init; }

        public void RaiseError(PlaybackError error) => _errors.Push(error);

        private Task<Result> Answer() =>
            Task.FromResult(Failure is null ? Result.Ok() : Result.Fail(Failure));

        public Task<Result> LoadAsync(IMediaSource source, CancellationToken ct = default) =>
            Answer();

        public Task<Result> UnloadAsync(CancellationToken ct = default) => Answer();

        public Task<Result> PlayAsync(CancellationToken ct = default) => Answer();

        public Task<Result> PauseAsync(CancellationToken ct = default) => Answer();

        public Task<Result> SeekAsync(TimeSpan position, CancellationToken ct = default) =>
            Answer();

        public Task<Result> SetRepeatModeAsync(RepeatMode mode, CancellationToken ct = default) =>
            Answer();

        public PlaybackState State => PlaybackState.Idle;
        public SeekState SeekingState => SeekState.NotSeeking;
        public RepeatMode RepeatMode => RepeatMode.Off;
        public bool IsActivelyPresenting => false;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public MediaInfo? MediaInfo => null;

        public IObservable<StateTransition<PlaybackState>> PlaybackStateChanged { get; } =
            new NeverObservable<StateTransition<PlaybackState>>();
        public IObservable<StateTransition<SeekState>> SeekStateChanged { get; } =
            new NeverObservable<StateTransition<SeekState>>();
        public IObservable<StateTransition<RepeatMode>> RepeatModeChanged { get; } =
            new NeverObservable<StateTransition<RepeatMode>>();
        public IObservable<LoopRestarted> LoopRestarted { get; } =
            new NeverObservable<LoopRestarted>();
        public IObservable<LoopStalled> LoopStalled { get; } = new NeverObservable<LoopStalled>();
        public IObservable<TimeSpan> PositionTick { get; } = new NeverObservable<TimeSpan>();

        public IObservable<PlaybackError> ErrorOccurred => _errors;

        public PlaybackDiagnosticsSnapshot GetDiagnostics() => PlaybackDiagnosticsSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A minimal hot observable: enough to prove the forward.</summary>
    private sealed class SubjectObservable<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];

        public IDisposable Subscribe(IObserver<T> observer)
        {
            _observers.Add(observer);
            return new Unsubscribe(() => _observers.Remove(observer));
        }

        public void Push(T value)
        {
            foreach (var o in _observers.ToArray())
                o.OnNext(value);
        }

        private sealed class Unsubscribe(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    /// <summary>An observable that never produces and never completes.</summary>
    private sealed class NeverObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose() { }
        }
    }
}
