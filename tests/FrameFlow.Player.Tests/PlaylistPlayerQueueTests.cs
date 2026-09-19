using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player.Tests;

/// <summary>
/// The queue verbs on <see cref="IMediaPlaylistPlayer"/> (#171), over a stub controller. The
/// ordering rules are unit-tested on the coordinator in <c>PlaylistCoordinatorTests</c>; these
/// pin what the player adds: the items it hands back, and the refusals.
/// </summary>
public sealed class PlaylistPlayerQueueTests
{
    [Fact]
    public async Task AddedAndEnqueuedItems_AppearInTheSnapshot()
    {
        await using var player = NewPlayer(PlaybackState.Paused, out _);

        var added = await player.AddAsync(Source("b"));
        var queued = await player.EnqueueAsync(Source("x"));
        var next = await player.SetNextAsync(Source("y"));

        var snapshot = player.GetPlaylist();
        Assert.Equal(added, snapshot.Playlist[^1]);
        Assert.Equal([queued], snapshot.Queued);
        Assert.Equal([next!], snapshot.Next);
        Assert.Null(await player.SetNextAsync(null));
    }

    [Fact]
    public async Task JumpAndRemove_RefuseAnItemNotInThePlayer()
    {
        await using var player = NewPlayer(PlaybackState.Paused, out _);
        var foreign = new PlaylistCoordinator([Source("z")], RepeatMode.Off).Snapshot().Playlist[0];

        var jump = await player.JumpToAsync(foreign);
        var remove = await player.RemoveAsync(foreign);

        Assert.False(jump.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, jump.Error.Category);
        Assert.False(remove.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, remove.Error.Category);
    }

    [Fact]
    public async Task JumpAndReplace_AreRefused_WhileThePlayerIsInError()
    {
        // Row 25.
        await using var player = NewPlayer(PlaybackState.Error, out var coordinator);
        var target = await player.AddAsync(Source("b"));

        var jump = await player.JumpToAsync(target);
        var replace = await player.ReplaceAsync([Source("d")]);

        Assert.False(jump.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, jump.Error.Category);
        Assert.False(replace.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, replace.Error.Category);
        Assert.False(coordinator.HasPendingJump);
    }

    [Fact]
    public async Task JumpAndReplace_AreRefused_AfterDisposal()
    {
        var player = NewPlayer(PlaybackState.Paused, out var coordinator);
        var target = await player.AddAsync(Source("b"));
        await player.DisposeAsync();

        Assert.False((await player.JumpToAsync(target)).IsSuccess);
        Assert.False((await player.ReplaceAsync([Source("d")])).IsSuccess);
        Assert.False(coordinator.HasPendingJump);
    }

    [Fact]
    public async Task Replace_WithNoSources_Throws()
    {
        await using var player = NewPlayer(PlaybackState.Paused, out _);
        await Assert.ThrowsAsync<ArgumentException>(() => player.ReplaceAsync([]));
    }

    [Fact]
    public async Task Replace_ReturnsTheNewPlaylist()
    {
        await using var player = NewPlayer(PlaybackState.Paused, out _);

        var replaced = await player.ReplaceAsync([Source("d"), Source("e")]);

        Assert.True(replaced.IsSuccess);
        var snapshot = player.GetPlaylist();
        Assert.Equal(replaced.Value, snapshot.Playlist);
        Assert.Same(replaced.Value![0], snapshot.PendingJump);
    }

    private static PlaylistMediaPlayerCore NewPlayer(
        PlaybackState state,
        out PlaylistCoordinator coordinator
    )
    {
        coordinator = new PlaylistCoordinator([Source("a")], RepeatMode.Off);
        return new PlaylistMediaPlayerCore(
            new StubController { State = state },
            coordinator,
            audioSink: null,
            NullLogger.Instance
        );
    }

    private static IMediaSource Source(string name) => new MediaSource { DisplayName = name };

    internal sealed class StubController : IPlaybackController
    {
        public PlaybackState State { get; init; } = PlaybackState.Idle;

        public Task<Result> LoadAsync(IMediaSource source, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> UnloadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> PlayAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> PauseAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> SeekAsync(TimeSpan position, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> SetRepeatModeAsync(RepeatMode mode, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

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
        public IObservable<PlaylistItemFailed> ItemFailed { get; } =
            new NeverObservable<PlaylistItemFailed>();
        public IObservable<PlaylistItemLooped> ItemLooped { get; } =
            new NeverObservable<PlaylistItemLooped>();
        public IObservable<PlaylistItemStalled> ItemStalled { get; } =
            new NeverObservable<PlaylistItemStalled>();
        public IObservable<LoopStalled> LoopStalled { get; } = new NeverObservable<LoopStalled>();
        public IObservable<TimeSpan> PositionTick { get; } = new NeverObservable<TimeSpan>();
        public IObservable<PlaybackError> ErrorOccurred { get; } =
            new NeverObservable<PlaybackError>();

        public PlaybackDiagnosticsSnapshot GetDiagnostics() => PlaybackDiagnosticsSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
