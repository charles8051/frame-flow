// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;

namespace FrameFlow.Decoding.Diagnostics;

/// <summary>
/// Process-wide telemetry for <b>hardware decode-texture pool occupancy</b> —
/// the count of live <see cref="GpuVideoFrame"/> leases, each of which pins one
/// slice of FFmpeg's (small, default-sized) hwframe pool.
/// </summary>
/// <remarks>
/// <para>
/// This is the <i>direct</i> measure of the mechanism the perf survey (§A1)
/// blamed for playback choppiness: a pacing path that holds a decoded frame across
/// a clock wait keeps its decode-texture slice pinned, so under load the
/// outstanding-lease count climbs toward the pool ceiling and the decoder
/// stalls waiting for a slice to return. A pacing change that <i>drops</i> late
/// frames returns their slices at once, so the count stays low. Making the
/// occupancy observable lets a pacing change be A/B'd on the cause, not just the
/// symptom — the telemetry gap the survey flagged.
/// </para>
/// <para>
/// A lease is acquired exactly once per pinned pool surface — in
/// <see cref="GpuVideoFrame.FromOwnedAvFrame"/>, the single factory that takes
/// ownership of a cloned <c>AVFrame</c> — and released at the frame's final
/// ref-count drop (<c>av_frame_free</c>). <c>AddRef</c> shares the same surface
/// and is deliberately <b>not</b> counted, so the gauge reflects distinct
/// pinned slices, not consumer references.
/// </para>
/// <para>
/// Scrape with <c>dotnet-counters</c> via the <c>FrameFlow.Decoding</c> meter.
/// </para>
/// </remarks>
public static class DecodePoolMetrics
{
    private static readonly Meter Meter = new("FrameFlow.Decoding", "1.0.0");

    private static int _outstanding; // live decode-texture leases (pinned pool slices)
    private static int _highWater;   // peak outstanding since process start

    static DecodePoolMetrics()
    {
        Meter.CreateObservableGauge(
            "frameflow.decoding.gpu_frames_outstanding",
            () => Volatile.Read(ref _outstanding),
            unit: "{frames}",
            description: "Live hardware decode-texture leases (pinned hwframe-pool slices). Climbs toward the pool ceiling under the held-lease decoder stall."
        );
        Meter.CreateObservableGauge(
            "frameflow.decoding.gpu_frames_outstanding_max",
            () => Volatile.Read(ref _highWater),
            unit: "{frames}",
            description: "Peak outstanding hardware decode-texture leases since process start."
        );
    }

    /// <summary>
    /// Records that one hwframe-pool slice was pinned (a <see cref="GpuVideoFrame"/>
    /// took ownership of a cloned <c>AVFrame</c>). Pairs 1:1 with
    /// <see cref="OnLeaseReleased"/>.
    /// </summary>
    public static void OnLeaseAcquired()
    {
        int cur = Interlocked.Increment(ref _outstanding);

        // Lock-free high-water update.
        int hw;
        while (cur > (hw = Volatile.Read(ref _highWater)))
        {
            if (Interlocked.CompareExchange(ref _highWater, cur, hw) == hw)
                break;
        }
    }

    /// <summary>
    /// Records that a pinned slice was returned to the pool (a
    /// <see cref="GpuVideoFrame"/>'s final release ran <c>av_frame_free</c>).
    /// </summary>
    public static void OnLeaseReleased() => Interlocked.Decrement(ref _outstanding);
}
