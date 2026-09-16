// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// The playlist player's queue as an immutable value. Each rule of the queue is an operation that
/// returns a new value, with a result where the caller needs one, and leaves this one unchanged.
/// It owns no lock, no clock and no IO. <see cref="PlaylistCoordinator"/> holds the current value
/// and applies operations to it under its lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model</b> (<i>The playlist player's queue</i>, #171). The queue keeps a playlist of items
/// that stay after they play, a cursor into it, next items and queued items. Next and queued items
/// are one-shot: each leaves its collection when it is taken and never joins the playlist. The
/// cursor is a gap: taking a playlist item puts it just after that item, and removing an item keeps
/// it between the items on either side.
/// </para>
/// <para>
/// <b>The order.</b> An advance takes the pending jump's target, then the first next item, then the
/// playlist item after the cursor, then the first queued item. At the end of a pass it goes back to
/// the first playlist item under <see cref="RepeatMode.All"/>, or on a skip or failed start under
/// <see cref="RepeatMode.One"/>, and otherwise ends. Under <see cref="RepeatMode.One"/> a natural
/// end or a fault replays the current item. The repeat mode is not stored in the collections, so a
/// switch applies to the whole playlist.
/// </para>
/// <para>
/// <b>The revision</b> rises on edits and hand-offs: an add, an enqueue, a next item, a recorded
/// jump, a removal, a clear, a replace, a take and a start. A repeat-mode change, a latch and the
/// failure count leave it alone.
/// </para>
/// <para>
/// <b>Equality</b> is structural. Two values are equal when their collections hold the same items
/// in the same order and every other member is equal. Items compare by reference.
/// </para>
/// </remarks>
internal sealed record PlaylistQueue
{
    private PlaylistQueue() { }

    /// <summary>The items that stay after they play, in the order they were added.</summary>
    public ImmutableList<PlaylistItem> Playlist { get; private init; } = [];

    /// <summary>
    /// The number of playlist items before the cursor. The playlist continues with the item at this
    /// index.
    /// </summary>
    public int Cursor { get; private init; }

    /// <summary>One-shot items that play next, first to play first.</summary>
    public ImmutableList<PlaylistItem> Next { get; private init; } = [];

    /// <summary>One-shot items that play after the playlist items left in the pass, in order.</summary>
    public ImmutableList<PlaylistItem> Queued { get; private init; } = [];

    /// <summary>The target of a jump that has not been taken.</summary>
    public PlaylistItem? PendingJump { get; private init; }

    /// <summary>The item most recently taken.</summary>
    public PlaylistItem? Current { get; private init; }

    /// <summary>Whether <see cref="Current"/> has started.</summary>
    public bool CurrentStarted { get; private init; }

    /// <summary>Whether <see cref="Current"/> was removed while it was current.</summary>
    public bool CurrentRemoved { get; private init; }

    /// <summary>
    /// The item taken for a replay from <c>Ended</c> before the controller unloads, handed to the
    /// next session's first take.
    /// </summary>
    public PlaylistItem? ReservedStart { get; private init; }

    /// <summary>The item most recently reported as started.</summary>
    public PlaylistItem? Reported { get; private init; }

    /// <summary>Metadata for <see cref="Reported"/>.</summary>
    public MediaInfo? ReportedInfo { get; private init; }

    /// <summary>The duration of <see cref="Reported"/>, or <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan ReportedDuration => ReportedInfo?.Duration ?? TimeSpan.Zero;

    /// <summary>The repeat mode.</summary>
    public RepeatMode Repeat { get; private init; }

    /// <summary>Items that failed in a row without making progress. See <see cref="ItemFailed"/>.</summary>
    public int ConsecutiveFailures { get; private init; }

    /// <summary>A skip or end-of-stream latched before a session could act on it.</summary>
    public PlaylistAdvance? LatchedAdvance { get; private init; }

    /// <summary>Rises on every edit and hand-off.</summary>
    public long Revision { get; private init; }

    /// <summary>The index of the last reported start, or -1 before the first.</summary>
    public int TransitionCount { get; private init; } = -1;

    /// <summary>Whether any item has started.</summary>
    public bool AnyStarted => TransitionCount >= 0;

    /// <summary>Whether a jump is waiting to be taken.</summary>
    public bool HasPendingJump => PendingJump is not null;

    /// <summary>
    /// Whether the current item is expected to repeat at its end, which is when the loop-stall
    /// watchdog watches it. The item has started and has not been removed, no jump is pending, and
    /// either the mode is <see cref="RepeatMode.One"/>, or the mode is <see cref="RepeatMode.All"/>
    /// and the item is the only playlist item with nothing set next or queued.
    /// </summary>
    /// <remarks>
    /// Decision 6 of <c>docs/adr/ADR-0075-looping-on-both-players.md</c>. A repeat of the same item keeps it
    /// started, so a repeat that hangs is still watched. A pending jump is taken by the next advance
    /// ahead of any repeat.
    /// </remarks>
    public bool ExpectsRepeat =>
        Current is { } current
        && CurrentStarted
        && !CurrentRemoved
        && PendingJump is null
        && (
            Repeat == RepeatMode.One
            || (
                Repeat == RepeatMode.All
                && Playlist.Count == 1
                && ReferenceEquals(Playlist[0], current)
                && Next.IsEmpty
                && Queued.IsEmpty
            )
        );

    /// <summary>A queue with <paramref name="playlist"/> as its playlist, before any item is taken.</summary>
    public static PlaylistQueue Create(IEnumerable<PlaylistItem> playlist, RepeatMode repeat)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return new PlaylistQueue { Playlist = [.. playlist], Repeat = repeat };
    }

    /// <summary>What an advance decided.</summary>
    internal enum NextKind
    {
        /// <summary>Move to a different source.</summary>
        Advance,

        /// <summary>
        /// The next item plays the same source object as the current one: replay it, reusing the
        /// live runtime where the session can.
        /// </summary>
        Replay,

        /// <summary>Nothing is left to take. The playlist is finished.</summary>
        End,
    }

    /// <summary>The item an advance took, and whether taking it wrapped the playlist.</summary>
    internal readonly record struct NextDecision(NextKind Kind, PlaylistItem? Item, bool Wrapped);

    // ── Edits ───────────────────────────────────────────────────────────────

    /// <summary>Sets the repeat mode.</summary>
    public PlaylistQueue WithRepeatMode(RepeatMode repeat) => this with { Repeat = repeat };

    /// <summary>Appends <paramref name="item"/> to the playlist.</summary>
    public PlaylistQueue Add(PlaylistItem item) =>
        this with
        {
            Playlist = Playlist.Add(item),
            Revision = Revision + 1,
        };

    /// <summary>Adds a one-shot item after the queued items.</summary>
    public PlaylistQueue Enqueue(PlaylistItem item) =>
        this with
        {
            Queued = Queued.Add(item),
            Revision = Revision + 1,
        };

    /// <summary>Adds a one-shot item ahead of the next items already there.</summary>
    public PlaylistQueue SetNext(PlaylistItem item) =>
        this with
        {
            Next = Next.Insert(0, item),
            Revision = Revision + 1,
        };

    /// <summary>Records <paramref name="item"/> as the pending jump, replacing any earlier one.</summary>
    public (PlaylistQueue Queue, JumpRequest Result) RequestJump(PlaylistItem item)
    {
        // A current item that has been removed is no longer in the player, and is refused like any
        // other item that is not.
        if (ReferenceEquals(item, Current) && !CurrentRemoved)
            return (this, JumpRequest.AlreadyCurrent);
        if (!InPlayer(item))
            return (this, JumpRequest.NotInPlayer);
        return (this with { PendingJump = item, Revision = Revision + 1 }, JumpRequest.Pending);
    }

    /// <summary>
    /// Removes a playlist, next or queued item, or marks the current item removed. The current item
    /// is never stopped. The result is <see langword="false"/> when the item is not in the player.
    /// </summary>
    public (PlaylistQueue Queue, bool Removed) Remove(PlaylistItem item)
    {
        var playlist = Playlist;
        var cursor = Cursor;
        var next = Next;
        var queued = Queued;
        var removed = false;

        var index = playlist.IndexOf(item);
        if (index >= 0)
        {
            playlist = playlist.RemoveAt(index);
            if (index < cursor)
                cursor--;
            removed = true;
        }
        else if (next.Contains(item))
        {
            next = next.Remove(item);
            removed = true;
        }
        else if (queued.Contains(item))
        {
            queued = queued.Remove(item);
            removed = true;
        }

        var currentRemoved = CurrentRemoved;
        if (ReferenceEquals(item, Current) && !CurrentRemoved)
        {
            currentRemoved = true;
            removed = true;
        }

        if (!removed)
            return (this, false);

        return (
            this with
            {
                Playlist = playlist,
                Cursor = cursor,
                Next = next,
                Queued = queued,
                CurrentRemoved = currentRemoved,
                PendingJump = ReferenceEquals(item, PendingJump) ? null : PendingJump,
                Revision = Revision + 1,
            },
            true
        );
    }

    /// <summary>
    /// Removes every playlist, next and queued item, discards a pending jump, and marks the current
    /// item removed. The current item plays on, and a reserved start is kept.
    /// </summary>
    public PlaylistQueue Clear() =>
        this with
        {
            Playlist = [],
            Next = [],
            Queued = [],
            PendingJump = null,
            Cursor = 0,
            CurrentRemoved = CurrentRemoved || Current is not null,
            Revision = Revision + 1,
        };

    /// <summary>
    /// Clears the queue, makes <paramref name="items"/> the playlist and the first of them the
    /// pending jump, in one edit.
    /// </summary>
    public PlaylistQueue Replace(IReadOnlyList<PlaylistItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new ArgumentException(
                "A replacement playlist requires at least one item.",
                nameof(items)
            );

        var cleared = Clear();
        return cleared with
        {
            // A replay from Ended that has taken its item but not yet started a session starts on
            // the new playlist instead. A clear keeps the reservation, because without one the
            // replay would have nothing to open; a replace gives it the jump target.
            ReservedStart = null,
            Playlist = [.. items],
            PendingJump = items[0],
            Revision = cleared.Revision + 1,
        };
    }

    // ── Takes and starts ────────────────────────────────────────────────────

    /// <summary>
    /// Takes the item a replay from <c>Ended</c> will start with, before the controller unloads.
    /// The result is <see langword="false"/> when the player holds nothing to take.
    /// </summary>
    public (PlaylistQueue Queue, bool Reserved) ReserveStart()
    {
        if (ReservedStart is not null)
            return (this, true);
        var (queue, item) = TakeFirst();
        return item is null ? (this, false) : (queue with { ReservedStart = item }, true);
    }

    /// <summary>
    /// Takes the item a new session starts with: a reserved item if there is one; otherwise the
    /// pending jump's target, the first next item, the playlist item after the cursor or the first
    /// queued item; otherwise the first playlist item again. The item is <see langword="null"/>
    /// when the player holds nothing.
    /// </summary>
    public (PlaylistQueue Queue, PlaylistItem? Item) TakeStart() =>
        ReservedStart is { } reserved ? (this with { ReservedStart = null }, reserved) : TakeFirst();

    /// <summary>Decides what plays after the current item, and takes it.</summary>
    /// <param name="reason">
    /// Why the advance happened. Under <see cref="RepeatMode.One"/> a natural end or a fault replays
    /// the current item, and a skip or failed start moves on.
    /// </param>
    /// <remarks>
    /// The decision is <see cref="NextKind.Replay"/> whenever the item taken plays the <i>same
    /// source object</i> as the current one: under <see cref="RepeatMode.One"/>, when a
    /// <see cref="RepeatMode.All"/> playlist of one source wraps, or at a back-to-back duplicate.
    /// That lets the session reuse the live decode runtime in place, which is what makes the
    /// single-clip loop seam gapless. <see cref="NextDecision.Wrapped"/> still rides along for the
    /// transition report.
    /// </remarks>
    public (PlaylistQueue Queue, NextDecision Decision) DecideNext(PlaylistAdvance reason)
    {
        if (PendingJump is { } jump)
            return (this with { PendingJump = null }).Take(jump, wrapped: false, byAdvance: true);

        var movesOn = reason is PlaylistAdvance.Skip or PlaylistAdvance.FailedStart || CurrentRemoved;

        if (Repeat == RepeatMode.One && !movesOn && Current is { } current)
            return (this, new NextDecision(NextKind.Replay, current, Wrapped: false));

        if (!Next.IsEmpty)
            return (this with { Next = Next.RemoveAt(0) }).Take(Next[0], wrapped: false, byAdvance: true);

        if (Cursor < Playlist.Count)
            return Take(Playlist[Cursor], wrapped: false, byAdvance: true);

        if (!Queued.IsEmpty)
            return (this with { Queued = Queued.RemoveAt(0) }).Take(Queued[0], wrapped: false, byAdvance: true);

        if (
            !Playlist.IsEmpty
            && (Repeat == RepeatMode.All || (Repeat == RepeatMode.One && movesOn))
        )
        {
            return Take(Playlist[0], wrapped: true, byAdvance: true);
        }

        return (this, new NextDecision(NextKind.End, null, Wrapped: false));
    }

    /// <summary>
    /// Records that <paramref name="item"/> has started with <paramref name="info"/>. The result is
    /// the start's transition index.
    /// </summary>
    public (PlaylistQueue Queue, int Index) ReportCurrent(PlaylistItem item, MediaInfo? info)
    {
        var index = TransitionCount + 1;
        return (
            this with
            {
                CurrentStarted = CurrentStarted || ReferenceEquals(item, Current),
                Reported = item,
                ReportedInfo = info,
                TransitionCount = index,
                Revision = Revision + 1,
            },
            index
        );
    }

    // ── Latch and failures ──────────────────────────────────────────────────

    /// <summary>Latches an advance for the next play, keeping why it happened.</summary>
    public PlaylistQueue LatchAdvance(PlaylistAdvance reason) => this with { LatchedAdvance = reason };

    /// <summary>Clears a latched advance. The result is the advance that was latched, if any.</summary>
    public (PlaylistQueue Queue, PlaylistAdvance? Latched) ConsumeLatchedAdvance() =>
        (this with { LatchedAdvance = null }, LatchedAdvance);

    /// <summary>Records an item that ended without failing, which resets the failure count.</summary>
    public PlaylistQueue ItemEnded() => this with { ConsecutiveFailures = 0 };

    /// <summary>
    /// Records a failed item. The result is <see langword="true"/> when the playlist should give up.
    /// <see cref="PlaylistFailureGuard"/> describes the rule.
    /// </summary>
    /// <param name="playedFor">
    /// How far the item had played when it failed. <see cref="TimeSpan.Zero"/> for an item that
    /// never started.
    /// </param>
    /// <param name="itemLength">
    /// The item's duration, or <see cref="TimeSpan.Zero"/> when it is not known.
    /// </param>
    public (PlaylistQueue Queue, bool GiveUp) ItemFailed(TimeSpan playedFor, TimeSpan itemLength)
    {
        if (playedFor >= PlaylistFailureGuard.ProgressNeeded(itemLength))
            return (this with { ConsecutiveFailures = 0 }, false);

        var count = ConsecutiveFailures + 1;
        return (
            this with
            {
                ConsecutiveFailures = count,
            },
            count > PlaylistFailureGuard.MaxConsecutiveFailures
        );
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    /// <summary>A public copy of the queue.</summary>
    public PlaylistSnapshot Snapshot() =>
        new(Playlist, Next, Queued, Current, CurrentStarted, Cursor, PendingJump, Revision);

    /// <summary>Whether <paramref name="item"/> is a playlist, next or queued item.</summary>
    public bool InPlayer(PlaylistItem item) =>
        Playlist.Contains(item) || Next.Contains(item) || Queued.Contains(item);

    public bool Equals(PlaylistQueue? other) =>
        other is not null
        && Playlist.SequenceEqual(other.Playlist)
        && Cursor == other.Cursor
        && Next.SequenceEqual(other.Next)
        && Queued.SequenceEqual(other.Queued)
        && ReferenceEquals(PendingJump, other.PendingJump)
        && ReferenceEquals(Current, other.Current)
        && CurrentStarted == other.CurrentStarted
        && CurrentRemoved == other.CurrentRemoved
        && ReferenceEquals(ReservedStart, other.ReservedStart)
        && ReferenceEquals(Reported, other.Reported)
        && Equals(ReportedInfo, other.ReportedInfo)
        && Repeat == other.Repeat
        && ConsecutiveFailures == other.ConsecutiveFailures
        && LatchedAdvance == other.LatchedAdvance
        && Revision == other.Revision
        && TransitionCount == other.TransitionCount;

    public override int GetHashCode() =>
        HashCode.Combine(Playlist.Count, Cursor, Next.Count, Queued.Count, Current, Revision);

    // ── Private ─────────────────────────────────────────────────────────────

    private (PlaylistQueue Queue, PlaylistItem? Item) TakeFirst()
    {
        if (PendingJump is { } jump)
            return Taken((this with { PendingJump = null }).Take(jump, wrapped: false));
        if (!Next.IsEmpty)
            return Taken((this with { Next = Next.RemoveAt(0) }).Take(Next[0], wrapped: false));
        if (Cursor < Playlist.Count)
            return Taken(Take(Playlist[Cursor], wrapped: false));
        if (!Queued.IsEmpty)
            return Taken((this with { Queued = Queued.RemoveAt(0) }).Take(Queued[0], wrapped: false));
        return Playlist.IsEmpty ? (this, null) : Taken(Take(Playlist[0], wrapped: false));
    }

    private static (PlaylistQueue Queue, PlaylistItem? Item) Taken(
        (PlaylistQueue Queue, NextDecision Decision) take
    ) => (take.Queue, take.Decision.Item);

    // Makes item current. A playlist item moves the cursor to just after it; a one-shot item leaves
    // its collection. An advance that takes the current item again repeats it, and the repeat keeps
    // it started, so the loop-stall watchdog still watches it (ExpectsRepeat). A new session's first
    // take has not started anything yet.
    private (PlaylistQueue Queue, NextDecision Decision) Take(PlaylistItem item, bool wrapped, bool byAdvance = false)
    {
        var queue = this;
        var index = Playlist.IndexOf(item);
        if (index >= 0)
            queue = queue with { Cursor = index + 1 };
        else if (Next.Contains(item))
            queue = queue with { Next = Next.Remove(item) };
        else if (Queued.Contains(item))
            queue = queue with { Queued = Queued.Remove(item) };

        var kind =
            Current is not null && ReferenceEquals(Current.Source, item.Source)
                ? NextKind.Replay
                : NextKind.Advance;

        return (
            queue with
            {
                Current = item,
                CurrentStarted = byAdvance && CurrentStarted && !CurrentRemoved && ReferenceEquals(item, Current),
                CurrentRemoved = false,
                Revision = Revision + 1,
            },
            new NextDecision(kind, item, wrapped)
        );
    }
}
