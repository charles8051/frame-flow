// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Shared, thread-safe bridge between a playlist-capable player surface and the
/// <see cref="PlaylistSession"/> that drives the decode runtime underneath it.
/// The player edits the queue and reads it; the session takes items from it at each
/// item boundary (<see cref="DecideNext"/>) and reports the new current item back
/// (<see cref="ReportCurrent"/>). Created once per playlist player and lives as long as the
/// player, across every session the controller loads.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps the playlist concept <i>out</i> of
/// <see cref="PlaybackControllerCore"/> and the per-item
/// <see cref="SubstrateSession"/>: the controller drives one
/// <see cref="PlaylistSession"/> as a single session, and the playlist's queue,
/// loop policy, and current-item state live here instead.
/// </para>
/// <para>
/// <b>A cell around a value.</b> The queue itself is a <see cref="PlaylistQueue"/>, which holds
/// the rules. The coordinator keeps the current value, applies each operation to it under its
/// lock and stores the result. It also holds what is not part of the queue: the attached
/// session's skip and jump handlers, and the <see cref="SourceTransitioned"/> stream, which it
/// raises outside the lock so a subscriber can call back in.
/// </para>
/// </remarks>
internal sealed class PlaylistCoordinator
{
    private readonly object _gate = new();
    private readonly PlaybackSubject<PlaylistTransition> _transitioned = new();
    private readonly PlaybackSubject<PlaylistSnapshot> _changed = new();
    private readonly object _raiseGate = new();
    private readonly Queue<PlaylistSnapshot> _pendingChanges = new();

    private PlaylistQueue _queue;

    private bool _replayPending;

    private Action? _skipHandler;
    private Action? _jumpHandler;
    private object? _sessionToken;

    /// <summary>
    /// Seeds the coordinator with the initial playlist (the first element is the item that
    /// loads first) and the starting repeat mode. The playlist may be empty: the player then
    /// starts with nothing loaded, and the first <c>PlayAsync</c> starts whatever has been added
    /// by then.
    /// </summary>
    public PlaylistCoordinator(IEnumerable<IMediaSource> initial, RepeatMode repeat)
    {
        ArgumentNullException.ThrowIfNull(initial);
        var items = new List<PlaylistItem>();
        foreach (var s in initial)
        {
            ArgumentNullException.ThrowIfNull(s);
            items.Add(new PlaylistItem(s));
        }
        _queue = PlaylistQueue.Create(items, repeat);
    }

    /// <summary>
    /// An empty coordinator for a controller that plays one source at a time. Each load makes the
    /// loaded source the only item (<see cref="LoadSource"/>).
    /// </summary>
    internal PlaylistCoordinator(RepeatMode repeat) => _queue = PlaylistQueue.Create([], repeat);

    /// <summary>The queue as it is now.</summary>
    internal PlaylistQueue Queue
    {
        get
        {
            lock (_gate)
                return _queue;
        }
    }

    // ── Player-facing ───────────────────────────────────────────────────────

    /// <summary>
    /// The source of the item that most recently started, or <see langword="null"/> before the
    /// first.
    /// </summary>
    public IMediaSource? CurrentSource => Queue.Reported?.Source;

    /// <summary>Metadata for the item that most recently started, or <see langword="null"/>.</summary>
    public MediaInfo? CurrentMediaInfo => Queue.ReportedInfo;

    /// <summary>Duration of the item that most recently started, or <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan CurrentDuration => Queue.ReportedDuration;

    /// <summary>The active loop policy. Settable at runtime.</summary>
    public RepeatMode RepeatMode
    {
        get => Queue.Repeat;
        set => Apply(q => q.WithRepeatMode(value));
    }

    /// <summary>Fires once per hand-off (including the first item) with the now-current item.</summary>
    public IObservable<PlaylistTransition> SourceTransitioned => _transitioned;

    /// <summary>
    /// Fires with the snapshot the change produced, whenever the queue's
    /// <see cref="PlaylistQueue.Revision"/> advances.
    /// </summary>
    /// <remarks>
    /// Gated on the revision rather than on the call, because <see cref="Apply(Func{PlaylistQueue,
    /// PlaylistQueue})"/> is also how the session steps its own bookkeeping. Exactly the nine
    /// operations that change what a snapshot reports move the revision: the six edit verbs, a
    /// latched jump, and the two halves of a hand-off. <c>ItemEnded</c>, <c>ItemFailed</c> and the
    /// advance latch do not.
    /// </remarks>
    public IObservable<PlaylistSnapshot> PlaylistChanged => _changed;

    /// <summary>Appends a source to the playlist, where it stays after it plays.</summary>
    public PlaylistItem Add(IMediaSource source)
    {
        var item = new PlaylistItem(source);
        Apply(q => q.Add(item));
        return item;
    }

    /// <summary>
    /// Adds a one-shot item that plays after the playlist items left in the current pass.
    /// </summary>
    public PlaylistItem Enqueue(IMediaSource source)
    {
        var item = new PlaylistItem(source);
        Apply(q => q.Enqueue(item));
        return item;
    }

    /// <summary>
    /// Adds a one-shot item that plays next, ahead of the next items already there.
    /// A <see langword="null"/> source adds nothing and returns <see langword="null"/>.
    /// </summary>
    public PlaylistItem? SetNext(IMediaSource? source)
    {
        if (source is null)
            return null;
        var item = new PlaylistItem(source);
        Apply(q => q.SetNext(item));
        return item;
    }

    /// <summary>
    /// Requests that the session end the current item now and advance. If a session is
    /// attached it is poked immediately; otherwise the request is latched and consumed on the
    /// next <c>PlayAsync</c> (covers a skip issued before the first play, or while a replay
    /// loads).
    /// </summary>
    public void RequestSkip()
    {
        Action? handler;
        lock (_gate)
        {
            handler = _skipHandler;
            if (handler is null)
                Commit(_queue.LatchAdvance(PlaylistAdvance.Skip));
        }

        RaiseChanged();
        handler?.Invoke();
    }

    /// <summary>
    /// Records <paramref name="item"/> as the pending jump, replacing any earlier one, and pokes
    /// the attached session.
    /// </summary>
    public JumpRequest RequestJump(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Action? handler;
        JumpRequest result;
        lock (_gate)
        {
            (_queue, result) = _queue.RequestJump(item);
            handler = result == JumpRequest.Pending ? _jumpHandler : null;
        }

        handler?.Invoke();
        return result;
    }

    /// <summary>
    /// Removes a playlist, next or queued item, or marks the current item removed. The current
    /// item is never stopped. Returns <see langword="false"/> when the item is not in the player.
    /// </summary>
    public bool Remove(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Apply(q => q.Remove(item));
    }

    /// <summary>
    /// Removes every playlist, next and queued item, discards a pending jump, and marks the
    /// current item removed. The current item plays on.
    /// </summary>
    public void Clear() => Apply(q => q.Clear());

    /// <summary>
    /// Clears the player, adds <paramref name="sources"/> as the playlist and makes the first
    /// new item the pending jump, in one edit. Pokes the attached session.
    /// </summary>
    public IReadOnlyList<PlaylistItem> Replace(IEnumerable<IMediaSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var items = sources
            .Select(s =>
            {
                ArgumentNullException.ThrowIfNull(s);
                return new PlaylistItem(s);
            })
            .ToArray();
        if (items.Length == 0)
            throw new ArgumentException(
                "A replacement playlist requires at least one source.",
                nameof(sources)
            );

        Action? handler;
        lock (_gate)
        {
            Commit(_queue.Replace(items));
            handler = _jumpHandler;
        }

        RaiseChanged();
        handler?.Invoke();
        return items;
    }

    /// <summary>A copy of the queue, taken under the coordinator's lock.</summary>
    public PlaylistSnapshot Snapshot() => Queue.Snapshot();

    // ── Session-facing (internal) ───────────────────────────────────────────

    /// <summary>
    /// Items that failed in a row without making progress. Kept here so the count survives the
    /// new session a replay from Ended loads.
    /// </summary>
    internal int ConsecutiveFailures => Queue.ConsecutiveFailures;

    /// <summary>Records an item that ended without failing, which resets the failure count.</summary>
    internal void ItemEnded() => Apply(q => q.ItemEnded());

    /// <summary>
    /// Records a failed item. Returns <see langword="true"/> when the playlist should give up.
    /// </summary>
    internal bool ItemFailed(TimeSpan playedFor, TimeSpan itemLength) =>
        Apply(q => q.ItemFailed(playedFor, itemLength));

    /// <summary>
    /// Makes <paramref name="source"/> the only item, in a new queue with the same repeat mode. A
    /// replay from <c>Ended</c> keeps the queue instead, whatever it now holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The replay is marked on the coordinator, not on the queue: <see cref="ReserveStart"/> sets
    /// the mark and the next load consumes it, under the same lock. A queue edit between the two
    /// therefore cannot hide the replay, and the edit stands: a replacement made while the player
    /// was ended is what the replay then starts with, because the queue already holds it.
    /// </para>
    /// <para>
    /// Every other load replaces the queue, so an ordinary reload plays the source it was given and
    /// nothing that was enqueued before it. A new queue has started nothing, so a first item that
    /// cannot be opened fails the load, as it does on a controller's first load.
    /// </para>
    /// <para>
    /// A replay whose load fails leaves the mark set, and the controller is then in <c>Error</c>,
    /// which takes no further load.
    /// </para>
    /// <para>Called before the new session attaches, so no handler is poked.</para>
    /// </remarks>
    internal void LoadSource(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_replayPending)
            {
                _replayPending = false;
                return;
            }

            // AsOnly, not PlaylistQueue.Create: a fresh queue's revision restarts at zero, and a
            // subscriber that has already seen revision 3 must not then be handed 0.
            Commit(_queue.AsOnly(new PlaylistItem(source)));
        }

        RaiseChanged();
    }

    /// <summary>Whether any item has started since the player was created.</summary>
    internal bool AnyStarted => Queue.AnyStarted;

    /// <summary>Whether a jump is waiting to be taken.</summary>
    internal bool HasPendingJump => Queue.HasPendingJump;

    /// <summary>
    /// Latches an advance for the next <c>PlayAsync</c>, keeping why it happened. Called when no
    /// session can act on it yet.
    /// </summary>
    internal void LatchAdvance(PlaylistAdvance reason) => Apply(q => q.LatchAdvance(reason));

    /// <summary>Atomically reads and clears a latched advance.</summary>
    internal PlaylistAdvance? ConsumeLatchedAdvance() => Apply(q => q.ConsumeLatchedAdvance());

    /// <summary>
    /// Wires the active session's skip and jump entry points. Set by the session in its
    /// <c>InitializeAsync</c>. Returns a token for <see cref="DetachSession"/>.
    /// </summary>
    internal object AttachSession(Action onSkip, Action onJump)
    {
        ArgumentNullException.ThrowIfNull(onSkip);
        ArgumentNullException.ThrowIfNull(onJump);
        var token = new object();
        lock (_gate)
        {
            _skipHandler = onSkip;
            _jumpHandler = onJump;
            _sessionToken = token;
        }
        return token;
    }

    /// <summary>
    /// Unwires a session's entry points if they are still the attached ones, so a request made
    /// while no session is attached is latched or left pending for the next.
    /// </summary>
    internal void DetachSession(object token)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_sessionToken, token))
                return;
            _skipHandler = null;
            _jumpHandler = null;
            _sessionToken = null;
        }
    }

    /// <summary>
    /// Takes the item the next session will start with and marks the load that follows so it keeps
    /// this queue. Returns <see langword="null"/> when the player holds nothing to take, which
    /// leaves the mark alone.
    /// </summary>
    /// <remarks>
    /// Two callers reserve: a replay from <c>Ended</c>, before the controller unloads the session
    /// it is replaying, and the first <c>PlayAsync</c> on a player built with an empty queue.
    /// Both need the load that follows to play the queue rather than replace it.
    /// </remarks>
    internal PlaylistItem? ReserveStart()
    {
        PlaylistItem? reservedStart;
        lock (_gate)
        {
            var (queue, reserved) = _queue.ReserveStart();
            if (!reserved)
                return null;
            Commit(queue);
            _replayPending = true;
            reservedStart = queue.ReservedStart;
        }

        RaiseChanged();
        return reservedStart;
    }

    /// <summary>
    /// Gives back a reservation whose load failed, and clears the mark it set. The next load is
    /// then an ordinary one.
    /// </summary>
    internal void ReleaseStart()
    {
        lock (_gate)
        {
            _replayPending = false;
            Commit(_queue.ReleaseStart());
        }

        RaiseChanged();
    }

    /// <summary>
    /// Takes the item a new session starts with. Returns <see langword="null"/> when the player
    /// holds nothing. <see cref="PlaylistQueue.TakeStart"/> gives the order.
    /// </summary>
    internal PlaylistItem? TakeStart() => Apply(q => q.TakeStart());

    /// <summary>
    /// Decides what plays after the current item, and takes it. Called by the session under its
    /// own transition gate. <see cref="PlaylistQueue.DecideNext"/> gives the rules.
    /// </summary>
    internal PlaylistQueue.NextDecision DecideNext(PlaylistAdvance reason) =>
        Apply(q => q.DecideNext(reason));

    /// <summary>
    /// Records that <paramref name="item"/> has started, updates the current metadata, and fires
    /// <see cref="SourceTransitioned"/>. The notification is raised outside the lock so a
    /// subscriber can call back into the coordinator.
    /// </summary>
    internal void ReportCurrent(PlaylistItem item, MediaInfo? info, bool wrapped)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Read before the queue moves it: the reason describes the item that ended, while the rest
        // of the transition describes the one that started.
        var previous = Queue.Reported;
        var index = Apply(q => q.ReportCurrent(item, info));
        RaiseTransition(
            item,
            info,
            index,
            wrapped,
            previous,
            previous is null ? PlaylistTransitionReason.FirstItem : PlaylistTransitionReason.EndOfItem
        );
    }

    /// <summary>
    /// Fires <see cref="SourceTransitioned"/> for a start already recorded on the queue. Call it
    /// outside <see cref="Update{T}"/>, so a subscriber can call back into the coordinator. Nothing
    /// fires for an item with no metadata.
    /// </summary>
    internal void RaiseTransition(
        PlaylistItem item,
        MediaInfo? info,
        int index,
        bool wrapped,
        PlaylistItem? previous,
        PlaylistTransitionReason reason
    )
    {
        if (info is not null)
            _transitioned.OnNext(
                new PlaylistTransition(item, info, index, wrapped, previous, reason)
            );
    }

    /// <summary>
    /// Applies <paramref name="operation"/> to the queue under the lock and stores the queue it
    /// returns. The session steps its protocol through this, so a player edit and a step never
    /// interleave. The operation must not call back into the coordinator.
    /// </summary>
    internal T Update<T>(Func<PlaylistQueue, (PlaylistQueue Queue, T Result)> operation) =>
        Apply(operation);

    /// <summary>
    /// Disposes the transition subject. Called by whoever owns this coordinator: the player
    /// wrapper for a playlist, and the session factory for a controller's own queue of one.
    /// </summary>
    internal void Dispose()
    {
        _transitioned.Dispose();
        _changed.Dispose();
    }

    // ── Private ─────────────────────────────────────────────────────────────

    private void Apply(Func<PlaylistQueue, PlaylistQueue> operation)
    {
        lock (_gate)
            Commit(operation(_queue));

        RaiseChanged();
    }

    private T Apply<T>(Func<PlaylistQueue, (PlaylistQueue Queue, T Result)> operation)
    {
        T result;
        lock (_gate)
        {
            var (queue, value) = operation(_queue);
            result = value;
            Commit(queue);
        }

        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Stores <paramref name="next"/> and queues the snapshot <see cref="PlaylistChanged"/>
    /// should carry, when the revision moved. Call while holding <see cref="_gate"/>, then call
    /// <see cref="RaiseChanged"/> after releasing it.
    /// </summary>
    /// <remarks>
    /// Every assignment to <c>_queue</c> outside the constructors goes through here. <c>Apply</c>
    /// is not the only writer: <see cref="Replace"/> and <see cref="LoadSource"/> take the lock
    /// themselves because they need more than the queue under it, and a first draft that raised
    /// from <c>Apply</c> alone silently dropped both. The snapshot is taken here, from the queue
    /// being committed, rather than read back from <c>_queue</c> afterwards: a second edit landing
    /// in between would otherwise make both notifications carry the later queue.
    /// </remarks>
    private void Commit(PlaylistQueue next)
    {
        var moved = next.Revision != _queue.Revision;
        _queue = next;
        if (moved)
            _pendingChanges.Enqueue(next.Snapshot());
    }

    /// <summary>
    /// Delivers queued snapshots in the order they were committed. Call after releasing
    /// <see cref="_gate"/>, never while holding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both locks are load-bearing and neither alone is enough. Raising under <see cref="_gate"/>
    /// would run a subscriber's callback inside the coordinator's own lock. Raising under no lock
    /// at all lets a thread that committed <em>later</em> publish first: the commit order is
    /// established under <c>_gate</c>, but the window between releasing it and calling
    /// <c>OnNext</c> is not ordered, so an edit and a hand-off on different threads could deliver
    /// revision 2 before revision 1. A consumer told that <c>Revision</c> orders the stream would
    /// then render a superseded queue last.
    /// </para>
    /// <para>
    /// So the snapshots are queued under <c>_gate</c>, which puts them in commit order, and this
    /// lock lets one thread drain that queue at a time. The cost is that a thread committing a
    /// change can wait on another thread's in-flight handler. Handlers are expected to be short
    /// or to marshal, which is what a UI subscriber does anyway
    /// (<c>ObserveOnUiThread</c>).
    /// </para>
    /// <para>
    /// A subscriber that edits the queue from inside its own handler re-enters this on the same
    /// thread, which <see langword="lock"/> allows, and drains its own snapshot nested. Order
    /// still holds.
    /// </para>
    /// </remarks>
    private void RaiseChanged()
    {
        lock (_raiseGate)
        {
            while (true)
            {
                PlaylistSnapshot next;
                lock (_gate)
                {
                    if (!_pendingChanges.TryDequeue(out next!))
                        return;
                }

                _changed.OnNext(next);
            }
        }
    }
}

/// <summary>Why a playlist advance happened.</summary>
internal enum PlaylistAdvance
{
    /// <summary>The current item reported end-of-stream.</summary>
    EndOfStream,

    /// <summary>The caller skipped or jumped.</summary>
    Skip,

    /// <summary>The current item faulted while it played.</summary>
    Fault,

    /// <summary>The item just taken could not be opened or started.</summary>
    FailedStart,
}

/// <summary>What <see cref="PlaylistCoordinator.RequestJump"/> did.</summary>
internal enum JumpRequest
{
    /// <summary>The jump is pending and the session has been poked.</summary>
    Pending,

    /// <summary>The target is the current item, so nothing changes.</summary>
    AlreadyCurrent,

    /// <summary>The target is not in the player.</summary>
    NotInPlayer,
}
