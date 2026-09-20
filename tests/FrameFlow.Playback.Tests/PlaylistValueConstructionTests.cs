using System.Collections.Immutable;
using FrameFlow.Media;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The public constructors on <see cref="PlaylistItem"/>, <see cref="PlaylistSnapshot"/> and
/// <see cref="PlaylistTransition"/>: what a consumer can build, and what the snapshot refuses.
/// </summary>
/// <remarks>
/// These types are returned by <c>IMediaPlaylistPlayer</c>, a public interface. Until #318 their
/// constructors were internal, so the interface could be implemented but not satisfied: no test
/// double or decorator could produce the values its own members return. Pure values, no session.
/// </remarks>
public sealed class PlaylistValueConstructionTests
{
    private sealed record FakeSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    private static FakeSource S(string name) => new(name);

    private static PlaylistItem Item(string name) => new(S(name));

    private static MediaInfo Info() => new("test", TimeSpan.FromSeconds(3), [], []);

    // ── PlaylistItem ────────────────────────────────────────────────────────

    [Fact]
    public void Item_CarriesItsSource()
    {
        var source = S("a");

        Assert.Same(source, new PlaylistItem(source).Source);
    }

    [Fact]
    public void Item_NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PlaylistItem(null!));
    }

    [Fact]
    public void Item_SameSourceTwice_AreDifferentItems()
    {
        // Reference identity is the whole point: a caller jumps to or removes one of them.
        var source = S("a");

        Assert.NotEqual(new PlaylistItem(source), new PlaylistItem(source));
    }

    [Fact]
    public void Item_BuiltOutsideAPlayer_IsNotInAQueueThatHoldsItsSource()
    {
        // The refusal a consumer-built item gets from JumpToAsync / RemoveAsync, at the value
        // layer that decides it.
        var source = S("a");
        var queue = PlaylistQueue.Create([new PlaylistItem(source)], RepeatMode.Off);

        var foreign = new PlaylistItem(source);

        Assert.False(queue.InPlayer(foreign));
        Assert.Equal(JumpRequest.NotInPlayer, queue.RequestJump(foreign).Result);
        Assert.False(queue.Remove(foreign).Removed);
    }

    // ── PlaylistSnapshot ────────────────────────────────────────────────────

    private static PlaylistSnapshot Snapshot(
        IReadOnlyList<PlaylistItem>? playlist = null,
        IReadOnlyList<PlaylistItem>? next = null,
        IReadOnlyList<PlaylistItem>? queued = null,
        PlaylistItem? current = null,
        bool currentStarted = false,
        int resumeIndex = 0,
        PlaylistItem? pendingJump = null,
        long revision = 0
    ) =>
        new(
            playlist ?? [],
            next ?? [],
            queued ?? [],
            current,
            currentStarted,
            resumeIndex,
            pendingJump,
            revision
        );

    [Fact]
    public void Snapshot_RoundTripsItsParts()
    {
        var a = Item("a");
        var b = Item("b");

        var snapshot = Snapshot(
            playlist: [a, b],
            current: a,
            currentStarted: true,
            resumeIndex: 1,
            pendingJump: b,
            revision: 7
        );

        Assert.Equal([a, b], snapshot.Playlist);
        Assert.Same(a, snapshot.Current);
        Assert.True(snapshot.CurrentStarted);
        Assert.Equal(1, snapshot.ResumeIndex);
        Assert.Same(b, snapshot.PendingJump);
        Assert.Equal(7, snapshot.Revision);
    }

    [Fact]
    public void Snapshot_ResumeIndexEqualToPlaylistCount_IsTheEndOfAPass()
    {
        var snapshot = Snapshot(playlist: [Item("a")], resumeIndex: 1);

        Assert.Equal(1, snapshot.ResumeIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Snapshot_ResumeIndexOutsideThePlaylist_Throws(int resumeIndex)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Snapshot(playlist: [Item("a")], resumeIndex: resumeIndex)
        );

        Assert.Equal("resumeIndex", ex.ParamName);
    }

    [Fact]
    public void Snapshot_PendingJumpTheQueueDoesNotHold_Throws()
    {
        // The queue only latches a jump to an item it holds, and clears the latch when that item
        // is removed, so this describes a queue no player could be in.
        var ex = Assert.Throws<ArgumentException>(() =>
            Snapshot(playlist: [Item("a")], pendingJump: Item("b"))
        );

        Assert.Equal("pendingJump", ex.ParamName);
    }

    [Fact]
    public void Snapshot_PendingJumpInNextOrQueued_IsAccepted()
    {
        var inNext = Item("n");
        var inQueued = Item("q");

        Assert.Same(inNext, Snapshot(next: [inNext], pendingJump: inNext).PendingJump);
        Assert.Same(inQueued, Snapshot(queued: [inQueued], pendingJump: inQueued).PendingJump);
    }

    [Fact]
    public void Snapshot_NullItemInACollection_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => Snapshot(playlist: [null!]));

        Assert.Equal("playlist", ex.ParamName);
    }

    [Fact]
    public void Snapshot_NullCollection_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PlaylistSnapshot(null!, [], [], null, false, 0, null, 0)
        );
    }

    [Fact]
    public void Snapshot_NegativeRevision_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(revision: -1));

        Assert.Equal("revision", ex.ParamName);
    }

    [Fact]
    public void Snapshot_MutableListPassedIn_DoesNotChangeUnderTheSnapshot()
    {
        // IReadOnlyList is a view, not a guarantee. A snapshot whose contents move is not one.
        var a = Item("a");
        var backing = new List<PlaylistItem> { a };

        var snapshot = Snapshot(playlist: backing, resumeIndex: 1);

        backing.Clear();
        backing.Add(Item("swapped"));

        Assert.Equal([a], snapshot.Playlist);
        Assert.Equal(1, snapshot.ResumeIndex);
    }

    [Fact]
    public void Snapshot_MutableListEmptiedAfterwards_CannotInvalidateResumeIndex()
    {
        var backing = new List<PlaylistItem> { Item("a"), Item("b") };
        var snapshot = Snapshot(playlist: backing, resumeIndex: 2);

        backing.Clear();

        Assert.Equal(2, snapshot.Playlist.Count);
        Assert.True(snapshot.ResumeIndex <= snapshot.Playlist.Count);
    }

    [Fact]
    public void Snapshot_ImmutableListPassedIn_IsStoredWithoutCopying()
    {
        // The queue's own collections are ImmutableList, so the player's snapshots allocate nothing.
        var items = ImmutableList.Create(Item("a"));

        Assert.Same(items, Snapshot(playlist: items).Playlist);
    }

    // ── PlaylistTransition ──────────────────────────────────────────────────

    [Fact]
    public void Transition_NamesItsItemWithoutANullCheck()
    {
        // Item was nullable only because a four-argument constructor could leave it unset (#317).
        var item = Item("a");
        var previous = Item("p");

        var transition = new PlaylistTransition(
            item,
            Info(),
            Index: 2,
            Wrapped: false,
            previous,
            PlaylistTransitionReason.EndOfItem
        );

        Assert.Same(item, transition.Item);
        Assert.Same(item.Source, transition.Item.Source);
        Assert.Same(previous, transition.Previous);
        Assert.Equal(PlaylistTransitionReason.EndOfItem, transition.Reason);
    }

    [Fact]
    public void Transition_NullItem_Throws()
    {
        // A non-nullable reference type is a compile-time claim; the constructor still has to hold
        // the line for a nullable-oblivious caller.
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new PlaylistTransition(
                null!,
                Info(),
                Index: 0,
                Wrapped: false,
                Previous: null,
                PlaylistTransitionReason.FirstItem
            )
        );

        Assert.Equal("Item", ex.ParamName);
    }

    [Fact]
    public void Transition_NullMediaInfo_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new PlaylistTransition(
                Item("a"),
                null!,
                Index: 0,
                Wrapped: false,
                Previous: null,
                PlaylistTransitionReason.FirstItem
            )
        );

        Assert.Equal("MediaInfo", ex.ParamName);
    }

    [Fact]
    public void Transition_FirstItemHasNoPrevious()
    {
        var transition = new PlaylistTransition(
            Item("a"),
            Info(),
            Index: 0,
            Wrapped: false,
            Previous: null,
            PlaylistTransitionReason.FirstItem
        );

        Assert.Null(transition.Previous);
    }
}
