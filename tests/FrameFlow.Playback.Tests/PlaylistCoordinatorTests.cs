namespace FrameFlow.Playback.Tests;

/// <summary>
/// Deterministic unit tests for <see cref="PlaylistCoordinator"/>: the playlist, cursor and
/// one-shot collections, the order an advance takes items in, jumps and edits. No FFmpeg or
/// corpus required: this exercises the decisions in isolation from the decode runtime.
/// </summary>
/// <remarks>
/// The numbered tests are the Validation rows of the draft ADR
/// <c>docs/adr/playlist-queue-model.md</c>. Rows 1 to 5 and 8 repeat the probes that ADR
/// records against the coordinator before it; each probe's result on that tree is quoted.
/// </remarks>
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

    /// <summary>
    /// Takes the start item, then advances <paramref name="steps"/> times for
    /// <paramref name="reason"/>. Returns the sources in order, marking a wrap with "^" and the
    /// end with "|".
    /// </summary>
    private static string Walk(
        PlaylistCoordinator coord,
        int steps,
        PlaylistAdvance reason = PlaylistAdvance.EndOfStream
    )
    {
        var start = coord.TakeStart()!;
        return start.Source.DisplayName + " " + Advance(coord, steps, reason);
    }

    private static string Advance(
        PlaylistCoordinator coord,
        int steps,
        PlaylistAdvance reason = PlaylistAdvance.EndOfStream
    )
    {
        var names = new List<string>();
        for (var i = 0; i < steps; i++)
        {
            var d = coord.DecideNext(reason);
            if (d.Kind == PlaylistCoordinator.NextKind.End)
            {
                names.Add("|");
                break;
            }
            names.Add((d.Wrapped ? "^" : "") + d.Item!.Source.DisplayName);
        }
        return string.Join(" ", names);
    }

    private static string Next(PlaylistCoordinator coord, PlaylistAdvance reason = PlaylistAdvance.EndOfStream) =>
        Advance(coord, 1, reason);

    // ── Construction and basic orders ───────────────────────────────────────

    [Fact]
    public void EmptyInitialQueue_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new PlaylistCoordinator(Array.Empty<IMediaSource>(), RepeatMode.Off)
        );
    }

    [Fact]
    public void Off_PlaysThePlaylistThenEnds()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        Assert.Equal("a b c |", Walk(coord, 3));
    }

    [Fact]
    public void All_LoopsAndFlagsTheWrap()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.All);
        Assert.Equal("a b ^a b ^a b", Walk(coord, 5));
    }

    [Fact]
    public void One_ReplaysTheCurrentItemOnANaturalEndOrAFault()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.One);
        var a = coord.TakeStart()!;

        foreach (var reason in new[] { PlaylistAdvance.EndOfStream, PlaylistAdvance.Fault })
        {
            var d = coord.DecideNext(reason);
            Assert.Equal(PlaylistCoordinator.NextKind.Replay, d.Kind);
            Assert.Same(a, d.Item);
        }
    }

    [Fact]
    public void One_ReplaysAOneShotItemThatIsCurrent()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.One);
        _ = coord.TakeStart();
        var x = coord.SetNext(S("x"))!;

        Assert.Same(x, coord.DecideNext(PlaylistAdvance.Skip).Item);
        var replay = coord.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.Equal(PlaylistCoordinator.NextKind.Replay, replay.Kind);
        Assert.Same(x, replay.Item);
    }

    [Fact]
    public void All_SingleClipWrap_IsReplayNotAdvance()
    {
        // The canonical signage attract/panel loop: ONE clip, RepeatMode.All. Each wrap lands
        // back on the very same source object, so the decision must be a Replay (reuse the live
        // runtime in place — the gapless single-clip loop), NOT an Advance (which the session
        // would service with a full teardown + rebuild).
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);
        var a = coord.TakeStart()!;

        for (var i = 0; i < 4; i++)
        {
            var d = coord.DecideNext(PlaylistAdvance.EndOfStream);
            Assert.Equal(PlaylistCoordinator.NextKind.Replay, d.Kind);
            Assert.Same(a, d.Item);
            // Wrapping a single-item playlist still flags the wrap, so the transition report
            // is unchanged.
            Assert.True(d.Wrapped);
        }
    }

    [Fact]
    public void BackToBackSameSourceObject_IsReplay_OfADifferentItem()
    {
        // The same source object added twice is two items. Across the boundary the runtime can
        // still be reused (same demuxer, decoders, device), so it is a Replay.
        var a = S("a");
        var coord = new PlaylistCoordinator([a, a], RepeatMode.All);
        var first = coord.TakeStart()!;

        var d = coord.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.Equal(PlaylistCoordinator.NextKind.Replay, d.Kind);
        Assert.NotSame(first, d.Item);
        Assert.Same(a, d.Item!.Source);
        Assert.False(d.Wrapped);
    }

    [Fact]
    public void DistinctButEqualSources_RebuildNotReplay()
    {
        // Reference identity, not value equality, gates reuse: only the exact same source object
        // is guaranteed to share an open demuxer, decoders, decode device and presenter binding.
        var a1 = S("same");
        var a2 = S("same");
        Assert.Equal(a1, a2);
        Assert.NotSame(a1, a2);

        var coord = new PlaylistCoordinator([a1, a2], RepeatMode.All);
        _ = coord.TakeStart();

        var d = coord.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.Equal(PlaylistCoordinator.NextKind.Advance, d.Kind);
        Assert.Same(a2, d.Item!.Source);
    }

    [Fact]
    public void Enqueue_PlaysAfterThePlaylistItemsLeftInThePass()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        _ = coord.Enqueue(S("x"));
        _ = coord.Enqueue(S("y"));

        Assert.Equal("a b x y |", Walk(coord, 4));
    }

    [Fact]
    public void SetNext_PlaysAheadOfThePlaylist()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        _ = coord.TakeStart();
        _ = coord.SetNext(S("jump"));

        Assert.Equal("jump b |", Advance(coord, 3));
    }

    [Fact]
    public void SetNext_Null_AddsNothing()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        _ = coord.TakeStart();
        var revision = coord.Snapshot().Revision;

        Assert.Null(coord.SetNext(null));
        Assert.Equal(revision, coord.Snapshot().Revision);
        Assert.Equal("b", Next(coord));
    }

    [Fact]
    public void AddedItems_StayAfterTheyPlay_AndLoopUnderAll()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);
        _ = coord.TakeStart();
        _ = coord.Add(S("b"));

        Assert.Equal("b ^a b ^a", Advance(coord, 4));
        Assert.Equal(["a", "b"], coord.Snapshot().Playlist.Select(i => i.Source.DisplayName));
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
    public void RepeatMode_AllToOff_StopsLooping()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.All);
        Assert.Equal("a b", Walk(coord, 1));

        // Under All this would wrap to a; switching to Off ends it instead.
        coord.RepeatMode = RepeatMode.Off;
        Assert.Equal("|", Next(coord));
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
    public void Revision_RisesOnEditsAndHandOffs()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);
        var r0 = coord.Snapshot().Revision;

        var a = coord.TakeStart()!;
        var r1 = coord.Snapshot().Revision;
        _ = coord.Add(S("b"));
        var r2 = coord.Snapshot().Revision;
        coord.ReportCurrent(a, Info(), wrapped: false);
        var r3 = coord.Snapshot().Revision;

        Assert.True(r0 < r1 && r1 < r2 && r2 < r3, $"{r0} {r1} {r2} {r3}");
    }

    // ── Validation rows ─────────────────────────────────────────────────────

    [Fact]
    public void Row01_SetNextUnderAll_DoesNotGrowTheRotation()
    {
        // Before: a c b c a c b c (#171).
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.All);
        _ = coord.TakeStart();
        _ = coord.SetNext(S("c"));

        Assert.Equal("c b c ^a b c ^a b c", Advance(coord, 9));
    }

    [Fact]
    public void Row02_SwitchToAllMidQueue_WrapsOverTheWholePlaylist()
    {
        // Before: b c c c ... — only items taken after the switch looped.
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        Assert.Equal("a b", Walk(coord, 1));
        coord.RepeatMode = RepeatMode.All;

        Assert.Equal("c ^a b c", Advance(coord, 4));
    }

    [Fact]
    public void Row03_SkipUnderOne_MovesOn()
    {
        // Before: a a a a — every advance under One replayed.
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.One);
        _ = coord.TakeStart();

        Assert.Equal("b", Next(coord, PlaylistAdvance.Skip));
        // The item skipped to repeats on its natural end.
        Assert.Equal(PlaylistCoordinator.NextKind.Replay, coord.DecideNext(PlaylistAdvance.EndOfStream).Kind);
        // And a skip at the end of the pass goes back to the first item.
        Assert.Equal("^a", Next(coord, PlaylistAdvance.Skip));
    }

    [Fact]
    public void Row04_AllThenOffThenAll_KeepsEveryItemInTheRotation()
    {
        // Before: c a b a b ... — c left the rotation because it was taken under Off.
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.All);
        Assert.Equal("a b", Walk(coord, 1));
        coord.RepeatMode = RepeatMode.Off;
        Assert.Equal("c", Next(coord));
        coord.RepeatMode = RepeatMode.All;

        Assert.Equal("^a b c", Advance(coord, 3));
    }

    [Fact]
    public void Row05_RotationPatternUnderAll_HoldsNothingAfterEachItemPlays()
    {
        // Before: the loop buffer held 1,001 items after 1,000 hand-offs.
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);
        _ = coord.TakeStart();

        for (var i = 0; i < 1000; i++)
        {
            _ = coord.Enqueue(S($"x{i}"));
            Assert.Equal($"x{i}", Next(coord));
        }

        var snapshot = coord.Snapshot();
        Assert.Single(snapshot.Playlist);
        Assert.Empty(snapshot.Queued);
        // When the pattern stops enqueueing, the player wraps to the playlist.
        Assert.Equal("^a", Next(coord));
    }

    [Theory]
    [InlineData(RepeatMode.Off)]
    [InlineData(RepeatMode.All)]
    public void Row06_RotationPattern_KeepsItsOrder(RepeatMode repeat)
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], repeat);
        var order = new List<string> { coord.TakeStart()!.Source.DisplayName };

        for (var i = 0; i < 6; i++)
        {
            _ = coord.Enqueue(S($"x{i}"));
            order.Add(Next(coord));
        }

        Assert.Equal("a b c x0 x1 x2 x3", string.Join(" ", order));
    }

    [Fact]
    public void Row07_SetNextTwice_PlaysTheLaterCallFirst()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        _ = coord.TakeStart();
        _ = coord.SetNext(S("x"));
        _ = coord.SetNext(S("y"));

        Assert.Equal("y x b |", Advance(coord, 4));
    }

    [Theory]
    [InlineData(RepeatMode.Off)]
    [InlineData(RepeatMode.All)]
    [InlineData(RepeatMode.One)]
    public void Row08_AnItemThatFailsToStart_IsPassedOver(RepeatMode repeat)
    {
        // Before, under One, b was never taken: the advance after a replayed a. A cursor that
        // moved only when an item started would return b again here.
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], repeat);
        _ = coord.TakeStart();
        var toB = repeat == RepeatMode.One ? PlaylistAdvance.Skip : PlaylistAdvance.EndOfStream;
        Assert.Equal("b", Next(coord, toB));

        Assert.Equal("c", Next(coord, PlaylistAdvance.FailedStart));
    }

    [Fact]
    public void Row09_JumpToAPlaylistItem_ContinuesThePlaylistFromThere()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.All);
        _ = coord.TakeStart();
        var c = coord.Snapshot().Playlist[2];

        Assert.Equal(JumpRequest.Pending, coord.RequestJump(c));
        Assert.Equal("c ^a b c", Advance(coord, 4, PlaylistAdvance.Skip));
    }

    [Fact]
    public void Row09_JumpToAOneShotItem_TakesItOutOfItsCollection()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        _ = coord.TakeStart();
        var x = coord.Enqueue(S("x"));

        _ = coord.RequestJump(x);
        Assert.Equal("x b |", Advance(coord, 3, PlaylistAdvance.Skip));
    }

    [Fact]
    public void Row09_JumpToTheCurrentItem_OrOneNotInThePlayer_TakesNothing()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        var a = coord.TakeStart()!;
        var other = new PlaylistCoordinator([S("z")], RepeatMode.Off).Snapshot().Playlist[0];

        Assert.Equal(JumpRequest.AlreadyCurrent, coord.RequestJump(a));
        Assert.Equal(JumpRequest.NotInPlayer, coord.RequestJump(other));
        Assert.False(coord.HasPendingJump);
    }

    [Fact]
    public void Row10_ALaterJumpReplacesAnEarlierOne()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        _ = coord.TakeStart();
        var playlist = coord.Snapshot().Playlist;

        _ = coord.RequestJump(playlist[1]);
        _ = coord.RequestJump(playlist[2]);

        Assert.Equal("c", Next(coord, PlaylistAdvance.Skip));
    }

    [Fact]
    public void Row10_ClearAndRemovingTheTarget_DiscardAPendingJump()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        _ = coord.TakeStart();
        var c = coord.Snapshot().Playlist[2];

        _ = coord.RequestJump(c);
        Assert.True(coord.Remove(c));
        Assert.False(coord.HasPendingJump);
        Assert.Equal("b", Next(coord, PlaylistAdvance.Skip));

        _ = coord.RequestJump(coord.Snapshot().Playlist[0]);
        coord.Clear();
        Assert.False(coord.HasPendingJump);
        Assert.Null(coord.Snapshot().PendingJump);
    }

    [Fact]
    public void Row11_RemovingTheCurrentItem_ContinuesWithTheItemThatFollowedIt()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        _ = coord.TakeStart();
        var b = coord.DecideNext(PlaylistAdvance.EndOfStream).Item!;

        Assert.True(coord.Remove(b));
        Assert.Equal("c", Next(coord));
    }

    [Fact]
    public void Row12_RemovingTheCursorItemWhileAOneShotPlays_ContinuesWithItsSuccessor()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        var a = coord.TakeStart()!;
        _ = coord.SetNext(S("x"));
        Assert.Equal("x", Next(coord));

        Assert.True(coord.Remove(a));
        Assert.Equal("b", Next(coord));
    }

    [Fact]
    public void Row12_RemovingAnItemBeforeTheCursor_LeavesTheNextItemUnchanged()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        var a = coord.TakeStart()!;
        Assert.Equal("b", Next(coord));

        Assert.True(coord.Remove(a));
        Assert.Equal(1, coord.Snapshot().ResumeIndex);
        Assert.Equal("c", Next(coord));
    }

    [Fact]
    public void Row13_Replace_ReturnsTheNewItems_AndJumpsToTheFirst()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        _ = coord.TakeStart();
        _ = coord.Enqueue(S("x"));

        var items = coord.Replace([S("d"), S("e")]);

        Assert.Equal(2, items.Count);
        var snapshot = coord.Snapshot();
        Assert.Equal(items, snapshot.Playlist);
        Assert.Empty(snapshot.Queued);
        Assert.Same(items[0], snapshot.PendingJump);
        Assert.Equal("d e |", Advance(coord, 3, PlaylistAdvance.Skip));
    }

    [Fact]
    public void Row13_Replace_WithNoSources_Throws()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);
        Assert.Throws<ArgumentException>(() => coord.Replace([]));
    }

    [Fact]
    public void Row14_ClearUnderOne_ThenSkipOrNaturalEnd_EndsThePlaylist()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.One);
        _ = coord.TakeStart();
        coord.Clear();
        Assert.Equal("|", Next(coord, PlaylistAdvance.Skip));

        // A cleared current item does not repeat on its natural end either.
        var coord2 = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.One);
        _ = coord2.TakeStart();
        coord2.Clear();
        Assert.Equal("|", Next(coord2));
    }

    [Fact]
    public void Row15_TheReplayStart_AtTheEndOfAPass_IsTheFirstItem()
    {
        // Before: nothing to take. The session reports a start with Wrapped false.
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        Assert.Equal("a b |", Walk(coord, 2));
        coord.RepeatMode = RepeatMode.All;

        Assert.True(coord.ReserveStart());
        Assert.Equal("a", coord.TakeStart()!.Source.DisplayName);
        Assert.Equal(1, coord.Snapshot().ResumeIndex);
    }

    [Fact]
    public void Row15_TheReplayStart_TakesAPendingJumpFirst()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b"), S("c")], RepeatMode.Off);
        Assert.Equal("a b c |", Walk(coord, 3));
        Assert.Equal(JumpRequest.Pending, coord.RequestJump(coord.Snapshot().Playlist[1]));

        Assert.True(coord.ReserveStart());
        Assert.Equal("b", coord.TakeStart()!.Source.DisplayName);
    }

    [Fact]
    public void Row15_WithNothingInThePlayer_ThereIsNoReplayStart()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);
        _ = coord.TakeStart();
        coord.Clear();

        Assert.False(coord.ReserveStart());
        Assert.Null(coord.TakeStart());
    }

    [Fact]
    public void Row16_AReservedStart_SurvivesAClear()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.Off);
        Assert.Equal("a |", Walk(coord, 1));

        Assert.True(coord.ReserveStart());
        coord.Clear();

        Assert.Equal("a", coord.TakeStart()!.Source.DisplayName);
        Assert.Null(coord.TakeStart());
    }

    [Fact]
    public void Row17_TheFailureCount_IsTheCoordinatorsAcrossSessions()
    {
        var coord = new PlaylistCoordinator([S("a")], RepeatMode.All);

        for (var i = 0; i < 4; i++)
            Assert.False(coord.Failures.ItemFailed(TimeSpan.Zero, TimeSpan.Zero));
        // A new session reads the same guard.
        for (var i = 0; i < 4; i++)
            Assert.False(coord.Failures.ItemFailed(TimeSpan.Zero, TimeSpan.Zero));

        Assert.True(coord.Failures.ItemFailed(TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public void Row18_ATakenItem_IsCurrentBeforeItStarts()
    {
        var coord = new PlaylistCoordinator([S("a"), S("b")], RepeatMode.Off);
        var a = coord.TakeStart()!;

        var opening = coord.Snapshot();
        Assert.Same(a, opening.Current);
        Assert.False(opening.CurrentStarted);
        Assert.Null(coord.CurrentSource);

        // It can be named while it opens: a jump to it does nothing, and it can be removed.
        Assert.Equal(JumpRequest.AlreadyCurrent, coord.RequestJump(a));
        Assert.True(coord.Remove(a));

        coord.ReportCurrent(a, Info(), wrapped: false);
        var started = coord.Snapshot();
        Assert.Same(a, started.Current);
        Assert.True(started.CurrentStarted);
        Assert.Same(a.Source, coord.CurrentSource);

        // Removed while opening, it does not stop, and the playlist continues after it.
        Assert.Equal("b", Next(coord));

        // A one-shot item leaves its collection when it is taken, so while it opens it can be
        // named only as the current item.
        var x = coord.Enqueue(S("x"));
        Assert.Equal("x", Next(coord));
        Assert.Empty(coord.Snapshot().Queued);
        Assert.Equal(JumpRequest.AlreadyCurrent, coord.RequestJump(x));
        Assert.True(coord.Remove(x));
        Assert.False(coord.Remove(x));
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
