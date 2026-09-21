using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player.Tests;

/// <summary>
/// The queue verbs on <see cref="IMediaPlayer"/> (#171), over a stub controller. The
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

    [Fact]
    public async Task MediaInfo_IsNull_BeforeAnItemHasLoaded()
    {
        // Reachable and ordinary: a player built without WithMedia, a cleared queue, or the gap
        // between items. It used to throw InvalidOperationException here, which cost
        // FrameFlowStreamSummary a bare catch around a property read (#40 in BREAKING-CHANGES).
        await using var player = NewPlayer(PlaybackState.Idle, out _);

        Assert.Null(player.MediaInfo);
    }

    [Fact]
    public async Task MediaInfo_FollowsTheCoordinator_OnceAnItemIsLoaded()
    {
        await using var player = NewPlayer(PlaybackState.Playing, out var coordinator);
        var item = coordinator.Snapshot().Playlist[0];
        var loaded = new MediaInfo("test", TimeSpan.FromSeconds(3), [], []);

        coordinator.ReportCurrent(item, loaded, wrapped: false);

        Assert.Same(loaded, player.MediaInfo);
        Assert.Equal(loaded.Duration, player.Duration);
    }

    // ── PlaylistChanged (#311) ──────────────────────────────────────────────

    [Fact]
    public async Task PlaylistChanged_FiresOnceForEachEditVerb_WithRisingRevisions()
    {
        await using var player = NewPlayer(PlaybackState.Paused, out _);
        var seen = new List<PlaylistSnapshot>();
        using var _sub = player.PlaylistChanged.Subscribe(seen.Add);

        var added = await player.AddAsync(Source("b"));
        await player.EnqueueAsync(Source("x"));
        await player.SetNextAsync(Source("y"));
        await player.RemoveAsync(added);
        await player.ReplaceAsync([Source("d")]);
        await player.ClearAsync();

        Assert.Equal(6, seen.Count);
        Assert.Equal(seen.Select(s => s.Revision).Order(), seen.Select(s => s.Revision));
        Assert.Distinct(seen.Select(s => s.Revision));
    }

    [Fact]
    public async Task PlaylistChanged_CarriesTheSnapshotTheEditProduced()
    {
        // The payload is the queue as of that edit, not a handle to fetch one later: a second
        // edit landing first would otherwise hand both handlers the same later queue.
        await using var player = NewPlayer(PlaybackState.Paused, out _);
        var seen = new List<PlaylistSnapshot>();
        using var _sub = player.PlaylistChanged.Subscribe(seen.Add);

        var added = await player.AddAsync(Source("b"));
        var queued = await player.EnqueueAsync(Source("x"));

        Assert.Equal(added, seen[0].Playlist[^1]);
        Assert.Empty(seen[0].Queued);
        Assert.Equal([queued], seen[1].Queued);
    }

    [Fact]
    public async Task PlaylistChanged_DoesNotFire_ForAnEditThatChangedNothing()
    {
        // Remove returns the same queue when it held no such item, so the revision does not move
        // and there is nothing to redraw. Gating on the call rather than the revision would
        // report a change that did not happen.
        await using var player = NewPlayer(PlaybackState.Paused, out var coordinator);
        var foreign = new PlaylistCoordinator([Source("z")], RepeatMode.Off).Snapshot().Playlist[0];
        var seen = new List<PlaylistSnapshot>();
        using var _sub = player.PlaylistChanged.Subscribe(seen.Add);

        var removed = await player.RemoveAsync(foreign);
        coordinator.ItemEnded();

        Assert.False(removed.IsSuccess);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task PlaylistChanged_FiresForBothHalvesOfAHandOff()
    {
        // A hand-off is two commits, and a queue renderer wants both: the take moves Current,
        // and the report flips CurrentStarted. Neither is an edit, and both change what
        // GetPlaylist would return, which is what this stream answers.
        await using var player = NewPlayer(PlaybackState.Playing, out var coordinator);
        var seen = new List<PlaylistSnapshot>();
        using var _sub = player.PlaylistChanged.Subscribe(seen.Add);

        var taken = coordinator.TakeStart();
        coordinator.ReportCurrent(taken!, new MediaInfo("t", TimeSpan.FromSeconds(1), [], []), false);

        Assert.Equal(2, seen.Count);
        Assert.Same(taken, seen[0].Current);
        Assert.False(seen[0].CurrentStarted);
        Assert.True(seen[1].CurrentStarted);
    }

    [Fact]
    public async Task PlaylistChanged_RevisionNeverGoesBackwards_AcrossASingleSourceLoad()
    {
        // LoadSource builds a queue holding only the loaded source. Built with
        // PlaylistQueue.Create its revision would restart at zero, so a subscriber that had
        // already seen 3 would be handed 0 and a consumer ordering on Revision would discard
        // the replacement as stale.
        var coordinator = new PlaylistCoordinator(RepeatMode.Off);
        await using var player = new PlaylistMediaPlayerCore(
            new StubController { State = PlaybackState.Paused },
            coordinator,
            audioSink: null,
            NullLogger.Instance
        );
        var seen = new List<long>();

        await player.AddAsync(Source("a"));
        await player.AddAsync(Source("b"));
        using var _sub = player.PlaylistChanged.Subscribe(s => seen.Add(s.Revision));
        var beforeLoad = player.GetPlaylist().Revision;

        coordinator.LoadSource(Source("loaded"));

        var afterLoad = Assert.Single(seen);
        Assert.True(
            afterLoad > beforeLoad,
            $"revision went {beforeLoad} -> {afterLoad}; it must only ever rise"
        );
        Assert.Equal(afterLoad, player.GetPlaylist().Revision);
    }

    [Fact]
    public async Task PlaylistChanged_DeliversInCommitOrder_WhenAHandlerEditsFromInsideItself()
    {
        // Pins the re-entrant case: a handler may edit the queue, and its own edit arrives after
        // the notification it is handling rather than nested inside it.
        //
        // This does NOT cover the cross-thread ordering the drain exists for. Re-entry is on one
        // thread, so it comes out in order under any of the designs considered. The race the
        // drain fixes needs a thread descheduled between releasing the lock and publishing, and
        // nothing here can place it there.
        await using var player = NewPlayer(PlaybackState.Paused, out _);
        var seen = new List<int>();
        var reentered = false;

        using var _sub = player.PlaylistChanged.Subscribe(s =>
        {
            seen.Add(s.Playlist.Count);
            if (reentered)
                return;
            reentered = true;
            player.AddAsync(Source("second")).GetAwaiter().GetResult();
        });

        await player.AddAsync(Source("first"));

        // Started at one item: the first add makes two, the re-entrant add makes three.
        Assert.Equal([2, 3], seen);
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
