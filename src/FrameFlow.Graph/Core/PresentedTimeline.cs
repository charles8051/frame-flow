// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph.Core;

/// <summary>Where an item's timeline stands against the one on screen (<see cref="PresentedTimeline"/>).</summary>
public enum TimelineRelation
{
    /// <summary>On screen, or the item or the screen names no timeline. The consumer takes the item.</summary>
    Current,

    /// <summary>A timeline the screen has left. Its frames will not be shown again, so the consumer drops the item.</summary>
    Past,

    /// <summary>
    /// Neither on screen nor known to have been left. The consumer holds the item, within its own
    /// bounds, until its timeline is presented.
    /// </summary>
    Upcoming,
}

/// <summary>
/// Follows the timeline on screen, and says whether an item belongs to it, to one the screen has
/// left, or to one still to come (ADR-0084, decision 5).
/// </summary>
/// <remarks>
/// <para>
/// <b>For anything that is not in the player's order.</b> It serves derived items matched against
/// what is shown, such as inference results and captions, which can arrive before their frame is
/// presented, and any consumer behind a path that can deliver an item of one run after the next
/// run's first item. A stream that keeps the player's order can use the simpler
/// <see cref="TimelineFollower"/>.
/// </para>
/// <para>
/// <b>Order comes from presentation.</b> It is fed the timeline of each presented frame, from
/// <c>FramePresentedInfo.Timeline</c>, and keeps the current one and the few it left most
/// recently. Ids carry no order of their own (<see cref="TimelineId"/>), so a timeline that has
/// never been presented is upcoming, whenever its id was made.
/// </para>
/// <para>
/// <b>The history bounds memory, not correctness.</b> A timeline that has fallen out of the history
/// reads as upcoming again. An item of it is held until the consumer's bounds evict it, and it never
/// shows, because that timeline is never presented again.
/// </para>
/// <para>
/// <b>None is current.</b> An item on <see cref="TimelineId.None"/> is current, and so is every item
/// before a frame that names a timeline has been presented. A presented frame on
/// <see cref="TimelineId.None"/> leaves the timeline on screen where it was. A consumer fed by a
/// source that carries no ids keeps the behaviour it had before timelines.
/// </para>
/// <para>
/// Immutable: <see cref="Present"/> returns the next value and changes nothing, so one instance can
/// be read from any thread. It allocates only when the timeline on screen changes.
/// </para>
/// </remarks>
public sealed class PresentedTimeline
{
    /// <summary>How many left timelines a <see cref="PresentedTimeline"/> remembers unless told otherwise.</summary>
    public const int DefaultHistory = 16;

    // The timelines left, most recent first, never holding Current or None, and at most
    // HistoryCapacity long. Never written after construction.
    private readonly TimelineId[] _left;

    /// <summary>Nothing presented yet, remembering <see cref="DefaultHistory"/> left timelines.</summary>
    public PresentedTimeline()
        : this(DefaultHistory) { }

    /// <summary>Nothing presented yet, remembering <paramref name="history"/> left timelines.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="history"/> is less than 1.</exception>
    public PresentedTimeline(int history)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(history);
        HistoryCapacity = history;
        _left = [];
    }

    private PresentedTimeline(TimelineId current, TimelineId[] left, int history)
    {
        Current = current;
        _left = left;
        HistoryCapacity = history;
    }

    /// <summary>
    /// The timeline of the last presented frame that named one, or <see cref="TimelineId.None"/>
    /// before the first.
    /// </summary>
    public TimelineId Current { get; }

    /// <summary>How many of the timelines it has left this remembers.</summary>
    public int HistoryCapacity { get; }

    /// <summary>
    /// The timeline on screen after a frame on <paramref name="presented"/> is presented. A new
    /// timeline becomes current, and the one it replaces becomes the most recently left, pushing
    /// the oldest out once the history is full.
    /// </summary>
    public PresentedTimeline Present(TimelineId presented)
    {
        if (presented == TimelineId.None || presented == Current)
            return this;

        if (Current == TimelineId.None)
            return new PresentedTimeline(presented, _left, HistoryCapacity);

        // The one left goes first. A timeline presented again after it was left, which the
        // player's order does not produce, is current rather than past.
        var left = new List<TimelineId>(HistoryCapacity) { Current };
        foreach (var id in _left)
        {
            if (left.Count == HistoryCapacity)
                break;
            if (id != presented)
                left.Add(id);
        }

        return new PresentedTimeline(presented, [.. left], HistoryCapacity);
    }

    /// <summary>Where an item on <paramref name="item"/> stands against the timeline on screen.</summary>
    public TimelineRelation Classify(TimelineId item)
    {
        if (item == TimelineId.None || Current == TimelineId.None || item == Current)
            return TimelineRelation.Current;

        return Array.IndexOf(_left, item) >= 0 ? TimelineRelation.Past : TimelineRelation.Upcoming;
    }
}
