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
/// <b>The model</b> (draft ADR <i>The playlist player's queue</i>, #171). The coordinator keeps
/// a playlist of items that stay after they play, a cursor into it, next items from
/// <see cref="SetNext"/> and queued items from <see cref="Enqueue"/>. Next and queued items are
/// one-shot: each leaves its collection when it is taken and never joins the playlist. The
/// cursor is a gap: taking a playlist item puts it just after that item, and removing an item
/// keeps it between the items on either side.
/// </para>
/// <para>
/// <b>The order.</b> An advance takes the pending jump's target, then the first next item, then
/// the playlist item after the cursor, then the first queued item. At the end of a pass it goes
/// back to the first playlist item under <see cref="RepeatMode.All"/>, or on a skip or failed
/// start under <see cref="RepeatMode.One"/>, and otherwise ends. Under
/// <see cref="RepeatMode.One"/> a natural end or a fault replays the current item. The repeat
/// mode is not stored in the collections, so a switch applies to the whole playlist.
/// </para>
/// </remarks>
internal sealed class PlaylistCoordinator
{
    private readonly object _gate = new();
    private readonly List<PlaylistItem> _playlist = new();
    private readonly LinkedList<PlaylistItem> _next = new();
    private readonly LinkedList<PlaylistItem> _queued = new();
    private readonly PlaybackSubject<PlaylistTransition> _transitioned = new();

    private RepeatMode _repeat;

    // The number of playlist items before the cursor: the playlist continues with the item at
    // this index.
    private int _cursor;

    private PlaylistItem? _pendingJump;

    // The item most recently taken, whether it has started, and whether it has been removed
    // while current.
    private PlaylistItem? _current;
    private bool _currentStarted;
    private bool _currentRemoved;

    // Taken for a replay from Ended before the controller unloads, and handed to the next
    // session's first take.
    private PlaylistItem? _reservedStart;

    // The item SourceTransitioned last reported, and its metadata.
    private PlaylistItem? _reported;
    private MediaInfo? _currentInfo;
    private TimeSpan _currentDuration;

    private int _transitionCount = -1;
    private long _revision;

    // What a skip or end-of-stream latched before a session could act on it: 0 for nothing,
    // otherwise a PlaylistAdvance plus one.
    private int _latchedAdvance;

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
        foreach (var s in initial)
        {
            ArgumentNullException.ThrowIfNull(s);
            _playlist.Add(new PlaylistItem(s));
        }
        if (_playlist.Count == 0)
            throw new ArgumentException(
                "A playlist requires at least one source.",
                nameof(initial)
            );
        _repeat = repeat;
    }

    // ── Player-facing ───────────────────────────────────────────────────────

    /// <summary>
    /// The source of the item that most recently started, or <see langword="null"/> before the
    /// first.
    /// </summary>
    public IMediaSource? CurrentSource
    {
        get
        {
            lock (_gate)
                return _reported?.Source;
        }
    }

    /// <summary>Metadata for the item that most recently started, or <see langword="null"/>.</summary>
    public MediaInfo? CurrentMediaInfo
    {
        get
        {
            lock (_gate)
                return _currentInfo;
        }
    }

    /// <summary>Duration of the item that most recently started, or <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan CurrentDuration
    {
        get
        {
            lock (_gate)
                return _currentDuration;
        }
    }

    /// <summary>The active loop policy. Settable at runtime.</summary>
    public RepeatMode RepeatMode
    {
        get
        {
            lock (_gate)
                return _repeat;
        }
        set
        {
            lock (_gate)
                _repeat = value;
        }
    }

    /// <summary>Fires once per hand-off (including the first item) with the now-current item.</summary>
    public IObservable<PlaylistTransition> SourceTransitioned => _transitioned;

    /// <summary>Appends a source to the playlist, where it stays after it plays.</summary>
    public PlaylistItem Add(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var item = new PlaylistItem(source);
        lock (_gate)
        {
            _playlist.Add(item);
            _revision++;
        }
        return item;
    }

    /// <summary>
    /// Adds a one-shot item that plays after the playlist items left in the current pass.
    /// </summary>
    public PlaylistItem Enqueue(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var item = new PlaylistItem(source);
        lock (_gate)
        {
            _queued.AddLast(item);
            _revision++;
        }
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
        lock (_gate)
        {
            _next.AddFirst(item);
            _revision++;
        }
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
            handler = _skipHandler;

        if (handler is not null)
            handler();
        else
            LatchAdvance(PlaylistAdvance.Skip);
    }

    /// <summary>
    /// Records <paramref name="item"/> as the pending jump, replacing any earlier one, and pokes
    /// the attached session.
    /// </summary>
    public JumpRequest RequestJump(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Action? handler;
        lock (_gate)
        {
            if (ReferenceEquals(item, _current))
                return JumpRequest.AlreadyCurrent;
            if (!InCollectionsLocked(item))
                return JumpRequest.NotInPlayer;

            _pendingJump = item;
            _revision++;
            handler = _jumpHandler;
        }

        handler?.Invoke();
        return JumpRequest.Pending;
    }

    /// <summary>
    /// Removes a playlist, next or queued item, or marks the current item removed. The current
    /// item is never stopped. Returns <see langword="false"/> when the item is not in the player.
    /// </summary>
    public bool Remove(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            var removed = false;

            var index = _playlist.IndexOf(item);
            if (index >= 0)
            {
                _playlist.RemoveAt(index);
                if (index < _cursor)
                    _cursor--;
                removed = true;
            }
            else if (_next.Remove(item) || _queued.Remove(item))
            {
                removed = true;
            }

            if (ReferenceEquals(item, _current) && !_currentRemoved)
            {
                _currentRemoved = true;
                removed = true;
            }

            if (ReferenceEquals(item, _pendingJump))
                _pendingJump = null;

            if (removed)
                _revision++;
            return removed;
        }
    }

    /// <summary>
    /// Removes every playlist, next and queued item, discards a pending jump, and marks the
    /// current item removed. The current item plays on.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
            ClearLocked();
    }

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
            ClearLocked();
            _playlist.AddRange(items);
            _pendingJump = items[0];
            _revision++;
            handler = _jumpHandler;
        }

        handler?.Invoke();
        return items;
    }

    /// <summary>A copy of the queue, taken under the coordinator's lock.</summary>
    public PlaylistSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PlaylistSnapshot(
                _playlist.ToArray(),
                _next.ToArray(),
                _queued.ToArray(),
                _current,
                _currentStarted,
                _cursor,
                _pendingJump,
                _revision
            );
        }
    }

    // ── Session-facing (internal) ───────────────────────────────────────────

    /// <summary>
    /// The failure count, kept here so it survives the new session a replay from Ended loads.
    /// Read and written only under a session's transition gate.
    /// </summary>
    internal PlaylistFailureGuard Failures { get; } = new();

    /// <summary>Whether any item has started since the player was created.</summary>
    internal bool AnyStarted
    {
        get
        {
            lock (_gate)
                return _transitionCount >= 0;
        }
    }

    /// <summary>Whether a jump is waiting to be taken.</summary>
    internal bool HasPendingJump
    {
        get
        {
            lock (_gate)
                return _pendingJump is not null;
        }
    }

    /// <summary>
    /// Latches an advance for the next <c>PlayAsync</c>, keeping why it happened. Called when no
    /// session can act on it yet.
    /// </summary>
    internal void LatchAdvance(PlaylistAdvance reason) =>
        Interlocked.Exchange(ref _latchedAdvance, (int)reason + 1);

    /// <summary>Atomically reads and clears a latched advance.</summary>
    internal PlaylistAdvance? ConsumeLatchedAdvance()
    {
        var latched = Interlocked.Exchange(ref _latchedAdvance, 0);
        return latched == 0 ? null : (PlaylistAdvance)(latched - 1);
    }

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
    internal bool ReserveStart()
    {
        lock (_gate)
        {
            if (_reservedStart is not null)
                return true;
            _reservedStart = TakeStartLocked();
            return _reservedStart is not null;
        }
    }

    /// <summary>
    /// Takes the item a new session starts with: a reserved item if there is one; otherwise the
    /// pending jump's target, the first next item, the playlist item after the cursor or the
    /// first queued item; otherwise the first playlist item again. Returns
    /// <see langword="null"/> when the player holds nothing.
    /// </summary>
    internal PlaylistItem? TakeStart()
    {
        lock (_gate)
        {
            if (_reservedStart is { } reserved)
            {
                _reservedStart = null;
                return reserved;
            }
            return TakeStartLocked();
        }
    }

    internal enum NextKind
    {
        /// <summary>Move to a different source.</summary>
        Advance,

        /// <summary>
        /// The next item plays the same source object as the current one: replay it, reusing
        /// the live runtime where the session can.
        /// </summary>
        Replay,

        /// <summary>Nothing is left to take — the playlist is finished.</summary>
        End,
    }

    internal readonly record struct NextDecision(NextKind Kind, PlaylistItem? Item, bool Wrapped);

    /// <summary>
    /// Decides what plays after the current item, and takes it. Called by the session under its
    /// own transition gate.
    /// </summary>
    /// <param name="reason">
    /// Why the advance happened. Under <see cref="RepeatMode.One"/> a natural end or a fault
    /// replays the current item, and a skip or failed start moves on.
    /// </param>
    /// <remarks>
    /// <b>Same-source replay.</b> The decision is <see cref="NextKind.Replay"/> whenever the item
    /// taken plays the <i>same source object</i> as the current one: under
    /// <see cref="RepeatMode.One"/>, when a <see cref="RepeatMode.All"/> playlist of one source
    /// wraps, or at a back-to-back duplicate. That lets the session reuse the live decode
    /// runtime in place instead of rebuilding it, which is what makes the single-clip loop seam
    /// gapless. <see cref="NextDecision.Wrapped"/> still rides along for the transition report.
    /// </remarks>
    internal NextDecision DecideNext(PlaylistAdvance reason)
    {
        lock (_gate)
        {
            if (_pendingJump is { } jump)
            {
                _pendingJump = null;
                return TakeLocked(jump, wrapped: false);
            }

            var movesOn =
                reason is PlaylistAdvance.Skip or PlaylistAdvance.FailedStart || _currentRemoved;

            if (_repeat == RepeatMode.One && !movesOn && _current is { } current)
                return new NextDecision(NextKind.Replay, current, Wrapped: false);

            if (_next.First is { } next)
            {
                _next.RemoveFirst();
                return TakeLocked(next.Value, wrapped: false);
            }

            if (_cursor < _playlist.Count)
                return TakeLocked(_playlist[_cursor], wrapped: false);

            if (_queued.First is { } queued)
            {
                _queued.RemoveFirst();
                return TakeLocked(queued.Value, wrapped: false);
            }

            if (
                _playlist.Count > 0
                && (_repeat == RepeatMode.All || (_repeat == RepeatMode.One && movesOn))
            )
            {
                return TakeLocked(_playlist[0], wrapped: true);
            }

            return new NextDecision(NextKind.End, null, Wrapped: false);
        }
    }

    /// <summary>
    /// Records that <paramref name="item"/> has started, updates the current metadata, and fires
    /// <see cref="SourceTransitioned"/>. The notification is raised outside the lock so a
    /// subscriber can call back into the coordinator.
    /// </summary>
    internal void ReportCurrent(PlaylistItem item, MediaInfo? info, bool wrapped)
    {
        ArgumentNullException.ThrowIfNull(item);
        int index;
        lock (_gate)
        {
            if (ReferenceEquals(item, _current))
                _currentStarted = true;
            _reported = item;
            _currentInfo = info;
            _currentDuration = info?.Duration ?? TimeSpan.Zero;
            index = ++_transitionCount;
            _revision++;
        }

        if (info is not null)
            _transitioned.OnNext(
                new PlaylistTransition(item.Source, info, index, wrapped) { Item = item }
            );
    }

    /// <summary>Disposes the transition subject. Called by the owning player wrapper.</summary>
    internal void Dispose() => _transitioned.Dispose();

    // ── Private ─────────────────────────────────────────────────────────────

    private PlaylistItem? TakeStartLocked()
    {
        if (_pendingJump is { } jump)
        {
            _pendingJump = null;
            return TakeLocked(jump, wrapped: false).Item;
        }
        if (_next.First is { } next)
        {
            _next.RemoveFirst();
            return TakeLocked(next.Value, wrapped: false).Item;
        }
        if (_cursor < _playlist.Count)
            return TakeLocked(_playlist[_cursor], wrapped: false).Item;
        if (_queued.First is { } queued)
        {
            _queued.RemoveFirst();
            return TakeLocked(queued.Value, wrapped: false).Item;
        }
        return _playlist.Count > 0 ? TakeLocked(_playlist[0], wrapped: false).Item : null;
    }

    private NextDecision TakeLocked(PlaylistItem item, bool wrapped)
    {
        var index = _playlist.IndexOf(item);
        if (index >= 0)
            _cursor = index + 1;
        else if (!_next.Remove(item))
            _queued.Remove(item);

        var previous = _current;
        _current = item;
        _currentStarted = false;
        _currentRemoved = false;
        _revision++;

        var kind =
            previous is not null && ReferenceEquals(previous.Source, item.Source)
                ? NextKind.Replay
                : NextKind.Advance;
        return new NextDecision(kind, item, wrapped);
    }

    private bool InCollectionsLocked(PlaylistItem item) =>
        _playlist.Contains(item) || _next.Contains(item) || _queued.Contains(item);

    private void ClearLocked()
    {
        _playlist.Clear();
        _next.Clear();
        _queued.Clear();
        _pendingJump = null;
        _cursor = 0;
        if (_current is not null)
            _currentRemoved = true;
        _revision++;
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
