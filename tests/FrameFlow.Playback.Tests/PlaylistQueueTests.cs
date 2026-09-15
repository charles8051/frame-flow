namespace FrameFlow.Playback.Tests;

/// <summary>
/// Deterministic unit tests for <see cref="PlaylistQueue"/>: the playlist, cursor and one-shot
/// collections, the order an advance takes items in, jumps and edits, each as an operation on an
/// immutable value. No FFmpeg, no lock and no session.
/// </summary>
/// <remarks>
/// The numbered tests are the Validation rows of <c>docs/adr/playlist-queue-model.md</c>. They ran
/// against <see cref="PlaylistCoordinator"/> until the queue became a value, step 2 of
/// <c>docs/adr/playlist-session-protocol.md</c>. <c>PlaylistCoordinatorTests</c> keeps what
/// belongs to the cell: the attached session, the transition stream and argument checks.
/// </remarks>
public sealed class PlaylistQueueTests
{
    private sealed record FakeSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    private static FakeSource S(string name) => new(name);

    private static PlaylistItem Item(string name) => new(S(name));

    private static PlaylistQueue Queue(RepeatMode repeat, params string[] names) =>
        PlaylistQueue.Create(names.Select(Item), repeat);

    private static MediaInfo Info(double seconds = 3) =>
        new("test", TimeSpan.FromSeconds(seconds), [], []);

    private static PlaylistItem? Take(ref PlaylistQueue queue)
    {
        (queue, var item) = queue.TakeStart();
        return item;
    }

    /// <summary>
    /// Takes the start item, then advances <paramref name="steps"/> times for
    /// <paramref name="reason"/>. Returns the sources in order, marking a wrap with "^" and the
    /// end with "|".
    /// </summary>
    private static string Walk(
        ref PlaylistQueue queue,
        int steps,
        PlaylistAdvance reason = PlaylistAdvance.EndOfStream
    ) => Take(ref queue)!.Source.DisplayName + " " + Advance(ref queue, steps, reason);

    private static string Advance(
        ref PlaylistQueue queue,
        int steps,
        PlaylistAdvance reason = PlaylistAdvance.EndOfStream
    )
    {
        var names = new List<string>();
        for (var i = 0; i < steps; i++)
        {
            (queue, var d) = queue.DecideNext(reason);
            if (d.Kind == PlaylistQueue.NextKind.End)
            {
                names.Add("|");
                break;
            }
            names.Add((d.Wrapped ? "^" : "") + d.Item!.Source.DisplayName);
        }
        return string.Join(" ", names);
    }

    private static string Next(
        ref PlaylistQueue queue,
        PlaylistAdvance reason = PlaylistAdvance.EndOfStream
    ) => Advance(ref queue, 1, reason);

    // ── The value ───────────────────────────────────────────────────────────

    [Fact]
    public void Operations_LeaveTheValueTheyAreAppliedToUnchanged()
    {
        PlaylistItem[] items = [Item("a"), Item("b"), Item("c")];
        var queue = PlaylistQueue.Create(items, RepeatMode.All);
        var twin = PlaylistQueue.Create(items, RepeatMode.All);

        _ = queue.TakeStart();
        _ = queue.DecideNext(PlaylistAdvance.EndOfStream);
        _ = queue.RequestJump(items[2]);
        _ = queue.Remove(items[1]);
        _ = queue.Add(Item("d"));
        _ = queue.Clear();
        _ = queue.ReportCurrent(items[0], Info());
        _ = queue.LatchAdvance(PlaylistAdvance.Skip);
        _ = queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero);
        _ = queue.WithRepeatMode(RepeatMode.Off);

        Assert.Equal(twin, queue);
        Assert.Equal(3, queue.Playlist.Count);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void Equality_IsStructural()
    {
        PlaylistItem[] items = [Item("a"), Item("b")];
        var x = Item("x");

        var first = PlaylistQueue.Create(items, RepeatMode.Off).Enqueue(x).TakeStart().Queue;
        var second = PlaylistQueue.Create(items, RepeatMode.Off).Enqueue(x).TakeStart().Queue;

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        // The same edits with a different item are a different value.
        var other = PlaylistQueue.Create(items, RepeatMode.Off).Enqueue(Item("x")).TakeStart().Queue;
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Revision_RisesOnEditsAndHandOffs()
    {
        var queue = Queue(RepeatMode.Off, "a");
        var r0 = queue.Revision;

        var a = Take(ref queue)!;
        var r1 = queue.Revision;
        queue = queue.Add(Item("b"));
        var r2 = queue.Revision;
        (queue, _) = queue.ReportCurrent(a, Info());
        var r3 = queue.Revision;

        Assert.True(r0 < r1 && r1 < r2 && r2 < r3, $"{r0} {r1} {r2} {r3}");
    }

    [Fact]
    public void Revision_DoesNotRiseOnARepeatModeChangeALatchOrAFailure()
    {
        var queue = Queue(RepeatMode.Off, "a");
        _ = Take(ref queue);
        var revision = queue.Revision;

        queue = queue.WithRepeatMode(RepeatMode.All).LatchAdvance(PlaylistAdvance.Skip).ItemEnded();
        (queue, _) = queue.ConsumeLatchedAdvance();
        (queue, _) = queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(revision, queue.Revision);
    }

    [Fact]
    public void ReportCurrent_RecordsTheStartedItem_AndCountsStarts()
    {
        var queue = Queue(RepeatMode.All, "a", "b");
        Assert.False(queue.AnyStarted);
        var a = Take(ref queue)!;

        (queue, var first) = queue.ReportCurrent(a, Info(3));
        Assert.Equal(0, first);
        Assert.True(queue.AnyStarted);
        Assert.Same(a, queue.Reported);
        Assert.Equal(TimeSpan.FromSeconds(3), queue.ReportedDuration);

        (queue, var d) = queue.DecideNext(PlaylistAdvance.EndOfStream);
        (queue, var second) = queue.ReportCurrent(d.Item!, Info(5));
        Assert.Equal(1, second);
        Assert.Same(d.Item, queue.Reported);
        Assert.Equal(TimeSpan.FromSeconds(5), queue.ReportedDuration);

        // A start with no metadata has no known duration.
        (queue, d) = queue.DecideNext(PlaylistAdvance.EndOfStream);
        (queue, _) = queue.ReportCurrent(d.Item!, null);
        Assert.Equal(TimeSpan.Zero, queue.ReportedDuration);
    }

    // ── Expected repeats (the looping record's decision 6) ──────────────────

    [Fact]
    public void ExpectsRepeat_UnderOne_ForAStartedCurrentItem_AOneShotIncluded()
    {
        var queue = Queue(RepeatMode.One, "a", "b");
        var a = Take(ref queue)!;
        Assert.False(queue.ExpectsRepeat); // taken, not yet started

        (queue, _) = queue.ReportCurrent(a, Info());
        Assert.True(queue.ExpectsRepeat);

        // One replays a one-shot current item too.
        var x = Item("x");
        queue = queue.SetNext(x);
        (queue, _) = queue.DecideNext(PlaylistAdvance.Skip);
        (queue, _) = queue.ReportCurrent(x, Info());
        Assert.Same(x, queue.Current);
        Assert.True(queue.ExpectsRepeat);
    }

    [Fact]
    public void ExpectsRepeat_UnderAll_OnlyForTheOnlyPlaylistItem_WithNothingNextOrQueued()
    {
        var queue = Queue(RepeatMode.All, "a");
        var a = Take(ref queue)!;
        (queue, _) = queue.ReportCurrent(a, Info());
        Assert.True(queue.ExpectsRepeat);

        Assert.False(queue.SetNext(Item("x")).ExpectsRepeat);
        Assert.False(queue.Enqueue(Item("y")).ExpectsRepeat);
        Assert.False(queue.Add(Item("b")).ExpectsRepeat);
        Assert.False(queue.WithRepeatMode(RepeatMode.Off).ExpectsRepeat);
    }

    [Fact]
    public void ExpectsRepeat_UnderAll_SurvivesTheWrapThatTakesTheSameItemAgain()
    {
        // The repeat is a take of the same item. It must stay watched while it runs, so the take
        // keeps the item started.
        var queue = Queue(RepeatMode.All, "a");
        var a = Take(ref queue)!;
        (queue, _) = queue.ReportCurrent(a, Info());

        (queue, var wrap) = queue.DecideNext(PlaylistAdvance.EndOfStream);

        Assert.Same(a, wrap.Item);
        Assert.True(wrap.Wrapped);
        Assert.True(queue.CurrentStarted);
        Assert.True(queue.ExpectsRepeat);
    }

    [Fact]
    public void ExpectsRepeat_IsFalse_ForARemovedItem_AOneShotUnderAll_OrADifferentItemNotYetStarted()
    {
        // Removed.
        var queue = Queue(RepeatMode.One, "a");
        var a = Take(ref queue)!;
        (queue, _) = queue.ReportCurrent(a, Info());
        Assert.False(queue.Remove(a).Queue.ExpectsRepeat);

        // A one-shot current item under All is not the only playlist item.
        var all = Queue(RepeatMode.All, "a");
        var first = Take(ref all)!;
        (all, _) = all.ReportCurrent(first, Info());
        var x = Item("x");
        all = all.SetNext(x);
        (all, _) = all.DecideNext(PlaylistAdvance.EndOfStream);
        (all, _) = all.ReportCurrent(x, Info());
        Assert.Same(x, all.Current);
        Assert.False(all.ExpectsRepeat);

        // A playlist of two under All, and a different item taken but not yet started.
        var two = Queue(RepeatMode.All, "a", "b");
        var twoA = Take(ref two)!;
        (two, _) = two.ReportCurrent(twoA, Info());
        Assert.False(two.ExpectsRepeat);
        (two, _) = two.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.False(two.CurrentStarted);
        Assert.False(two.ExpectsRepeat);
    }

    [Fact]
    public void ExpectsRepeat_IsFalse_WhileAJumpIsPending_BecauseTheNextAdvanceTakesTheJump()
    {
        var queue = Queue(RepeatMode.One, "a", "b");
        var a = Take(ref queue)!;
        (queue, _) = queue.ReportCurrent(a, Info());
        Assert.True(queue.ExpectsRepeat);

        (queue, var request) = queue.RequestJump(queue.Playlist[1]);
        Assert.Equal(JumpRequest.Pending, request);
        Assert.False(queue.ExpectsRepeat);

        (_, var decision) = queue.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.Same(queue.Playlist[1], decision.Item);
    }

    [Fact]
    public void ALatchedAdvance_IsConsumedOnce()
    {
        var queue = Queue(RepeatMode.All, "a").LatchAdvance(PlaylistAdvance.Skip);

        (queue, var latched) = queue.ConsumeLatchedAdvance();
        Assert.Equal(PlaylistAdvance.Skip, latched);
        (_, latched) = queue.ConsumeLatchedAdvance();
        Assert.Null(latched);
    }

    // ── Basic orders ────────────────────────────────────────────────────────

    [Fact]
    public void Off_PlaysThePlaylistThenEnds()
    {
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        Assert.Equal("a b c |", Walk(ref queue, 3));
    }

    [Fact]
    public void All_LoopsAndFlagsTheWrap()
    {
        var queue = Queue(RepeatMode.All, "a", "b");
        Assert.Equal("a b ^a b ^a b", Walk(ref queue, 5));
    }

    [Fact]
    public void One_ReplaysTheCurrentItemOnANaturalEndOrAFault()
    {
        var queue = Queue(RepeatMode.One, "a", "b");
        var a = Take(ref queue)!;

        foreach (var reason in new[] { PlaylistAdvance.EndOfStream, PlaylistAdvance.Fault })
        {
            (queue, var d) = queue.DecideNext(reason);
            Assert.Equal(PlaylistQueue.NextKind.Replay, d.Kind);
            Assert.Same(a, d.Item);
        }
    }

    [Fact]
    public void One_ReplaysAOneShotItemThatIsCurrent()
    {
        var queue = Queue(RepeatMode.One, "a");
        _ = Take(ref queue);
        var x = Item("x");
        queue = queue.SetNext(x);

        (queue, var skip) = queue.DecideNext(PlaylistAdvance.Skip);
        Assert.Same(x, skip.Item);
        (_, var replay) = queue.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.Equal(PlaylistQueue.NextKind.Replay, replay.Kind);
        Assert.Same(x, replay.Item);
    }

    [Fact]
    public void All_SingleClipWrap_IsReplayNotAdvance()
    {
        // One clip under RepeatMode.All. Each wrap lands back on the very same source object, so
        // the decision must be a Replay (reuse the live runtime in place, the gapless single-clip
        // loop), NOT an Advance (which the session would service with a full teardown and rebuild).
        var queue = Queue(RepeatMode.All, "a");
        var a = Take(ref queue)!;

        for (var i = 0; i < 4; i++)
        {
            (queue, var d) = queue.DecideNext(PlaylistAdvance.EndOfStream);
            Assert.Equal(PlaylistQueue.NextKind.Replay, d.Kind);
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
        var queue = PlaylistQueue.Create([new PlaylistItem(a), new PlaylistItem(a)], RepeatMode.All);
        var first = Take(ref queue)!;

        (_, var d) = queue.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.Equal(PlaylistQueue.NextKind.Replay, d.Kind);
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

        var queue = PlaylistQueue.Create(
            [new PlaylistItem(a1), new PlaylistItem(a2)],
            RepeatMode.All
        );
        _ = Take(ref queue);

        (_, var d) = queue.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.Equal(PlaylistQueue.NextKind.Advance, d.Kind);
        Assert.Same(a2, d.Item!.Source);
    }

    [Fact]
    public void Enqueue_PlaysAfterThePlaylistItemsLeftInThePass()
    {
        var queue = Queue(RepeatMode.Off, "a", "b").Enqueue(Item("x")).Enqueue(Item("y"));

        Assert.Equal("a b x y |", Walk(ref queue, 4));
    }

    [Fact]
    public void SetNext_PlaysAheadOfThePlaylist()
    {
        var queue = Queue(RepeatMode.Off, "a", "b");
        _ = Take(ref queue);
        queue = queue.SetNext(Item("jump"));

        Assert.Equal("jump b |", Advance(ref queue, 3));
    }

    [Fact]
    public void EachStartTake_FollowsTheAdvanceOrder()
    {
        // Each new session takes its first item in the order an advance does: a next item, the
        // playlist item after the cursor, a queued item, and then the first playlist item again.
        var queue = Queue(RepeatMode.Off, "a", "b").SetNext(Item("x")).Enqueue(Item("y"));

        var starts = new List<string>();
        for (var i = 0; i < 5; i++)
            starts.Add(Take(ref queue)!.Source.DisplayName);

        Assert.Equal("x a b y a", string.Join(" ", starts));
        Assert.Empty(queue.Next);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public void RemovingAOneShotItemBeforeItPlays_TakesItOutOfItsCollection()
    {
        var queue = Queue(RepeatMode.Off, "a", "b");
        _ = Take(ref queue);
        var x = Item("x");
        var y = Item("y");
        queue = queue.SetNext(x).Enqueue(y);
        (queue, _) = queue.RequestJump(y);
        var revision = queue.Revision;

        (queue, var removedNext) = queue.Remove(x);
        (queue, var removedQueued) = queue.Remove(y);

        Assert.True(removedNext);
        Assert.True(removedQueued);
        Assert.Empty(queue.Next);
        Assert.Empty(queue.Queued);
        Assert.Equal(revision + 2, queue.Revision);
        // Removing the jump's target discards the jump, as it does for a playlist item.
        Assert.False(queue.HasPendingJump);
        Assert.Equal("b |", Advance(ref queue, 2));

        (_, var again) = queue.Remove(x);
        Assert.False(again);
    }

    [Fact]
    public void AddedItems_StayAfterTheyPlay_AndLoopUnderAll()
    {
        var queue = Queue(RepeatMode.All, "a");
        _ = Take(ref queue);
        queue = queue.Add(Item("b"));

        Assert.Equal("b ^a b ^a", Advance(ref queue, 4));
        Assert.Equal(["a", "b"], queue.Playlist.Select(i => i.Source.DisplayName));
    }

    [Fact]
    public void RepeatMode_AllToOff_StopsLooping()
    {
        var queue = Queue(RepeatMode.All, "a", "b");
        Assert.Equal("a b", Walk(ref queue, 1));

        // Under All this would wrap to a; switching to Off ends it instead.
        queue = queue.WithRepeatMode(RepeatMode.Off);
        Assert.Equal("|", Next(ref queue));
    }

    // ── Validation rows ─────────────────────────────────────────────────────

    [Fact]
    public void Row01_SetNextUnderAll_DoesNotGrowTheRotation()
    {
        // Before: a c b c a c b c (#171).
        var queue = Queue(RepeatMode.All, "a", "b", "c");
        _ = Take(ref queue);
        queue = queue.SetNext(Item("c"));

        Assert.Equal("c b c ^a b c ^a b c", Advance(ref queue, 9));
    }

    [Fact]
    public void Row02_SwitchToAllMidQueue_WrapsOverTheWholePlaylist()
    {
        // Before: b c c c ... — only items taken after the switch looped.
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        Assert.Equal("a b", Walk(ref queue, 1));
        queue = queue.WithRepeatMode(RepeatMode.All);

        Assert.Equal("c ^a b c", Advance(ref queue, 4));
    }

    [Fact]
    public void Row03_SkipUnderOne_MovesOn()
    {
        // Before: a a a a — every advance under One replayed.
        var queue = Queue(RepeatMode.One, "a", "b");
        _ = Take(ref queue);

        Assert.Equal("b", Next(ref queue, PlaylistAdvance.Skip));
        // The item skipped to repeats on its natural end.
        (queue, var replay) = queue.DecideNext(PlaylistAdvance.EndOfStream);
        Assert.Equal(PlaylistQueue.NextKind.Replay, replay.Kind);
        // And a skip at the end of the pass goes back to the first item.
        Assert.Equal("^a", Next(ref queue, PlaylistAdvance.Skip));
    }

    [Fact]
    public void Row04_AllThenOffThenAll_KeepsEveryItemInTheRotation()
    {
        // Before: c a b a b ... — c left the rotation because it was taken under Off.
        var queue = Queue(RepeatMode.All, "a", "b", "c");
        Assert.Equal("a b", Walk(ref queue, 1));
        queue = queue.WithRepeatMode(RepeatMode.Off);
        Assert.Equal("c", Next(ref queue));
        queue = queue.WithRepeatMode(RepeatMode.All);

        Assert.Equal("^a b c", Advance(ref queue, 3));
    }

    [Fact]
    public void Row05_RotationPatternUnderAll_HoldsNothingAfterEachItemPlays()
    {
        // Before: the loop buffer held 1,001 items after 1,000 hand-offs.
        var queue = Queue(RepeatMode.All, "a");
        _ = Take(ref queue);

        for (var i = 0; i < 1000; i++)
        {
            queue = queue.Enqueue(Item($"x{i}"));
            Assert.Equal($"x{i}", Next(ref queue));
        }

        Assert.Single(queue.Playlist);
        Assert.Empty(queue.Queued);
        // When the pattern stops enqueueing, the player wraps to the playlist.
        Assert.Equal("^a", Next(ref queue));
    }

    [Theory]
    [InlineData(RepeatMode.Off)]
    [InlineData(RepeatMode.All)]
    public void Row06_RotationPattern_KeepsItsOrder(RepeatMode repeat)
    {
        var queue = Queue(repeat, "a", "b", "c");
        var order = new List<string> { Take(ref queue)!.Source.DisplayName };

        for (var i = 0; i < 6; i++)
        {
            queue = queue.Enqueue(Item($"x{i}"));
            order.Add(Next(ref queue));
        }

        Assert.Equal("a b c x0 x1 x2 x3", string.Join(" ", order));
    }

    [Fact]
    public void Row07_SetNextTwice_PlaysTheLaterCallFirst()
    {
        var queue = Queue(RepeatMode.Off, "a", "b");
        _ = Take(ref queue);
        queue = queue.SetNext(Item("x")).SetNext(Item("y"));

        Assert.Equal("y x b |", Advance(ref queue, 4));
    }

    [Theory]
    [InlineData(RepeatMode.Off)]
    [InlineData(RepeatMode.All)]
    [InlineData(RepeatMode.One)]
    public void Row08_AnItemThatFailsToStart_IsPassedOver(RepeatMode repeat)
    {
        // Before, under One, b was never taken: the advance after a replayed a. A cursor that
        // moved only when an item started would return b again here.
        var queue = Queue(repeat, "a", "b", "c");
        _ = Take(ref queue);
        var toB = repeat == RepeatMode.One ? PlaylistAdvance.Skip : PlaylistAdvance.EndOfStream;
        Assert.Equal("b", Next(ref queue, toB));

        Assert.Equal("c", Next(ref queue, PlaylistAdvance.FailedStart));
    }

    [Fact]
    public void Row09_JumpToAPlaylistItem_ContinuesThePlaylistFromThere()
    {
        var queue = Queue(RepeatMode.All, "a", "b", "c");
        _ = Take(ref queue);

        (queue, var result) = queue.RequestJump(queue.Playlist[2]);
        Assert.Equal(JumpRequest.Pending, result);
        Assert.Equal("c ^a b c", Advance(ref queue, 4, PlaylistAdvance.Skip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Row09_JumpToAOneShotItem_TakesItOutOfItsCollection(bool setNext)
    {
        var queue = Queue(RepeatMode.Off, "a", "b");
        _ = Take(ref queue);
        var x = Item("x");
        queue = setNext ? queue.SetNext(x) : queue.Enqueue(x);

        (queue, _) = queue.RequestJump(x);
        Assert.Equal("x b |", Advance(ref queue, 3, PlaylistAdvance.Skip));
        Assert.Empty(queue.Next);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public void Row09_JumpToTheCurrentItem_OrOneNotInThePlayer_TakesNothing()
    {
        var queue = Queue(RepeatMode.Off, "a", "b");
        var a = Take(ref queue)!;

        (queue, var toCurrent) = queue.RequestJump(a);
        (queue, var toOther) = queue.RequestJump(Item("z"));

        Assert.Equal(JumpRequest.AlreadyCurrent, toCurrent);
        Assert.Equal(JumpRequest.NotInPlayer, toOther);
        Assert.False(queue.HasPendingJump);
    }

    [Fact]
    public void Row10_ALaterJumpReplacesAnEarlierOne()
    {
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        _ = Take(ref queue);

        (queue, _) = queue.RequestJump(queue.Playlist[1]);
        (queue, _) = queue.RequestJump(queue.Playlist[2]);

        Assert.Equal("c", Next(ref queue, PlaylistAdvance.Skip));
    }

    [Fact]
    public void Row10_ClearAndRemovingTheTarget_DiscardAPendingJump()
    {
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        _ = Take(ref queue);
        var c = queue.Playlist[2];

        (queue, _) = queue.RequestJump(c);
        (queue, var removed) = queue.Remove(c);
        Assert.True(removed);
        Assert.False(queue.HasPendingJump);
        Assert.Equal("b", Next(ref queue, PlaylistAdvance.Skip));

        (queue, _) = queue.RequestJump(queue.Playlist[0]);
        queue = queue.Clear();
        Assert.False(queue.HasPendingJump);
        Assert.Null(queue.Snapshot().PendingJump);
    }

    [Fact]
    public void Row11_RemovingTheCurrentItem_ContinuesWithTheItemThatFollowedIt()
    {
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        _ = Take(ref queue);
        (queue, var d) = queue.DecideNext(PlaylistAdvance.EndOfStream);

        (queue, var removed) = queue.Remove(d.Item!);
        Assert.True(removed);
        Assert.Equal("c", Next(ref queue));
    }

    [Fact]
    public void Row12_RemovingTheCursorItemWhileAOneShotPlays_ContinuesWithItsSuccessor()
    {
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        var a = Take(ref queue)!;
        queue = queue.SetNext(Item("x"));
        Assert.Equal("x", Next(ref queue));

        (queue, var removed) = queue.Remove(a);
        Assert.True(removed);
        Assert.Equal("b", Next(ref queue));
    }

    [Fact]
    public void Row12_RemovingAnItemBeforeTheCursor_LeavesTheNextItemUnchanged()
    {
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        var a = Take(ref queue)!;
        Assert.Equal("b", Next(ref queue));

        (queue, var removed) = queue.Remove(a);
        Assert.True(removed);
        Assert.Equal(1, queue.Snapshot().ResumeIndex);
        Assert.Equal("c", Next(ref queue));
    }

    [Fact]
    public void Row13_Replace_MakesTheItemsThePlaylist_AndJumpsToTheFirst()
    {
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        _ = Take(ref queue);
        queue = queue.Enqueue(Item("x"));
        PlaylistItem[] items = [Item("d"), Item("e")];

        queue = queue.Replace(items);

        var snapshot = queue.Snapshot();
        Assert.Equal(items, snapshot.Playlist);
        Assert.Empty(snapshot.Queued);
        Assert.Same(items[0], snapshot.PendingJump);
        Assert.Equal("d e |", Advance(ref queue, 3, PlaylistAdvance.Skip));
    }

    [Fact]
    public void Row13_Replace_WithNoItems_Throws()
    {
        var queue = Queue(RepeatMode.Off, "a");
        Assert.Throws<ArgumentException>(() => queue.Replace([]));
    }

    [Fact]
    public void Row14_ClearUnderOne_ThenSkipOrNaturalEnd_EndsThePlaylist()
    {
        var queue = Queue(RepeatMode.One, "a", "b");
        _ = Take(ref queue);
        queue = queue.Clear();
        Assert.Equal("|", Next(ref queue, PlaylistAdvance.Skip));

        // A cleared current item does not repeat on its natural end either.
        var queue2 = Queue(RepeatMode.One, "a", "b");
        _ = Take(ref queue2);
        queue2 = queue2.Clear();
        Assert.Equal("|", Next(ref queue2));
    }

    [Fact]
    public void Row15_TheReplayStart_AtTheEndOfAPass_IsTheFirstItem()
    {
        // Before: nothing to take. The session reports a start with Wrapped false.
        var queue = Queue(RepeatMode.Off, "a", "b");
        Assert.Equal("a b |", Walk(ref queue, 2));
        queue = queue.WithRepeatMode(RepeatMode.All);

        (queue, var reserved) = queue.ReserveStart();
        Assert.True(reserved);
        Assert.Equal("a", Take(ref queue)!.Source.DisplayName);
        Assert.Equal(1, queue.Snapshot().ResumeIndex);
    }

    [Fact]
    public void Row15_TheReplayStart_TakesAPendingJumpFirst()
    {
        var queue = Queue(RepeatMode.Off, "a", "b", "c");
        Assert.Equal("a b c |", Walk(ref queue, 3));
        (queue, var jump) = queue.RequestJump(queue.Playlist[1]);
        Assert.Equal(JumpRequest.Pending, jump);

        (queue, var reserved) = queue.ReserveStart();
        Assert.True(reserved);
        Assert.Equal("b", Take(ref queue)!.Source.DisplayName);
    }

    [Fact]
    public void Row15_ReservingAgain_KeepsTheItemAlreadyReserved()
    {
        // A reservation that has not been taken is kept. Reserving again takes nothing more, so
        // the replay does not skip an item.
        var queue = Queue(RepeatMode.Off, "a", "b");
        Assert.Equal("a b |", Walk(ref queue, 2));

        (queue, var first) = queue.ReserveStart();
        var reservedOnce = queue;
        (queue, var second) = queue.ReserveStart();

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(reservedOnce, queue);
        Assert.Equal("a b |", Walk(ref queue, 2));
    }

    [Fact]
    public void Row15_WithNothingInThePlayer_ThereIsNoReplayStart()
    {
        var queue = Queue(RepeatMode.Off, "a");
        _ = Take(ref queue);
        queue = queue.Clear();

        (queue, var reserved) = queue.ReserveStart();
        Assert.False(reserved);
        Assert.Null(Take(ref queue));
    }

    [Fact]
    public void Row16_AReservedStart_SurvivesAClear()
    {
        var queue = Queue(RepeatMode.Off, "a");
        Assert.Equal("a |", Walk(ref queue, 1));

        (queue, var reserved) = queue.ReserveStart();
        Assert.True(reserved);
        queue = queue.Clear();

        Assert.Equal("a", Take(ref queue)!.Source.DisplayName);
        Assert.Null(Take(ref queue));
    }

    [Fact]
    public void Row16_AReplaceAfterTheReplaysTake_StartsTheReplayOnTheNewPlaylist()
    {
        // A replace names what should play, so the replay starts on its first item rather than
        // opening the item it had reserved and then jumping away from it.
        var queue = Queue(RepeatMode.Off, "a");
        Assert.Equal("a |", Walk(ref queue, 1));

        (queue, var reserved) = queue.ReserveStart();
        Assert.True(reserved);
        PlaylistItem[] items = [Item("d"), Item("e")];
        queue = queue.Replace(items);

        Assert.Same(items[0], Take(ref queue));
        Assert.False(queue.HasPendingJump);
        Assert.Equal("e |", Advance(ref queue, 2));
    }

    [Fact]
    public void Row17_TheFailureCount_IsPartOfTheValue()
    {
        var queue = Queue(RepeatMode.All, "a");

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
        {
            (queue, var giveUp) = queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero);
            Assert.False(giveUp);
        }

        Assert.Equal(PlaylistFailureGuard.MaxConsecutiveFailures, queue.ConsecutiveFailures);
        Assert.True(queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero).GiveUp);
    }

    [Fact]
    public void Row18_ATakenItem_IsCurrentBeforeItStarts()
    {
        var queue = Queue(RepeatMode.Off, "a", "b");
        var a = Take(ref queue)!;

        var opening = queue.Snapshot();
        Assert.Same(a, opening.Current);
        Assert.False(opening.CurrentStarted);
        Assert.Null(queue.Reported);

        // It can be named while it opens: a jump to it does nothing, and it can be removed.
        (queue, var toOpening) = queue.RequestJump(a);
        Assert.Equal(JumpRequest.AlreadyCurrent, toOpening);
        (queue, var removed) = queue.Remove(a);
        Assert.True(removed);

        // Once removed, it is no longer in the player, so a jump to it is refused.
        (queue, var toRemoved) = queue.RequestJump(a);
        Assert.Equal(JumpRequest.NotInPlayer, toRemoved);

        (queue, _) = queue.ReportCurrent(a, Info());
        var started = queue.Snapshot();
        Assert.Same(a, started.Current);
        Assert.True(started.CurrentStarted);
        Assert.Same(a, queue.Reported);

        // Removed while opening, it does not stop, and the playlist continues after it.
        Assert.Equal("b", Next(ref queue));

        // A one-shot item leaves its collection when it is taken, so while it opens it can be
        // named only as the current item.
        var x = Item("x");
        queue = queue.Enqueue(x);
        Assert.Equal("x", Next(ref queue));
        Assert.Empty(queue.Queued);
        (queue, var toOneShot) = queue.RequestJump(x);
        Assert.Equal(JumpRequest.AlreadyCurrent, toOneShot);
        (queue, removed) = queue.Remove(x);
        Assert.True(removed);
        (_, removed) = queue.Remove(x);
        Assert.False(removed);
    }
}
