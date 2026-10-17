// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FrameFlow.Playback.Diagnostics;

/// <summary>
/// Process-wide timing of the playlist session's awaited steps: opening, warming up, playing,
/// promoting a prepared item and disposing one (#780). A switch is a sequence of these steps, so
/// their durations split its time into stages.
/// </summary>
/// <remarks>
/// Internal for the reason <see cref="LoopStallMetrics"/> is: a consumer contracts with the meter
/// name, the instrument name and its <c>step</c> tag, which a <see cref="MeterListener"/> or
/// <c>dotnet-counters</c> reads without this type.
/// </remarks>
internal static class PlaylistStageMetrics
{
    private static readonly Meter Meter = new("FrameFlow.Playback", "1.0.0");

    private static readonly Histogram<double> StepDurationMs = Meter.CreateHistogram<double>(
        "frameflow.playlist.step_duration_ms",
        unit: "ms",
        description: "Wall-clock time of one awaited playlist session step, tagged by step: open, warm_up, play, "
            + "pause, seek, rewind, promote or dispose. An item switch is a run of these steps."
    );

    /// <summary>Records one step that started at <paramref name="startTimestamp"/>.</summary>
    public static void Record(string step, long startTimestamp) =>
        StepDurationMs.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("step", step));
}
