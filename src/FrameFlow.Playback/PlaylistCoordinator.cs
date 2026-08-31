// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Shared, thread-safe bridge between a playlist-capable player surface and the
/// <see cref="PlaylistSession"/> that drives the decode runtime underneath it.
/// The player mutates the queue (<see cref="Enqueue"/> / <see cref="SetNext"/> /
/// <see cref="RequestSkip"/>) and reads the current item; the session consumes
/// the queue at each item boundary (<see cref="DecideNext"/>) and reports the
/// new current item back (<see cref="ReportCurrent"/>). Created once per playlist
/// player and lives as long as the player.
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
/// <b>Loop model.</b> Under <see cref="RepeatMode.All"/> each dequeued item is
/// copied into a one-cycle loop buffer; when the upcoming queue empties, the
/// loop buffer refills it (and is cleared to re-accumulate), giving an unbounded
/// loop over a bounded amount of state. Under <see cref="RepeatMode.Off"/> played
/// items are discarded, so a consumer that enqueues the next item on each
/// transition gets continuous rotation with no growth. Under
/// <see cref="RepeatMode.One"/> the queue is untouched and the current item
/// replays.
/// </para>
/// </remarks>
internal sealed class PlaylistCoordinator
{
    private readonly object _gate = new();
    private readonly LinkedList<IMediaSource> _upcoming = new();
    private readonly List<IMediaSource> _loopBuffer = new();
    private readonly PlaybackSubject<PlaylistTransition> _transitioned = new();

    private RepeatMode _repeat;
    private IMediaSource? _current;
    private MediaInfo? _currentInfo;
    private TimeSpan _currentDuration;
    private int _transitionCount = -1;
    private int _skipRequested;
    private Action? _skipHandler;

    /// <summary>
    /// Seeds the coordinator with the initial play queue (the first element is
    /// the item that loads first) and the starting repeat mode.
    /// </summary>
    public PlaylistCoordinator(IEnumerable<IMediaSource> initial, RepeatMode repeat)
    {
        ArgumentNullException.ThrowIfNull(initial);
        foreach (var s in initial)
        {
            ArgumentNullException.ThrowIfNull(s);
            _upcoming.AddLast(s);
        }
        if (_upcoming.Count == 0)
            throw new ArgumentException(
                "A playlist requires at least one source.",
                nameof(initial)
            );
        _repeat = repeat;
    }

    // ── Player-facing (public-ish; reached via the player wrapper) ──────────

    /// <summary>The source currently presenting, or <see langword="null"/> before the first item.</summary>
    public IMediaSource? CurrentSource
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>Metadata for the current item, or <see langword="null"/> before the first item.</summary>
    public MediaInfo? CurrentMediaInfo
    {
        get
        {
            lock (_gate)
                return _currentInfo;
        }
    }

    /// <summary>Duration of the current item, or <see cref="TimeSpan.Zero"/> before the first item.</summary>
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

    /// <summary>Fires once per hand-off (including the first item) with the now-current source.</summary>
    public IObservable<PlaylistTransition> SourceTransitioned => _transitioned;

    /// <summary>Append a source to the tail of the play queue.</summary>
    public void Enqueue(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
            _upcoming.AddLast(source);
    }

    /// <summary>
    /// Make <paramref name="source"/> the very next item to play, ahead of
    /// anything already queued. A <see langword="null"/> value is a no-op.
    /// </summary>
    public void SetNext(IMediaSource? source)
    {
        if (source is null)
            return;
        lock (_gate)
            _upcoming.AddFirst(source);
    }

    /// <summary>
    /// Request that the session end the current item now and advance. If a
    /// session is attached it is poked immediately; otherwise the request is
    /// latched and consumed on the next <c>PlayAsync</c> (covers a skip issued
    /// before the first play).
    /// </summary>
    public void RequestSkip()
    {
        Action? handler;
        lock (_gate)
            handler = _skipHandler;

        if (handler is not null)
            handler();
        else
            Interlocked.Exchange(ref _skipRequested, 1);
    }

    // ── Session-facing (internal) ───────────────────────────────────────────

    /// <summary>Atomically reads and clears the pending skip request.</summary>
    internal bool ConsumeSkipRequest() => Interlocked.Exchange(ref _skipRequested, 0) == 1;

    /// <summary>
    /// Wires the active session's skip entry point so <see cref="RequestSkip"/>
    /// can poke it directly. Set by the session in its <c>InitializeAsync</c>.
    /// </summary>
    internal void AttachSkipHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _skipHandler = handler;
    }

    /// <summary>
    /// Pops the first item to play. Called once by the session's
    /// <c>InitializeAsync</c>. Under <see cref="RepeatMode.All"/> the item is
    /// also recorded in the loop buffer so the first wrap includes it.
    /// </summary>
    internal IMediaSource First()
    {
        lock (_gate)
        {
            var first = _upcoming.First!.Value;
            _upcoming.RemoveFirst();
            if (_repeat == RepeatMode.All)
                _loopBuffer.Add(first);
            return first;
        }
    }

    internal enum NextKind
    {
        /// <summary>Move to a different source.</summary>
        Advance,

        /// <summary>Replay the current source (RepeatMode.One).</summary>
        Replay,

        /// <summary>No more items — the playlist is finished.</summary>
        End,
    }

    internal readonly record struct NextDecision(NextKind Kind, IMediaSource? Source, bool Wrapped);

    /// <summary>
    /// Decides what plays after the item that just ended. Called by the session
    /// under its own transition gate; only the coordinator's own fields are
    /// guarded here.
    /// </summary>
    /// <param name="current">The source that just finished (used for replay).</param>
    /// <remarks>
    /// <b>Same-source replay.</b> The decision is <see cref="NextKind.Replay"/>
    /// whenever the next item to play is the <i>same source object</i> that just
    /// finished — explicitly under <see cref="RepeatMode.One"/>, and implicitly
    /// when a <see cref="RepeatMode.All"/> queue wraps onto a single distinct clip
    /// (the canonical signage attract/panel loop) or hits a back-to-back duplicate.
    /// Surfacing those as <c>Replay</c> lets the session reuse the live decode
    /// runtime in place (a cheap rewind — no teardown, no decode-device change, no
    /// presenter rebind) instead of rebuilding it, which is what makes the
    /// single-clip loop seam gapless. The <see cref="NextDecision.Wrapped"/> flag
    /// still rides along so the transition report is unchanged.
    /// </remarks>
    internal NextDecision DecideNext(IMediaSource? current)
    {
        lock (_gate)
        {
            if (_repeat == RepeatMode.One && current is not null)
                return new NextDecision(NextKind.Replay, current, Wrapped: false);

            var wrapped = false;
            if (_upcoming.Count == 0)
            {
                if (_repeat == RepeatMode.All && _loopBuffer.Count > 0)
                {
                    foreach (var s in _loopBuffer)
                        _upcoming.AddLast(s);
                    _loopBuffer.Clear();
                    wrapped = true;
                }
                else
                {
                    return new NextDecision(NextKind.End, null, Wrapped: false);
                }
            }

            var next = _upcoming.First!.Value;
            _upcoming.RemoveFirst();
            if (_repeat == RepeatMode.All)
                _loopBuffer.Add(next);

            // When the next item IS the source that just finished (a single-clip
            // All-loop wrap, or any back-to-back duplicate), the live runtime can be
            // reused in place rather than torn down and rebuilt. Reference identity is
            // the right test: only the exact same source object is guaranteed to share
            // the open demuxer, decoders, decode device, and warm presenter binding.
            if (ReferenceEquals(next, current))
                return new NextDecision(NextKind.Replay, next, wrapped);

            return new NextDecision(NextKind.Advance, next, wrapped);
        }
    }

    /// <summary>
    /// Records the now-current item and fires <see cref="SourceTransitioned"/>.
    /// The notification is raised outside the lock so a subscriber can call back
    /// into the coordinator without re-entrancy risk.
    /// </summary>
    internal void ReportCurrent(IMediaSource source, MediaInfo? info, bool wrapped)
    {
        int index;
        lock (_gate)
        {
            _current = source;
            _currentInfo = info;
            _currentDuration = info?.Duration ?? TimeSpan.Zero;
            index = ++_transitionCount;
        }

        if (info is not null)
            _transitioned.OnNext(new PlaylistTransition(source, info, index, wrapped));
    }

    /// <summary>Disposes the transition subject. Called by the owning player wrapper.</summary>
    internal void Dispose() => _transitioned.Dispose();
}
