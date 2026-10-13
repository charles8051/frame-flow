// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph.Core;

/// <summary>
/// What an item says about the timeline of the stream it arrived on, measured against the
/// <see cref="TimelineFollower"/> of that stream (ADR-0084, decision 5).
/// </summary>
internal enum TimelineStep
{
    /// <summary>
    /// The item names no timeline, or the follower has not seen one yet. The consumer keeps the
    /// rule it had before timelines, such as one keyed on presentation time.
    /// </summary>
    Untracked,

    /// <summary>
    /// The item is on the timeline being followed. Its presentation time continues the stream's,
    /// so a step back in that time is not a break.
    /// </summary>
    Continues,

    /// <summary>The item starts a new timeline. The consumer resets, then takes it.</summary>
    NewTimeline,
}

/// <summary>Classifies an item against a <see cref="TimelineFollower"/>. Pure.</summary>
internal static class TimelineSteps
{
    /// <summary>Where an item on <paramref name="item"/> stands against <paramref name="follower"/>.</summary>
    public static TimelineStep Step(this TimelineFollower follower, TimelineId item)
    {
        if (follower.StartsNewTimeline(item))
            return TimelineStep.NewTimeline;

        return item == TimelineId.None || follower.Current == TimelineId.None
            ? TimelineStep.Untracked
            : TimelineStep.Continues;
    }
}
