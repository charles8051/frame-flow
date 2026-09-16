namespace FrameFlow.Playback.Tests;

/// <summary>
/// Unit tests for <see cref="PlaylistCoordinator"/> as the cell around a
/// <see cref="PlaylistQueue"/>: argument checks, the attached session's skip and jump handlers,
/// the transition stream, and that each operation replaces the value it holds. The queue's rules
/// are tested on the value, in <c>PlaylistQueueTests</c>.
/// </summary>
public sealed class PlaylistCoordinatorTests
{
    private sealed record FakeSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    private static FakeSource S(string name) => new(name);

    private static MediaInfo Info(double seconds = 3) =>
        new("test", TimeSpan.FromSeconds(seconds), [], []);

    [Fact]
    public void EmptyInitialQueue_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new PlaylistCoordinator(Array.Empty<IMediaSource>(), RepeatMode.Off)
        );
    }

    [Fact]
    public void Replace_WithNoSources_Throws()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);
        Assert.Throws<ArgumentException>(() => coord.Replace([]));
    }

    [Fact]
    public void AddAndEnqueue_RejectANullSource_AndLeaveTheQueueAlone()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);
        var before = coord.Queue;

        var add = Assert.Throws<ArgumentNullException>(() => coord.Add(null!));
        var enqueue = Assert.Throws<ArgumentNullException>(() => coord.Enqueue(null!));

        Assert.Equal("source", add.ParamName);
        Assert.Equal("source", enqueue.ParamName);
        Assert.Same(before, coord.Queue);
    }

    [Fact]
    public void SetNext_Null_AddsNothing()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        _ = coord.TakeStart();
        var before = coord.Queue;

        Assert.Null(coord.SetNext(null));
        Assert.Same(before, coord.Queue);
    }

    [Fact]
    public void AnEdit_ReplacesTheHeldValue_AndLeavesTheOldOneAsItWas()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);
        var before = coord.Queue;

        var b = coord.Add(S("b"));

        Assert.Single(before.Playlist);
        Assert.Equal(2, coord.Queue.Playlist.Count);
        Assert.Same(b, coord.Queue.Playlist[1]);
        Assert.True(coord.Queue.Revision > before.Revision);
    }

    [Fact]
    public void ReportCurrent_UpdatesStateAndFiresTransitionWithTheItem()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.All);
        var a = coord.TakeStart()!;

        var seen = new List<PlaylistTransition>();
        using var sub = coord.SourceTransitioned.Subscribe(new Collector(seen.Add));

        coord.ReportCurrent(a, Info(3), wrapped: false);
        Assert.Same(a.Source, coord.CurrentSource);
        Assert.Equal(TimeSpan.FromSeconds(3), coord.CurrentDuration);

        var b = coord.DecideNext(PlaylistAdvance.EndOfStream).Item!;
        coord.ReportCurrent(b, Info(5), wrapped: true);
        Assert.Same(b.Source, coord.CurrentSource);
        Assert.Equal(TimeSpan.FromSeconds(5), coord.CurrentDuration);

        Assert.Equal(2, seen.Count);
        Assert.Equal(0, seen[0].Index);
        Assert.Same(a, seen[0].Item);
        Assert.False(seen[0].Wrapped);
        Assert.Equal(1, seen[1].Index);
        Assert.Same(b, seen[1].Item);
        Assert.True(seen[1].Wrapped);
    }

    [Fact]
    public void ATransitionSubscriber_CanEditTheQueue()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        var a = coord.TakeStart()!;
        var jumps = new List<JumpRequest>();
        using var sub = coord.SourceTransitioned.Subscribe(
            new Collector(_ => jumps.Add(coord.RequestJump(coord.Snapshot().Playlist[1])))
        );

        coord.ReportCurrent(a, Info(), wrapped: false);

        Assert.Equal([JumpRequest.Pending], jumps);
        Assert.True(coord.HasPendingJump);
    }

    [Fact]
    public void RequestSkip_InvokesTheAttachedSession()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);
        var skips = 0;
        _ = coord.AttachSession(() => Interlocked.Increment(ref skips), () => { });

        coord.RequestSkip();
        coord.RequestSkip();

        Assert.Equal(2, skips);
        Assert.Null(coord.Queue.LatchedAdvance);
    }

    [Fact]
    public void RequestSkip_WithNoSessionAttached_LatchesForTheNextPlay()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);
        var token = coord.AttachSession(() => throw new InvalidOperationException(), () => { });
        coord.DetachSession(token);

        coord.RequestSkip();
        Assert.Equal(PlaylistAdvance.Skip, coord.ConsumeLatchedAdvance());
        Assert.Null(coord.ConsumeLatchedAdvance()); // consumed once
    }

    [Fact]
    public void DetachSession_IgnoresAStaleToken()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);
        var oldToken = coord.AttachSession(() => { }, () => { });
        var skips = 0;
        _ = coord.AttachSession(() => skips++, () => { });

        coord.DetachSession(oldToken);
        coord.RequestSkip();

        Assert.Equal(1, skips);
    }

    [Fact]
    public void RequestJump_PokesTheAttachedSession_OnlyWhenTheJumpIsPending()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        var a = coord.TakeStart()!;
        var pokes = 0;
        _ = coord.AttachSession(() => { }, () => pokes++);

        Assert.Equal(JumpRequest.AlreadyCurrent, coord.RequestJump(a));
        Assert.Equal(
            JumpRequest.NotInPlayer,
            coord.RequestJump(new PlaylistCoordinator([S("z")], RepeatMode.Off).Snapshot().Playlist[0])
        );
        Assert.Equal(0, pokes);

        Assert.Equal(JumpRequest.Pending, coord.RequestJump(coord.Snapshot().Playlist[1]));
        Assert.Equal(1, pokes);
    }

    [Fact]
    public void Replace_PokesTheAttachedSession()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);
        var pokes = 0;
        _ = coord.AttachSession(() => { }, () => pokes++);

        var items = coord.Replace([S("d"), S("e")]);

        Assert.Equal(1, pokes);
        Assert.Same(items[0], coord.Snapshot().PendingJump);
    }

    [Fact]
    public void TheFailureCount_IsTheCoordinatorsAcrossSessions()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);

        for (var i = 0; i < 4; i++)
            Assert.False(coord.ItemFailed(TimeSpan.Zero, TimeSpan.Zero));
        // A new session reads the same count.
        for (var i = 0; i < 4; i++)
            Assert.False(coord.ItemFailed(TimeSpan.Zero, TimeSpan.Zero));

        Assert.True(coord.ItemFailed(TimeSpan.Zero, TimeSpan.Zero));
    }

    // ── The load a controller of one source makes (the one-player-type record) ──────────────

    [Fact]
    public void LoadSource_MakesTheSourceTheOnlyItem()
    {
        var coord = new PlaylistCoordinator(RepeatMode.One);
        var source = S("a");

        coord.LoadSource(source);

        var only = Assert.Single(coord.Queue.Playlist);
        Assert.Same(source, only.Source);
        Assert.Equal(RepeatMode.One, coord.RepeatMode);
    }

    [Fact]
    public void LoadSource_AfterAnEnqueue_DropsIt()
    {
        // A load is a new queue. Only a replay keeps what the player holds.
        var coord = new PlaylistCoordinator(RepeatMode.Off);
        coord.LoadSource(S("a"));
        coord.Enqueue(S("b"));

        coord.LoadSource(S("a"));

        Assert.Empty(coord.Queue.Queued);
        Assert.Single(coord.Queue.Playlist);
    }

    [Fact]
    public void LoadSource_AfterAReservation_KeepsTheQueue_AndTheMarkIsSpentOnce()
    {
        var coord = new PlaylistCoordinator(RepeatMode.Off);
        coord.LoadSource(S("a"));
        var enqueued = coord.Enqueue(S("b"));
        Assert.True(coord.ReserveStart());

        coord.LoadSource(S("a"));

        // The load left the queue alone: the reservation stands, and so does the enqueued item.
        Assert.NotNull(coord.Queue.ReservedStart);
        Assert.Same(enqueued, Assert.Single(coord.Queue.Queued));

        // The next load is an ordinary one: the mark was spent.
        coord.LoadSource(S("a"));
        Assert.Null(coord.Queue.ReservedStart);
        Assert.Empty(coord.Queue.Queued);
    }

    [Fact]
    public void LoadSource_AfterAReservationAndAReplace_KeepsTheReplacement()
    {
        // A replace between the replay's reservation and its load clears the reservation and makes
        // its first item the pending jump. The load must keep that: rebuilding the queue would
        // play the source the replay reloaded and discard what the caller asked for.
        var coord = new PlaylistCoordinator(RepeatMode.Off);
        coord.LoadSource(S("a"));
        Assert.True(coord.ReserveStart());

        var replaced = coord.Replace([S("b"), S("c")]);
        coord.LoadSource(S("a"));

        Assert.Equal(replaced, coord.Queue.Playlist);
        Assert.Same(replaced[0], coord.Queue.PendingJump);
    }

    [Fact]
    public void ReserveStart_OnAnEmptyPlayer_LeavesTheNextLoadOrdinary()
    {
        var coord = new PlaylistCoordinator(RepeatMode.Off);

        Assert.False(coord.ReserveStart());

        var source = S("a");
        coord.LoadSource(source);
        Assert.Same(source, Assert.Single(coord.Queue.Playlist).Source);
    }

    private sealed class Collector : IObserver<PlaylistTransition>
    {
        private readonly Action<PlaylistTransition> _onNext;

        public Collector(Action<PlaylistTransition> onNext) => _onNext = onNext;

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(PlaylistTransition value) => _onNext(value);
    }
}
