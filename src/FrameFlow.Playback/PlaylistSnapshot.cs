// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
    internal PlaylistSnapshot(
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
}
