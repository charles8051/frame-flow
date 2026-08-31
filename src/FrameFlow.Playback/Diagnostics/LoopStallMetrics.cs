// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;

namespace FrameFlow.Playback.Diagnostics;

/// <summary>
/// Process-wide telemetry for detected <b>loop stalls</b>: a
/// <c>RepeatMode.One</c> loop whose position overran the item duration without
/// a restart (frame delivery stopped while the clock kept advancing).
/// </summary>
/// <remarks>
/// <para>
/// Follows the same shape as <c>FrameFlow.Media.Diagnostics.PresentCadenceMetrics</c>
/// and the presenter-stall counter: a dedicated metrics class owning its own
/// named <see cref="Meter"/>, rather than meters scattered inline through the
/// controller. Scrape with <c>dotnet-counters</c> via the <c>FrameFlow.Playback</c>
/// meter; a non-zero, rising <c>frameflow.playback.loop_stalls</c> means a loop
/// silently died in production.
/// </para>
/// </remarks>
public static class LoopStallMetrics
{
    private static readonly Meter Meter = new("FrameFlow.Playback", "1.0.0");

    private static readonly Counter<long> LoopStalls = Meter.CreateCounter<long>(
        "frameflow.playback.loop_stalls",
        description: "Times a RepeatMode.One loop was detected stalled (position overran duration with no restart)."
    );

    /// <summary>Records one detected loop stall (call once per rising edge).</summary>
    public static void RecordLoopStall() => LoopStalls.Add(1);
}
