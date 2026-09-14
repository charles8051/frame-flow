// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Player;

/// <summary>
/// A player that presents an ordered, optionally looping sequence of sources
/// through ONE warm video sink and ONE warm audio sink. Item boundaries swap
/// only the decode source; the presenter (sink + GPU resources) and the playback
/// clock stay warm across the whole playlist, so the per-item present-pipeline
/// rebuild a naive consumer pays today is eliminated.
/// </summary>
/// <remarks>
/// <para>
/// The inherited <see cref="IMediaPlayer"/> transport acts on the <b>current</b>
/// item: <see cref="IMediaPlayer.PlayAsync"/> / <see cref="IMediaPlayer.PauseAsync"/>
/// pause and resume it, <see cref="IMediaPlayer.SeekAsync"/> seeks within its
/// timeline, and <see cref="IMediaPlayer.Position"/> /
/// <see cref="IMediaPlayer.Duration"/> / <see cref="IMediaPlayer.MediaInfo"/>
/// reflect it (and update on <see cref="SourceTransitioned"/>).
/// </para>
/// <para>
/// <b>The queue.</b> The player keeps a playlist of items that stay after they play:
/// the sources it was created with, then those added with <see cref="AddAsync"/>. Items
/// from <see cref="SetNextAsync"/> and <see cref="EnqueueAsync"/> play once and never join
/// the playlist. When an item ends or is skipped, the player takes the target of a pending
/// <see cref="JumpToAsync"/>, then the most recent <see cref="SetNextAsync"/> item, then the
/// next playlist item, then the earliest <see cref="EnqueueAsync"/> item. At the end of a
/// pass, <see cref="RepeatMode.All"/> goes back to the first playlist item and
/// <see cref="RepeatMode.Off"/> ends. <see cref="RepeatMode.One"/> repeats the current item
/// until a skip or jump moves on. <see cref="GetPlaylist"/> returns a snapshot of all of it.
/// </para>
/// <para>
/// For continuous rotation under <see cref="RepeatMode.Off"/>, enqueue the next item from a
/// <see cref="SourceTransitioned"/> handler with <see cref="EnqueueAsync"/>. Its items leave
/// once they have played, so the player holds nothing after each one. Items added with
/// <see cref="AddAsync"/> are kept for the life of the player.
/// </para>
/// <para>
/// <b>At the end of the queue.</b> The last item stays loaded at
/// <see cref="PlaybackState.Ended"/> if it played to its end or was skipped. Its diagnostics
/// can be read there, and <see cref="IMediaPlayer.SeekAsync"/> pauses on it at the position
/// sought, so a following <see cref="IMediaPlayer.PlayAsync"/> plays it from there.
/// <see cref="IMediaPlayer.PlayAsync"/> from <see cref="PlaybackState.Ended"/> plays the target
/// of a pending jump, or the next item the queue yields, or else starts the playlist again from
/// its first item. With nothing to play it returns a failed <see cref="Result"/> with
/// <see cref="ErrorCategory.InvalidOperation"/>, and the player stays in
/// <see cref="PlaybackState.Ended"/>. If the last item failed, nothing stays loaded, and a seek
/// from <see cref="PlaybackState.Ended"/> is refused the same way.
/// </para>
/// <para>
/// <b>Failed items.</b> An item that faults while it plays, or a later item that
/// cannot be started, raises <see cref="IMediaPlayer.ErrorOccurred"/> and is skipped
/// as though it had ended. The state does not change: under
/// <see cref="RepeatMode.Off"/> a last item that faults while playing ends the
/// playlist in <see cref="PlaybackState.Ended"/>, and under
/// <see cref="RepeatMode.All"/> or <see cref="RepeatMode.One"/> the rotation
/// continues. An item that cannot be started is passed over under every repeat mode. The
/// player's very first item is treated as a single source's: if it cannot be opened the load
/// fails, and if it faults before the first <see cref="IMediaPlayer.PlayAsync"/> the player
/// enters <see cref="PlaybackState.Error"/>. The player gives up and enters <see cref="PlaybackState.Error"/>
/// when more than eight items fail in a row. An item that ends or is skipped without
/// failing breaks the run, and so does a fault after an item has played for five
/// seconds, or for half its length if that is shorter.
/// </para>
/// </remarks>
public interface IMediaPlaylistPlayer : IMediaPlayer
{
    /// <summary>The source of the item that most recently started, or <see langword="null"/> before the first.</summary>
    IMediaSource? CurrentSource { get; }

    /// <summary>
    /// Adds a source that plays once, after the playlist items left in the current pass.
    /// </summary>
    /// <returns>The item added, for <see cref="JumpToAsync"/> or <see cref="RemoveAsync"/>.</returns>
    Task<PlaylistItem> EnqueueAsync(IMediaSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="source"/> the very next item to play, ahead of anything already
    /// set next or queued. It plays once. A <see langword="null"/> value adds nothing.
    /// </summary>
    /// <returns>The item added, or <see langword="null"/> when <paramref name="source"/> is null.</returns>
    Task<PlaylistItem?> SetNextAsync(IMediaSource? source, CancellationToken cancellationToken = default);

    /// <summary>Appends a source to the playlist, where it stays after it plays.</summary>
    /// <returns>The item added.</returns>
    Task<PlaylistItem> AddAsync(IMediaSource source, CancellationToken cancellationToken = default);

    /// <summary>Returns a snapshot of the queue.</summary>
    PlaylistSnapshot GetPlaylist();

    /// <summary>Makes <paramref name="item"/> the current item.</summary>
    /// <remarks>
    /// <para>
    /// The jump follows <see cref="IMediaPlayer.State"/> as a skip does. While
    /// <see cref="PlaybackState.Playing"/> the item plays; while
    /// <see cref="PlaybackState.Paused"/> it becomes current and stays paused. Before the first
    /// play, and at <see cref="PlaybackState.Ended"/>, it takes effect at the next
    /// <see cref="IMediaPlayer.PlayAsync"/>. A jump to a playlist item continues the playlist
    /// from there. A later jump replaces one not yet taken.
    /// </para>
    /// <para>
    /// A jump to the current item does nothing and succeeds; seek to zero to restart it. A jump
    /// to an item that is not in the player, or while the player is in
    /// <see cref="PlaybackState.Error"/> or disposed, returns a failed <see cref="Result"/> with
    /// <see cref="ErrorCategory.InvalidOperation"/>. The call returns once the jump is recorded;
    /// <see cref="SourceTransitioned"/> fires when the item starts.
    /// </para>
    /// </remarks>
    Task<Result> JumpToAsync(PlaylistItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="item"/> from the queue. The current item is never stopped:
    /// removing it lets it play on, and when it ends the player takes the next item as usual,
    /// without repeating it under <see cref="RepeatMode.One"/>.
    /// </summary>
    /// <returns>
    /// A failed <see cref="Result"/> with <see cref="ErrorCategory.InvalidOperation"/> when the
    /// item is not in the player.
    /// </returns>
    Task<Result> RemoveAsync(PlaylistItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every item and any pending jump. The current item plays on, and the playlist
    /// ends when it does unless items are added first.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the queue with <paramref name="sources"/> in one edit and jumps to the first of
    /// them, so a playing player moves to the new playlist at once.
    /// </summary>
    /// <returns>
    /// The new playlist items, or a failed <see cref="Result{T}"/> with
    /// <see cref="ErrorCategory.InvalidOperation"/> while the player is in
    /// <see cref="PlaybackState.Error"/> or disposed.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="sources"/> is empty.</exception>
    Task<Result<IReadOnlyList<PlaylistItem>>> ReplaceAsync(
        IEnumerable<IMediaSource> sources,
        CancellationToken cancellationToken = default
    );

    /// <summary>End the current item now and hand off to the next (no presenter rebuild).</summary>
    /// <remarks>
    /// <para>
    /// The skip follows <see cref="IMediaPlayer.State"/>. While
    /// <see cref="PlaybackState.Playing"/>, the next item plays. While
    /// <see cref="PlaybackState.Paused"/>, the next item becomes current and stays paused
    /// until <see cref="IMediaPlayer.PlayAsync"/>. A skip before the playlist has first played
    /// takes effect when it does. At <see cref="PlaybackState.Ended"/> the skip does nothing;
    /// call <see cref="IMediaPlayer.PlayAsync"/> to play what is queued.
    /// </para>
    /// <para>
    /// When nothing follows the current item under <see cref="RepeatMode.Off"/>, the skip ends
    /// the playlist in <see cref="PlaybackState.Ended"/>, whether it was playing or paused.
    /// </para>
    /// <para>
    /// The call returns once the skip is requested, before the next item is current.
    /// <see cref="SourceTransitioned"/> fires when it is.
    /// </para>
    /// </remarks>
    Task SkipToNextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires once per hand-off (including the first item) when the presenter
    /// switches from one source to the next. Carries the new
    /// <see cref="CurrentSource"/> and its <see cref="MediaInfo"/> so a consumer
    /// can advance its own model and, under <see cref="RepeatMode.Off"/>, enqueue
    /// the following item to keep a rotation going. <see cref="PlaylistTransition.Item"/> is
    /// the item that started.
    /// </summary>
    IObservable<PlaylistTransition> SourceTransitioned { get; }
}
