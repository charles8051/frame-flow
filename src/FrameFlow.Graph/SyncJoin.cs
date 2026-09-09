// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// How a <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}"/> decides which
/// retained secondary corresponds to a primary item's media time.
/// </summary>
public enum SyncMatch
{
    /// <summary>
    /// The secondary whose interval contains the primary's time
    /// (<c>From &lt;= t &lt; To</c>). For secondaries that carry a real
    /// duration — captions, subtitle cues, chapter marks.
    /// </summary>
    /// <remarks>
    /// A zero-width interval never matches under this policy, which is why
    /// point-valued secondaries (a detection stamped with the frame PTS it ran
    /// on) use <see cref="MostRecentAtOrBefore"/> instead.
    /// </remarks>
    Within,

    /// <summary>
    /// The newest secondary whose interval starts at or before the primary's
    /// time, subject to
    /// <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}.MaxStaleness"/>.
    /// For point-valued secondaries — detections, gate state, diagnostics
    /// aggregates, control parameters.
    /// </summary>
    MostRecentAtOrBefore,
}

/// <summary>
/// Extracts media time from each side of a
/// <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Delegates rather than an <c>ITimestamped</c> constraint, following the
/// per-edge cloner precedent in ADR-0054. <see cref="IFrame"/> and
/// <see cref="IAudioBuffer"/> both carry <c>Timestamp</c>, but
/// <c>FrameFlow.Whisper.CaptionRef</c> does not — its time lives on the wrapped
/// <c>Caption</c> record — and <see cref="RefBox{T}"/> carries none at all. A
/// constraint would force public type changes on consumers to buy nothing the
/// delegate doesn't already give.
/// </para>
/// <para>
/// A point-valued secondary supplies <c>(t, t)</c>.
/// </para>
/// </remarks>
/// <param name="PrimaryTime">Media time of a primary item.</param>
/// <param name="SecondaryInterval">Media-time span a secondary item covers.</param>
public sealed record SyncJoinKeys<TPrimary, TSecondary>(
    Func<TPrimary, TimeSpan> PrimaryTime,
    Func<TSecondary, (TimeSpan From, TimeSpan To)> SecondaryInterval
)
    where TPrimary : class, IRefCounted
    where TSecondary : class, IRefCounted;

/// <summary>
/// Join operator contract: a primary item plus the secondary that corresponds
/// to it in media time, or <see langword="null"/> when nothing matched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The substrate holds a ref on both arguments across the
/// call and disposes them when it returns or throws. The operator disposes
/// neither. Returning <see langword="null"/> drops the primary.
/// </para>
/// <para>
/// <b>Returning an input forwards it.</b> A body may return
/// <paramref name="primary"/> or <paramref name="secondary"/> as its output;
/// the substrate detects that and forwards the same ref downstream rather than
/// releasing it. Any other return value must be freshly built or
/// <c>AddRef</c>'d, because both inputs are released once the body returns.
/// </para>
/// <para>
/// <b>No match emits.</b> <paramref name="secondary"/> being
/// <see langword="null"/> is an ordinary case, not a failure. A caption overlay
/// renders the bare frame; a gate emits <c>closed</c>. Dropping the primary
/// instead would blank a video branch for the seconds before the first
/// secondary arrives.
/// </para>
/// </remarks>
public delegate ValueTask<TOut?> SyncJoinOperator<in TPrimary, in TSecondary, TOut>(
    TPrimary primary,
    TSecondary? secondary,
    CancellationToken ct
)
    where TPrimary : class, IRefCounted
    where TSecondary : class, IRefCounted
    where TOut : class, IRefCounted;

/// <summary>
/// Two-input node that correlates a slow secondary stream onto a fast primary
/// stream by media time. The primary sets the cadence; the secondary is a
/// lookup over a retention window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cadence.</b> Every primary item fires the body exactly once, paired with
/// the secondary selected by <see cref="MatchPolicy"/> or with
/// <see langword="null"/>. Secondary arrivals never fire the body on their own;
/// they only update the window.
/// </para>
/// <para>
/// <b>The window is secondary retention, not primary buffering.</b> A primary
/// item is never held waiting for a secondary that has not arrived — it is
/// matched against what the window already holds and emitted immediately.
/// <see cref="SyncMatch.Within"/> therefore requires the secondary to *lead*
/// the primary, which is what an upstream lookahead buffer (ADR-0047)
/// establishes. Consumers whose secondary lags use
/// <see cref="SyncMatch.MostRecentAtOrBefore"/>.
/// </para>
/// <para>
/// <b>Termination.</b> Primary EOS stops the join emitting, but the pump keeps
/// reading the secondary and discarding what arrives. It has to: a secondary
/// upstream holding more than the edge's free capacity blocks in
/// <c>WriteAsync</c> forever if nobody drains it, and the graph would never
/// complete. A finite secondary therefore ends this pump on its own; an
/// unbounded one keeps it alive until the graph token fires.
/// </para>
/// <para>
/// The pump does not cancel the graph on clean primary EOS. The join sits on
/// one branch of a wider topology and its primary ending says nothing about the
/// others — in a media graph, video EOS is not audio EOS.
/// </para>
/// <para>
/// Secondary EOS does not end the join. Retained entries stay matchable until
/// they age out of the window.
/// </para>
/// </remarks>
public sealed class SyncJoinNode<TPrimary, TSecondary, TOut> : IPumpableNode
    where TPrimary : class, IRefCounted
    where TSecondary : class, IRefCounted
    where TOut : class, IRefCounted
{
    private readonly SecondaryWindow<TSecondary> _retained = new();

    public string Id { get; }
    public FailureResponse OnError { get; }
    public SyncJoinOperator<TPrimary, TSecondary, TOut> Body { get; }
    public SyncJoinKeys<TPrimary, TSecondary> Keys { get; }
    public SyncMatch MatchPolicy { get; }

    /// <summary>
    /// How far back the window retains secondaries. An entry is evicted once
    /// its <c>To</c> falls more than this behind the highest primary time seen.
    /// </summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// Under <see cref="SyncMatch.MostRecentAtOrBefore"/>, the oldest a match
    /// may be, measured from the secondary's <c>From</c> to the primary's time.
    /// Ignored by <see cref="SyncMatch.Within"/>.
    /// </summary>
    public TimeSpan MaxStaleness { get; }

    public InputPort<TPrimary> Primary { get; }
    public InputPort<TSecondary> Secondary { get; }
    public OutputPort<TOut> Output { get; }

    /// <summary>Secondaries currently held by the window. Diagnostics and tests.</summary>
    public int RetainedCount => _retained.Count;

    public SyncJoinNode(
        string id,
        SyncJoinOperator<TPrimary, TSecondary, TOut> body,
        SyncJoinKeys<TPrimary, TSecondary> keys,
        SyncMatch matchPolicy,
        TimeSpan window,
        TimeSpan? maxStaleness = null,
        FailureResponse onError = FailureResponse.Propagate
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.Zero);

        Id = id;
        Body = body;
        Keys = keys;
        MatchPolicy = matchPolicy;
        Window = window;
        MaxStaleness = maxStaleness ?? TimeSpan.MaxValue;
        OnError = onError;
        Primary = new InputPort<TPrimary>(this, "primary");
        Secondary = new InputPort<TSecondary>(this, "secondary");
        Output = new OutputPort<TOut>(this, "output");
    }

    /// <summary>
    /// Drops every retained secondary. Called on the seek path: the window is
    /// pre-seek state and nothing in it correlates to post-seek primaries.
    /// </summary>
    /// <remarks>
    /// <see cref="FrameFlow.Graph"/> sits below <c>FrameFlow.Decoding</c> and so
    /// cannot implement its <c>ISeekResettable</c> without inverting the
    /// layering. The session registers an adapter over this method instead. See
    /// the sync-window-join ADR §6.
    /// </remarks>
    public void ResetWindow() => _retained.Clear();

    internal SecondaryWindow<TSecondary> Retained => _retained;

    Task IPumpableNode.RunPumpAsync(CancellationTokenSource graphCts) =>
        NodePumps.PumpSyncJoinAsync(this, graphCts);
}

/// <summary>
/// Ordered, evicting store of retained secondary items. Generalises
/// <c>FrameFlow.Whisper.CaptionTimeline</c>'s eviction into the substrate.
/// </summary>
/// <remarks>
/// The window owns one ref per retained entry and disposes it on eviction or
/// <see cref="Clear"/>. <see cref="AdvanceAndMatch"/> hands back a *fresh* ref
/// for the caller to dispose.
/// </remarks>
internal sealed class SecondaryWindow<T>
    where T : class, IRefCounted
{
    private readonly record struct Entry(TimeSpan From, TimeSpan To, T Item);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private TimeSpan _highWater = TimeSpan.MinValue;

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>
    /// Takes ownership of <paramref name="item"/> and files it by
    /// <paramref name="from"/>. Entries usually arrive in order, so the
    /// insertion point is found by scanning back from the end.
    /// </summary>
    public void Admit(T item, TimeSpan from, TimeSpan to)
    {
        lock (_gate)
        {
            int i = _entries.Count;
            while (i > 0 && _entries[i - 1].From > from)
                i--;
            _entries.Insert(i, new Entry(from, to, item));
        }
    }

    /// <summary>
    /// Advances the high-water mark to <paramref name="t"/>, evicts entries
    /// that have aged out of <paramref name="window"/>, and returns an AddRef'd
    /// match for <paramref name="t"/> under <paramref name="policy"/>, or
    /// <see langword="null"/>.
    /// </summary>
    public T? AdvanceAndMatch(
        TimeSpan t,
        SyncMatch policy,
        TimeSpan window,
        TimeSpan maxStaleness
    )
    {
        lock (_gate)
        {
            if (t > _highWater)
                _highWater = t;

            Evict(window);

            // Entries ascend by From, so the last hit scanning backwards is the
            // newest qualifying one.
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.From > t)
                    continue;

                bool hit = policy switch
                {
                    // Half-open so a zero-width interval never matches.
                    SyncMatch.Within => t < e.To,
                    SyncMatch.MostRecentAtOrBefore =>
                        maxStaleness == TimeSpan.MaxValue || t - e.From <= maxStaleness,
                    _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
                };

                if (hit)
                    return (T)e.Item.AddRef();

                // Within: an earlier entry may still cover t, so keep scanning.
                // MostRecentAtOrBefore: everything earlier is staler still.
                if (policy == SyncMatch.MostRecentAtOrBefore)
                    return null;
            }

            return null;
        }
    }

    /// <summary>Disposes and drops every retained entry.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var e in _entries)
                e.Item.Dispose();
            _entries.Clear();
            _highWater = TimeSpan.MinValue;
        }
    }

    // Caller holds _gate.
    private void Evict(TimeSpan window)
    {
        if (_highWater == TimeSpan.MinValue)
            return;

        // TimeSpan.MinValue - window would overflow; nothing has aged out yet
        // when the high-water mark is still near the floor.
        if (_highWater < TimeSpan.MinValue + window)
            return;

        var cutoff = _highWater - window;

        // Scan the whole list rather than stopping at the first survivor.
        // Entries are ordered by From, not by To, so one long or open-ended
        // interval admitted early otherwise shields every later entry whose To
        // has aged out — under Within a long span would pin per-second cues
        // behind it for its whole duration, and under MostRecentAtOrBefore a
        // shielded entry stays matchable past Window, making this property's
        // documented meaning false. The list is window-bounded, so one pass
        // and a single compaction is cheap.
        int keep = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.To < cutoff)
            {
                e.Item.Dispose();
                continue;
            }
            _entries[keep++] = e;
        }
        if (keep < _entries.Count)
            _entries.RemoveRange(keep, _entries.Count - keep);
    }
}
