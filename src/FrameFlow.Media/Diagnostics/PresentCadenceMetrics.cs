// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FrameFlow.Media.Diagnostics;

/// <summary>
/// Process-wide <b>delivery-cadence</b> telemetry: the interval between
/// successive frames a <i>real</i> video sink accepts for presentation.
/// </summary>
/// <remarks>
/// <para>
/// Recorded at the sink boundary that actually feeds the on-screen surface
/// (<c>AvaloniaVideoSink</c> / <c>CompositionInteropVideoSink</c>), <b>not</b>
/// at any upstream pacing decorator. That makes it measure the user-visible
/// smoothness <i>identically</i> regardless of whether pacing is the in-graph
/// <c>PaceUntil</c> operator or the presenter-side <c>ClockSelectVideoSink</c>
/// — both ultimately call the same real sink at clock cadence, so this is the
/// one uniform A/B comparison point for the pacing rework (perf survey §A1).
/// A tight distribution = smooth delivery; a long tail / high variance =
/// judder.
/// </para>
/// <para>
/// <b>Single active sink.</b> The cadence is tracked in one process-wide
/// timestamp, which is exactly right for single-window playback (a signage
/// path and the A/B harness). A multi-sink fan-out would interleave
/// several sinks' calls here; that path is not what this metric is for.
/// </para>
/// <para>
/// Scrape with <c>dotnet-counters</c> via the <c>FrameFlow.Present</c> meter;
/// the histogram surfaces as p50/p95/p99 of the inter-present interval.
/// </para>
/// </remarks>
public static class PresentCadenceMetrics
{
    private static readonly Meter Meter = new("FrameFlow.Present", "1.0.0");

    private static readonly Histogram<double> IntervalMs = Meter.CreateHistogram<double>(
        "frameflow.present.interval_ms",
        unit: "ms",
        description: "Interval between successive frames accepted by the real video sink (delivery cadence)."
    );

    // Stopwatch ticks of the previous accepted frame. 0 = none yet.
    private static long _lastTimestamp;

    /// <summary>
    /// Records one frame accepted by a real video sink for presentation. Call
    /// once per <c>PresentAsync</c> on the concrete sink (never on a pacing
    /// decorator), so the recorded interval reflects on-screen delivery cadence.
    /// </summary>
    public static void RecordPresent()
    {
        long now = Stopwatch.GetTimestamp();
        long prev = Interlocked.Exchange(ref _lastTimestamp, now);
        if (prev != 0)
        {
            double ms = (now - prev) * 1000.0 / Stopwatch.Frequency;
            IntervalMs.Record(ms);
        }
    }
}
