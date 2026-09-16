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
/// unbounded one keeps it alive until the graph token fires. A secondary held on
/// <see cref="MaxLead"/> is released at primary EOS and discarded with the rest.
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
public sealed class SyncJoinNode<TPrimary, TSecondary, TOut> : IPumpableNode, IRequiresEveryInput
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

    /// <summary>
    /// How far ahead of the primary the window may hold a secondary, or
    /// <see langword="null"/> for no limit. Past it, the join stops reading the
    /// secondary edge until the primary catches up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Window"/> bounds retention behind the primary and nothing else bounds it
    /// ahead. Without a lead, every secondary that arrives before the primary reaches its time
    /// is retained. A live camera joined as the secondary of a paused primary pins every frame
    /// it captures, which exhausts a decoder or hardware frame pool (#90).
    /// </para>
    /// <para>
    /// With a lead, a secondary whose <c>From</c> is more than this past the highest primary
    /// time seen is held rather than admitted, and the join stops reading the edge until the
    /// primary advances. The edge's overflow policy decides what upstream does:
    /// <see cref="EdgeOptions.Buffered(int)"/> makes the producer wait, and a dropping edge
    /// discards. Before any primary has arrived, the lead is measured from the earliest
    /// secondary retained. Primary EOS, <see cref="ResetWindow"/> and graph teardown each
    /// release a held secondary.
    /// </para>
    /// <para>
    /// <b>A producer made to wait must not also feed the primary.</b> If one pump produces both
    /// sides and its secondary runs further ahead than the lead, the held edge blocks that
    /// pump, the primary stops arriving, and nothing releases the edge. Captions transcribed
    /// from audio and joined to video from the same demux pump can have this shape. Set the
    /// lead above the furthest the secondary can run ahead, or give the secondary edge a
    /// dropping policy.
    /// </para>
    /// <para>
    /// A held secondary is admitted by the join's secondary reader once the primary comes
    /// within the lead of it. Set the lead well above the primary's item interval, so an entry
    /// becomes admissible several primary items before it can match. With a lead of about one
    /// interval, a primary can reach a held entry's time before the reader has admitted it,
    /// and matches without it.
    /// </para>
    /// </remarks>
    public TimeSpan? MaxLead { get; }

    public InputPort<TPrimary> Primary { get; }
    public InputPort<TSecondary> Secondary { get; }

    // A join reads both sides before it emits, so a graph that wired only one of them runs and
    // produces nothing. GraphTopology refuses that before the run starts.
    IEnumerable<IWireableInput> IRequiresEveryInput.RequiredInputs => [Primary, Secondary];
    public OutputPort<TOut> Output { get; }

    /// <summary>Secondaries currently held by the window. Diagnostics and tests.</summary>
    public int RetainedCount => _retained.Count;

    /// <summary>
    /// Whether the join is holding a secondary it has read but not admitted, because it leads
    /// the primary by more than <see cref="MaxLead"/>. While this is true the join is not
    /// reading the secondary edge. Diagnostics and tests.
    /// </summary>
    public bool IsSecondaryHeld => _retained.HasLeadWaiter;

    public SyncJoinNode(
        string id,
        SyncJoinOperator<TPrimary, TSecondary, TOut> body,
        SyncJoinKeys<TPrimary, TSecondary> keys,
        SyncMatch matchPolicy,
        TimeSpan window,
        TimeSpan? maxStaleness = null,
        FailureResponse onError = FailureResponse.Propagate,
        TimeSpan? maxLead = null
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.Zero);
        if (maxLead is { } lead)
            ArgumentOutOfRangeException.ThrowIfLessThan(lead, TimeSpan.Zero, nameof(maxLead));

        Id = id;
        Body = body;
        Keys = keys;
        MatchPolicy = matchPolicy;
        Window = window;
        MaxStaleness = maxStaleness ?? TimeSpan.MaxValue;
        MaxLead = maxLead;
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
    /// <para>
    /// It drops what the window has admitted and nothing upstream of it. Secondaries
    /// still on the edge were produced before the reset and are admitted after it, and
    /// so is one held on <see cref="MaxLead"/>, which the join has read but not
    /// admitted. A consumer that needs nothing from before its discontinuity to reach
    /// the join discards upstream too, as a graph rebuild does.
    /// </para>
    /// <para>
    /// <see cref="FrameFlow.Graph"/> sits below <c>FrameFlow.Decoding</c> and so
    /// cannot implement its <c>ISeekResettable</c> without inverting the
    /// layering. The session registers an adapter over this method instead. See
    /// the sync-window-join ADR §6.
    /// </para>
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

    // Whether a primary has been matched since the last Clear. Kept apart from
    // _highWater, whose TimeSpan.MinValue floor is also a legal primary time.
    private bool _primarySeen;

    // Set while the secondary reader is parked on the lead bound. Completed and cleared by
    // anything that can make room: the primary advancing, or the window being cleared.
    private TaskCompletionSource? _leadWaiter;

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public bool HasLeadWaiter
    {
        get
        {
            lock (_gate)
                return _leadWaiter is not null;
        }
    }

    /// <summary>
    /// Takes ownership of <paramref name="item"/> and files it by
    /// <paramref name="from"/>, unless <paramref name="maxLead"/> is set and the
    /// item leads by more than it. Entries usually arrive in order, so the
    /// insertion point is found by scanning back from the end.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the item was admitted. Otherwise the item is
    /// still the caller's, and the task completes when room may have opened; the
    /// caller tries again then.
    /// </returns>
    public Task? TryAdmit(T item, TimeSpan from, TimeSpan to, TimeSpan? maxLead)
    {
        lock (_gate)
        {
            if (maxLead is { } lead && !HasRoom(from, lead))
            {
                _leadWaiter ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                return _leadWaiter.Task;
            }

            int i = _entries.Count;
            while (i > 0 && _entries[i - 1].From > from)
                i--;
            _entries.Insert(i, new Entry(from, to, item));
            return null;
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
            if (!_primarySeen || t > _highWater)
            {
                _primarySeen = true;
                if (t > _highWater)
                    _highWater = t;
                ReleaseLeadWaiter();
            }

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
            _primarySeen = false;
            ReleaseLeadWaiter();
        }
    }

    // Caller holds _gate. The lead is measured from the primary once one has been
    // seen. Before that it is measured from the earliest retained entry, so a
    // primary gated from the start bounds the window too. An empty window always
    // has room.
    private bool HasRoom(TimeSpan from, TimeSpan lead)
    {
        TimeSpan reference;
        if (_primarySeen)
            reference = _highWater;
        else if (_entries.Count > 0)
            reference = _entries[0].From;
        else
            return true;

        // reference + lead overflows near TimeSpan.MaxValue, and nothing can lead
        // that far anyway.
        return reference > TimeSpan.MaxValue - lead || from <= reference + lead;
    }

    // Caller holds _gate.
    private void ReleaseLeadWaiter()
    {
        _leadWaiter?.TrySetResult();
        _leadWaiter = null;
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
