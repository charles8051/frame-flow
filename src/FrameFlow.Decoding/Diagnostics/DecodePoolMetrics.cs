// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;

namespace FrameFlow.Decoding.Diagnostics;

/// <summary>
/// Process-wide telemetry for <b>hardware decode-texture pool occupancy</b>:
/// how many <see cref="GpuVideoFrame"/> leases are live, each pinning one
/// slice of a decoder's fixed hwframe pool, and how many slices those pools hold.
/// </summary>
/// <remarks>
/// <para>
/// A pacing path that holds decoded frames keeps their slices pinned, so under load the
/// outstanding count climbs toward the pool's size. When a pool runs out, FFmpeg 9.0 fails the
/// decode call and the decoder faults (#370, reproduced in
/// <c>docs/investigations/2026-09-25-d3d11va-pool-exhaustion.md</c>). A pacing change that
/// <i>drops</i> late frames returns their slices at once, so the count stays low.
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
/// <b>Both gauges sum across decoders.</b> The outstanding count covers every decoder in the
/// process, so the capacity is the sum of the sizes of every live hardware pool, each read from
/// the first frame that came from it (<see cref="VideoDecoderDiagnosticsSnapshot.HardwarePoolSize"/>
/// carries one decoder's current pool). A pool is live while its decoder or any frame built from
/// it holds it, so a disposed decoder's pool stays counted until its last frame is released,
/// exactly as that frame stays in the outstanding count. A process running one player reads them as one pool; with several
/// players or a playlist's preroll, one pool can be exhausted while the sums look comfortable,
/// and a decoder's own snapshot is the place to look.
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
    private static int _capacity;    // summed pool sizes of live hardware decoders

    static DecodePoolMetrics()
    {
        Meter.CreateObservableGauge(
            "frameflow.decoding.gpu_frames_outstanding",
            () => Volatile.Read(ref _outstanding),
            unit: "{frames}",
            description: "Live hardware decode-texture leases (pinned hwframe-pool slices), summed across decoders. A pool that runs out fails the decode."
        );
        Meter.CreateObservableGauge(
            "frameflow.decoding.gpu_frames_outstanding_max",
            () => Volatile.Read(ref _highWater),
            unit: "{frames}",
            description: "Peak outstanding hardware decode-texture leases since process start."
        );
        Meter.CreateObservableGauge(
            "frameflow.decoding.gpu_pool_capacity",
            () => Volatile.Read(ref _capacity),
            unit: "{frames}",
            description: "Surfaces in live hwframe pools (held by a decoder or by a frame from them), summed across decoders as the outstanding gauge is. Zero when nothing decodes on hardware."
        );
    }

    /// <summary>Live decode-texture leases right now, summed across decoders.</summary>
    public static int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>
    /// Surfaces in live hardware pools right now, summed across decoders. A pool is live while
    /// its decoder or any frame built from it holds it.
    /// </summary>
    public static int Capacity => Volatile.Read(ref _capacity);

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

    /// <summary>
    /// Adds a pool's surfaces when it is first seen, and removes them, with the negated size,
    /// when nothing holds it any more.
    /// </summary>
    internal static void OnPoolCapacityChanged(int delta) => Interlocked.Add(ref _capacity, delta);
}
