// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Player;

/// <summary>
/// The player: an ordered, optionally looping queue of sources presented through ONE warm video
/// sink and ONE warm audio sink. Item boundaries swap only the decode source; the presenter (sink
/// + GPU resources) and the playback clock stay warm across the whole queue, so the per-item
/// present-pipeline rebuild a naive consumer pays is eliminated.
/// <para>
/// There is one player type and every player is a queue, a single file included — it is a queue of
/// one (ADR-0077 decision 1). <see cref="IMediaTransport"/> is the narrower view of the same
/// object, for a caller that drives transport and has nothing to say about the queue.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The inherited <see cref="IMediaTransport"/> acts on the <b>current</b>
/// item: <see cref="IMediaTransport.PlayAsync"/> / <see cref="IMediaTransport.PauseAsync"/>
/// pause and resume it, <see cref="IMediaTransport.SeekAsync"/> seeks within its
/// timeline, and <see cref="IMediaTransport.Position"/> /
/// <see cref="IMediaTransport.Duration"/> / <see cref="IMediaTransport.MediaInfo"/>
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
/// can be read there, and <see cref="IMediaTransport.SeekAsync"/> pauses on it at the position
/// sought, so a following <see cref="IMediaTransport.PlayAsync"/> plays it from there.
/// <see cref="IMediaTransport.PlayAsync"/> from <see cref="PlaybackState.Ended"/> plays the target
/// of a pending jump, or the next item the queue yields, or else starts the playlist again from
/// its first item. With nothing to play it returns a failed <see cref="Result"/> with
/// <see cref="ErrorCategory.InvalidOperation"/>, and the player stays in
/// <see cref="PlaybackState.Ended"/>. If the last item failed, nothing stays loaded, and a seek
/// from <see cref="PlaybackState.Ended"/> is refused the same way.
/// </para>
/// <para>
/// <b>Failed items.</b> An item that faults while it plays, or a later item that
/// cannot be started, raises <see cref="IMediaTransport.ErrorOccurred"/> and is skipped
/// as though it had ended. The state does not change: under
/// <see cref="RepeatMode.Off"/> a last item that faults while playing ends the
/// playlist in <see cref="PlaybackState.Ended"/>, and under
/// <see cref="RepeatMode.All"/> or <see cref="RepeatMode.One"/> the rotation
/// continues. An item that cannot be started is passed over under every repeat mode. The
/// player's very first item is treated as a single source's: if it cannot be opened the load
/// fails, and if it faults before the first <see cref="IMediaTransport.PlayAsync"/> the player
/// enters <see cref="PlaybackState.Error"/>. The player gives up and enters <see cref="PlaybackState.Error"/>
/// when more than eight items fail in a row. An item that ends or is skipped without
/// failing breaks the run, and so does a fault after an item has played for five
/// seconds, or for half its length if that is shorter.
/// </para>
/// </remarks>
public interface IMediaPlayer : IMediaTransport
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
    /// The jump follows <see cref="IMediaTransport.State"/> as a skip does. While
    /// <see cref="PlaybackState.Playing"/> the item plays; while
    /// <see cref="PlaybackState.Paused"/> it becomes current and stays paused. Before the first
    /// play, and at <see cref="PlaybackState.Ended"/>, it takes effect at the next
    /// <see cref="IMediaTransport.PlayAsync"/>. A jump to a playlist item continues the playlist
    /// from there. A later jump replaces one not yet taken.
    /// </para>
    /// <para>
    /// A jump to the current item does nothing and succeeds; seek to zero to restart it. A jump
    /// to an item that is not in the player, including a current item that has been removed, or
    /// while the player is in
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
    /// The skip follows <see cref="IMediaTransport.State"/>. While
    /// <see cref="PlaybackState.Playing"/>, the next item plays. While
    /// <see cref="PlaybackState.Paused"/>, the next item becomes current and stays paused
    /// until <see cref="IMediaTransport.PlayAsync"/>. A skip before the playlist has first played
    /// takes effect when it does. At <see cref="PlaybackState.Ended"/> the skip does nothing;
    /// call <see cref="IMediaTransport.PlayAsync"/> to play what is queued.
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

    /// <summary>
    /// Fires with the queue as it now stands, whenever it changes. Renders a playlist panel or a
    /// queue length without polling <see cref="GetPlaylist"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot is the event's payload rather than something to fetch afterwards, so a
    /// handler renders what the change produced instead of racing back for a queue that may have
    /// moved again. <see cref="PlaylistSnapshot.Revision"/> orders them.
    /// </para>
    /// <para>
    /// <b>It fires for more than the six edit verbs.</b> A latched jump and each half of a
    /// hand-off also change what a snapshot reports, so they raise it too, exactly matching when
    /// <c>Revision</c> advances. That is the point: the question this answers is "is the queue I
    /// drew still current", and the answer moves when the current item does.
    /// </para>
    /// <para>
    /// <see cref="SourceTransitioned"/> answers a different question. It fires only on a
    /// hand-off, and carries the item's <see cref="FrameFlow.Media.MediaInfo"/> and the reason.
    /// Subscribe to that one to react to an item starting; subscribe to this one to redraw a
    /// queue.
    /// </para>
    /// <para>
    /// Raised outside the coordinator's lock, so a handler may call back into the player, and on
    /// whichever thread made the change: the caller's thread for an edit, the player's for a
    /// hand-off. Delivery is serialized and in commit order, which is what lets
    /// <c>Revision</c> order what you receive. The cost is that a thread changing the queue can
    /// wait on another thread's in-flight handler, so keep handlers short or marshal, as a UI
    /// subscriber does anyway.
    /// </para>
    /// </remarks>
    IObservable<PlaylistSnapshot> PlaylistChanged { get; }

    /// <summary>
    /// Fires when an item failed and was skipped, naming the item and how it failed. Prefer this
    /// over <see cref="IMediaTransport.ErrorOccurred"/> for item failures: that stream cannot say
    /// which item an error belongs to, and reading <see cref="GetPlaylist"/> inside its handler
    /// does not answer it either, because the queue has already moved past the failed item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same failure also reaches <see cref="IMediaTransport.ErrorOccurred"/>, which stays because
    /// it is the only failure signal a caller holding that surface has. This one is raised first
    /// and carries the identical <see cref="PlaybackError"/> instance, so a consumer subscribed to
    /// both discards the second sighting by reference equality.
    /// </para>
    /// <para>
    /// It does not fire for every failure, and does not always mean the player carries on. The
    /// first item is treated as a single source's, so a failure before anything has played fails
    /// the load or enters <see cref="PlaybackState.Error"/> instead, raising nothing here. Nothing
    /// fires while the player is disposing. And the failure that exhausts the consecutive-failure
    /// guard is raised here and then followed by <see cref="PlaybackState.Error"/>, so a consumer
    /// that retries or re-queues on this event should watch the state too.
    /// </para>
    /// </remarks>
    IObservable<PlaylistItemFailed> ItemFailed { get; }

    /// <summary>
    /// Fires when the current item is back at its start after playing to its end, naming the item.
    /// Prefer this over <see cref="IMediaTransport.LoopRestarted"/> when the item matters.
    /// </summary>
    /// <remarks>
    /// The same loop also reaches <see cref="IMediaTransport.LoopRestarted"/>. This one is raised
    /// first and carries the identical <see cref="FrameFlow.Media.LoopRestarted"/> instance, so the
    /// same reference-equality discard applies.
    /// </remarks>
    IObservable<PlaylistItemLooped> ItemLooped { get; }

    /// <summary>
    /// Fires when the current item was expected to repeat and appears to have wedged instead,
    /// naming the item. Prefer this over <see cref="IMediaTransport.LoopStalled"/> when the item
    /// matters.
    /// </summary>
    /// <remarks>
    /// The same stall also reaches <see cref="IMediaTransport.LoopStalled"/>. This one is raised first
    /// and carries the identical <see cref="FrameFlow.Media.LoopStalled"/> instance, so the same
    /// reference-equality discard applies. Neither is a recovery: the player does not rebuild the
    /// wedged item.
    /// </remarks>
    IObservable<PlaylistItemStalled> ItemStalled { get; }
}
