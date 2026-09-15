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

    private PlaylistQueue _queue;

    private Action? _skipHandler;
    private Action? _jumpHandler;
    private object? _sessionToken;

    /// <summary>
    /// Seeds the coordinator with the initial playlist (the first element is the item that
    /// loads first) and the starting repeat mode.
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
        if (items.Count == 0)
            throw new ArgumentException(
                "A playlist requires at least one source.",
                nameof(initial)
            );
        _queue = PlaylistQueue.Create(items, repeat);
    }

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
                _queue = _queue.LatchAdvance(PlaylistAdvance.Skip);
        }

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
            _queue = _queue.Replace(items);
            handler = _jumpHandler;
        }

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
    /// Takes the item a replay from Ended will start with, before the controller unloads.
    /// Returns <see langword="false"/> when the player holds nothing to take.
    /// </summary>
    internal bool ReserveStart() => Apply(q => q.ReserveStart());

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
        var index = Apply(q => q.ReportCurrent(item, info));
        RaiseTransition(item, info, index, wrapped);
    }

    /// <summary>
    /// Fires <see cref="SourceTransitioned"/> for a start already recorded on the queue. Call it
    /// outside <see cref="Update{T}"/>, so a subscriber can call back into the coordinator. Nothing
    /// fires for an item with no metadata.
    /// </summary>
    internal void RaiseTransition(PlaylistItem item, MediaInfo? info, int index, bool wrapped)
    {
        if (info is not null)
            _transitioned.OnNext(
                new PlaylistTransition(item.Source, info, index, wrapped) { Item = item }
            );
    }

    /// <summary>
    /// Applies <paramref name="operation"/> to the queue under the lock and stores the queue it
    /// returns. The session steps its protocol through this, so a player edit and a step never
    /// interleave. The operation must not call back into the coordinator.
    /// </summary>
    internal T Update<T>(Func<PlaylistQueue, (PlaylistQueue Queue, T Result)> operation) =>
        Apply(operation);

    /// <summary>Disposes the transition subject. Called by the owning player wrapper.</summary>
    internal void Dispose() => _transitioned.Dispose();

    // ── Private ─────────────────────────────────────────────────────────────

    private void Apply(Func<PlaylistQueue, PlaylistQueue> operation)
    {
        lock (_gate)
            _queue = operation(_queue);
    }

    private T Apply<T>(Func<PlaylistQueue, (PlaylistQueue Queue, T Result)> operation)
    {
        lock (_gate)
        {
            (_queue, var result) = operation(_queue);
            return result;
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
