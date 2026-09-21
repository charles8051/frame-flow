// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace FrameFlow.Playback;

/// <summary>
/// A copy of a playlist player's queue, taken at one moment.
/// </summary>
/// <remarks>
/// <para>
/// The player takes the next item from these collections in a fixed order: a pending jump's
/// target, then the first <see cref="Next"/> item, then the <see cref="Playlist"/> item at
/// <see cref="ResumeIndex"/>, then the first <see cref="Queued"/> item. At the end of a pass
/// it goes back to the first playlist item under <c>RepeatMode.All</c>, and otherwise ends.
/// </para>
/// <para>
/// Poll it as you would diagnostics. <see cref="Revision"/> rises on every edit and every
/// hand-off, so a snapshot whose revision has not changed can be skipped.
/// </para>
/// </remarks>
public sealed class PlaylistSnapshot
{
    /// <summary>
    /// Builds a snapshot, refusing one whose parts contradict each other.
    /// </summary>
    /// <remarks>
    /// Public so a caller can build one for a test double or a decorator over
    /// <c>IMediaPlayer</c> (#318). The player's own snapshots always satisfy these
    /// checks; they exist so a hand-built one cannot quietly describe a queue no player could
    /// be in.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A collection holds a <see langword="null"/> item, <paramref name="resumeIndex"/> falls
    /// outside <paramref name="playlist"/>, <paramref name="pendingJump"/> names an item that is
    /// in none of the three collections, or <paramref name="revision"/> is negative.
    /// </exception>
    public PlaylistSnapshot(
        IReadOnlyList<PlaylistItem> playlist,
        IReadOnlyList<PlaylistItem> next,
        IReadOnlyList<PlaylistItem> queued,
        PlaylistItem? current,
        bool currentStarted,
        int resumeIndex,
        PlaylistItem? pendingJump,
        long revision
    )
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(queued);

        // Freeze before validating, so what is checked is what is stored. An IReadOnlyList can be
        // a List the caller still holds, and a snapshot whose contents move is not a snapshot.
        playlist = Freeze(playlist);
        next = Freeze(next);
        queued = Freeze(queued);

        RejectNullItems(playlist, nameof(playlist));
        RejectNullItems(next, nameof(next));
        RejectNullItems(queued, nameof(queued));

        // Cursor equals the playlist's count at the end of a pass, so the count itself is in range.
        if (resumeIndex < 0 || resumeIndex > playlist.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resumeIndex),
                resumeIndex,
                $"ResumeIndex must be between 0 and the playlist's count ({playlist.Count}); it is "
                    + "where the playlist continues, and the count means the pass has ended."
            );
        }

        // The queue only ever latches a jump to an item it holds, and clears the latch when that
        // item is removed, so a pending jump to something absent is a queue that cannot exist.
        if (
            pendingJump is not null
            && !playlist.Contains(pendingJump)
            && !next.Contains(pendingJump)
            && !queued.Contains(pendingJump)
        )
        {
            throw new ArgumentException(
                "PendingJump names an item that is in none of playlist, next or queued. A jump is "
                    + "only latched to an item the player holds.",
                nameof(pendingJump)
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        Playlist = playlist;
        Next = next;
        Queued = queued;
        Current = current;
        CurrentStarted = currentStarted;
        ResumeIndex = resumeIndex;
        PendingJump = pendingJump;
        Revision = revision;
    }

    /// <summary>
    /// The items the player keeps, in the order they were added. Under <c>RepeatMode.All</c>
    /// the player loops over them.
    /// </summary>
    public IReadOnlyList<PlaylistItem> Playlist { get; }

    /// <summary>
    /// Items added by <c>SetNextAsync</c>, in the order they will play. Each plays once.
    /// </summary>
    public IReadOnlyList<PlaylistItem> Next { get; }

    /// <summary>
    /// Items added by <c>EnqueueAsync</c>, in the order they will play. Each plays once, after
    /// the playlist items left in the current pass.
    /// </summary>
    public IReadOnlyList<PlaylistItem> Queued { get; }

    /// <summary>
    /// The item most recently taken, or <see langword="null"/> before the first. It may be a
    /// one-shot item, and so not in <see cref="Playlist"/>, an item that has been removed, or an
    /// item still opening.
    /// </summary>
    public PlaylistItem? Current { get; }

    /// <summary>
    /// Whether <see cref="Current"/> has started. <see langword="false"/> while it is opening, except
    /// while an advance repeats the same item, which keeps it started.
    /// </summary>
    public bool CurrentStarted { get; }

    /// <summary>
    /// The index in <see cref="Playlist"/> of the item the playlist continues with.
    /// Equal to the playlist's count at the end of a pass.
    /// </summary>
    public int ResumeIndex { get; }

    /// <summary>The target of a jump that has not been taken yet, if any.</summary>
    public PlaylistItem? PendingJump { get; }

    /// <summary>Rises on every edit to the queue and every hand-off.</summary>
    public long Revision { get; }

    /// <summary>
    /// Returns <paramref name="items"/> when it cannot change under the snapshot, and a copy
    /// otherwise. The queue's own collections are <see cref="ImmutableList{T}"/>, so the player's
    /// snapshots take the first branch and allocate nothing.
    /// </summary>
    private static IReadOnlyList<PlaylistItem> Freeze(IReadOnlyList<PlaylistItem> items) =>
        items is ImmutableList<PlaylistItem> or ImmutableArray<PlaylistItem>
            ? items
            : [.. items];

    private static void RejectNullItems(IReadOnlyList<PlaylistItem> items, string name)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null)
                throw new ArgumentException($"{name}[{i}] is null.", name);
        }
    }
}
