// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Names the timeline a frame or buffer belongs to: one continuous run of presentation times
/// (ADR-0084).
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity, not order.</b> Ids are compared for equality only. Every id <see cref="Next"/>
/// returns differs from every other in the process, but an id made later does not name a later
/// timeline: a playlist item prepared ahead makes its id before the item that is playing makes the
/// ids of its later loops and seeks. What is on screen gives the order, through
/// <see cref="Core.TimelineFollower"/> for a stream that keeps the player's order and
/// <see cref="Core.PresentedTimeline"/> for anything else.
/// </para>
/// <para>
/// <b>What it promises.</b> Within one id, presentation times lie on one continuous media
/// timeline. Across ids nothing about presentation times is promised.
/// </para>
/// <para>
/// <b>Scope.</b> Ids are opaque, local to the process, and not persisted. They are compared only
/// within one lineage: the items that reach one player's sinks, and anything derived from them.
/// Items from independent sources are never compared.
/// </para>
/// <para>
/// <see cref="None"/>, the default, means the producer does not say. A consumer treats an item
/// on <see cref="None"/> as belonging to whatever timeline it is following, which is how it
/// behaved before items carried an id.
/// </para>
/// </remarks>
public readonly record struct TimelineId
{
    // The last id handed out. Zero is None, so the first is 1.
    private static long _last;

    private readonly long _value;

    private TimelineId(long value) => _value = value;

    /// <summary>No timeline: the producer does not say which it belongs to.</summary>
    public static TimelineId None => default;

    /// <summary>
    /// A timeline id no other call in this process has returned, and never <see cref="None"/>.
    /// Thread-safe.
    /// </summary>
    public static TimelineId Next() => new(Interlocked.Increment(ref _last));

    /// <summary>A name for logs: <c>none</c>, or <c>timeline</c> and a number.</summary>
    /// <remarks>The number identifies the id within this process and says nothing about order.</remarks>
    public override string ToString() => _value == 0 ? "none" : $"timeline {_value}";
}
