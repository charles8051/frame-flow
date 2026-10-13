// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph.Core;

/// <summary>
/// Follows the timeline of a stream that keeps the player's order, and says when an item starts a
/// new one (ADR-0084, decision 5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only for a stream in the player's order.</b> That is a consumer inside the player's graphs,
/// at its sinks, or behind one first-in, first-out path fed from inside one graph at a time. There,
/// an item of a timeline already left cannot arrive after the next timeline's first item, so any id
/// other than the current one starts a new timeline, and the follower keeps no history. A consumer
/// behind a path that can reorder items, or one matching derived items against what is shown, uses
/// <see cref="PresentedTimeline"/> instead.
/// </para>
/// <para>
/// <b>None is current.</b> An item on <see cref="TimelineId.None"/> belongs to the timeline being
/// followed, and so does every item while the follower has seen no id yet. The first id it sees is
/// adopted without starting a new timeline. A consumer fed by a source that carries no ids keeps the
/// behaviour it had before timelines.
/// </para>
/// <para>
/// A value: <see cref="Follow"/> returns the next follower and changes nothing. The default value is
/// a follower that has seen nothing. One follower tracks one lineage, so a consumer with two
/// independent inputs keeps one per input.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// if (_follower.StartsNewTimeline(item.Timeline))
///     ResetState();
/// _follower = _follower.Follow(item.Timeline);
/// </code>
/// </example>
public readonly record struct TimelineFollower
{
    private TimelineFollower(TimelineId current) => Current = current;

    /// <summary>
    /// The timeline being followed, or <see cref="TimelineId.None"/> before the first item that
    /// names one.
    /// </summary>
    public TimelineId Current { get; }

    /// <summary>
    /// Whether an item on <paramref name="item"/> starts a new timeline: it names one, the follower
    /// is following one, and they differ. The consumer resets, then takes the item.
    /// </summary>
    public bool StartsNewTimeline(TimelineId item) =>
        item != TimelineId.None && Current != TimelineId.None && item != Current;

    /// <summary>
    /// The follower after an item on <paramref name="item"/>: following that timeline, or this one
    /// when the item names none.
    /// </summary>
    public TimelineFollower Follow(TimelineId item) =>
        item == TimelineId.None || item == Current ? this : new TimelineFollower(item);
}
