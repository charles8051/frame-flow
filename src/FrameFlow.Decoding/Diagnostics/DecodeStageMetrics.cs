// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Diagnostics;

/// <summary>
/// Opt-in per-stage timing for the decoder's internal readback path — the
/// <c>av_hwframe_transfer_data</c> download and the <c>sws_scale</c> colour
/// conversion that <see cref="VideoDecoder"/> runs when a hardware-decoded
/// frame is delivered to a CPU consumer.
/// </summary>
/// <remarks>
/// <para>
/// These two stages are the cost that <see cref="VideoDecoder.YieldHardwareFrames"/>
/// exists to make optional, and nothing measured them. The download is the
/// PCIe traffic; the conversion is the CPU work that turns the downloaded NV12
/// into the BGRA every CPU-side operator expects. A consumer deciding whether
/// a GPU-resident path is worth building needs the two separated, because only
/// the first is bandwidth and only the second scales with core speed.
/// </para>
/// <para>
/// <b>Off by default.</b> When <see cref="Enabled"/> is <see langword="false"/>
/// the decode path pays one relaxed bool read per frame and records nothing.
/// Recording is single-writer — the decode worker calls both <c>Record</c>
/// methods under the codec lock — so the reservoir needs no lock. A sample is
/// written to its slot before the count that makes it visible, so a
/// <see cref="Snapshot"/> from another thread never reads a slot the writer has
/// not filled; the most it can miss is the frame in flight.
/// </para>
/// <para>
/// <b>Process-wide, like <see cref="DecodePoolMetrics"/>.</b> Both collectors
/// are static, so every decoder in the process contributes to one snapshot and
/// a run cannot attribute a sample to a particular stream. That is the right
/// shape for the question these answer — what the readback costs on this
/// machine — and it keeps a diagnostic reachable without threading a collector
/// through decoder construction. A measurement session calls
/// <see cref="Reset"/> at its start so a second run in the same process does
/// not inherit the first one's samples.
/// </para>
/// </remarks>
public static class DecodeStageMetrics
{
    /// <summary>Samples retained per stage for percentile reporting.</summary>
    private const int ReservoirSize = 4096;

    private static bool _enabled;

    private static readonly long[] TransferTicks = new long[ReservoirSize];
    private static readonly long[] ConvertTicks = new long[ReservoirSize];

    private static long _transferCount;
    private static long _convertCount;

    /// <summary>
    /// Turns recording on. Set before decoding starts; a decoder already
    /// running observes the change on its next frame.
    /// </summary>
    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set => Volatile.Write(ref _enabled, value);
    }

    /// <summary>
    /// Records one <c>av_hwframe_transfer_data</c> call, in
    /// <see cref="System.Diagnostics.Stopwatch"/> ticks.
    /// </summary>
    public static void RecordHardwareTransfer(long elapsedTicks) =>
        Record(TransferTicks, ref _transferCount, elapsedTicks);

    /// <summary>
    /// Records one colour-convert-and-copy pass (<c>sws_scale</c> into a pooled
    /// BGRA buffer), in <see cref="System.Diagnostics.Stopwatch"/> ticks.
    /// </summary>
    public static void RecordColorConvert(long elapsedTicks) =>
        Record(ConvertTicks, ref _convertCount, elapsedTicks);

    private static void Record(long[] reservoir, ref long counter, long elapsedTicks)
    {
        // Write the sample, then publish the count. The reverse order lets a
        // concurrent Snapshot see a slot it believes was written and read the
        // previous lap's value — or a zero on the first lap, which lands in the
        // middle of a percentile and is indistinguishable from a real
        // measurement. Both Record calls come from the decode worker under
        // VideoDecoder's codec lock, so the read of `counter` here is
        // single-writer and needs no interlock.
        long n = counter;
        reservoir[(int)(n % ReservoirSize)] = elapsedTicks;
        Volatile.Write(ref counter, n + 1);
    }

    /// <summary>
    /// Clears both reservoirs and their counts. Call it when a measurement
    /// session starts: the collector is process-wide, so without it a second
    /// run in the same process reports the first run's samples too.
    /// </summary>
    public static void Reset()
    {
        Volatile.Write(ref _transferCount, 0);
        Volatile.Write(ref _convertCount, 0);
        Array.Clear(TransferTicks);
        Array.Clear(ConvertTicks);
    }

    /// <summary>
    /// Percentiles over the retained samples. Returns zeroed stages when
    /// nothing was recorded — a hardware transfer count of zero means the
    /// decoder never ran the download, which is itself the answer when
    /// hardware decode did not engage.
    /// </summary>
    public static DecodeStageSnapshot Snapshot() =>
        new(
            Summarize(TransferTicks, Volatile.Read(ref _transferCount)),
            Summarize(ConvertTicks, Volatile.Read(ref _convertCount))
        );

    private static DecodeStageSummary Summarize(long[] reservoir, long total)
    {
        if (total == 0)
            return new DecodeStageSummary(0, 0, 0, 0);

        int retained = (int)Math.Min(total, ReservoirSize);
        var sorted = new long[retained];
        Array.Copy(reservoir, sorted, retained);
        Array.Sort(sorted);

        return new DecodeStageSummary(
            total,
            ToMs(sorted[retained / 2]),
            ToMs(sorted[(int)(retained * 0.95)]),
            ToMs(sorted[retained - 1])
        );
    }

    private static double ToMs(long ticks) =>
        ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}

/// <summary>One stage's retained-sample percentiles, in milliseconds.</summary>
public sealed class DecodeStageSummary(long count, double p50Ms, double p95Ms, double maxMs)
{
    /// <summary>Calls recorded since the last <see cref="DecodeStageMetrics.Reset"/>.</summary>
    public long Count { get; } = count;

    /// <summary>Median of the retained samples.</summary>
    public double P50Ms { get; } = p50Ms;

    /// <summary>95th percentile of the retained samples.</summary>
    public double P95Ms { get; } = p95Ms;

    /// <summary>Slowest retained sample.</summary>
    public double MaxMs { get; } = maxMs;
}

/// <summary>Both readback stages, captured together.</summary>
public sealed class DecodeStageSnapshot(
    DecodeStageSummary hardwareTransfer,
    DecodeStageSummary colorConvert
)
{
    /// <summary>The <c>av_hwframe_transfer_data</c> download.</summary>
    public DecodeStageSummary HardwareTransfer { get; } = hardwareTransfer;

    /// <summary>The <c>sws_scale</c> NV12 to BGRA pass.</summary>
    public DecodeStageSummary ColorConvert { get; } = colorConvert;
}
