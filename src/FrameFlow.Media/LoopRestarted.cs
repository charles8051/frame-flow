// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// Event data emitted when the current item is back at its start after it played to its end. It is
/// not emitted for a skip, a jump, or a rebuild after a failure.
/// </summary>
/// <param name="LoopCount">
/// The 1-based loop count. A playlist counts consecutive loops of the current item, and counts from 1
/// again after any other start. A single-source player counts every loop it has run.
/// </param>
/// <param name="ItemDuration">The duration of the item that looped.</param>
public sealed record LoopRestarted(int LoopCount, TimeSpan ItemDuration)
{
    /// <summary>
    /// The timeline the loop presents under. A loop starts a new timeline, so the first frame
    /// presented after the item is back at its start carries this one in
    /// <see cref="FramePresentedInfo.Timeline"/>, and no frame before the loop does
    /// (ADR-0084, decision 7). <see cref="TimelineId.None"/> when the player does not say.
    /// </summary>
    public TimelineId Timeline { get; init; }
}
