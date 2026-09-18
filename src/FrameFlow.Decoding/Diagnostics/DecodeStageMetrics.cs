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
/// methods — so the reservoir needs no lock; <see cref="Snapshot"/> reads it
/// from another thread and may observe a sample mid-write, which is acceptable
/// for a diagnostic and never throws.
/// </para>
/// <para>
/// Companion to <see cref="DecodePoolMetrics"/>, which measures what a
/// GPU-resident path costs rather than what it saves.
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
        long n = Interlocked.Increment(ref counter);
        reservoir[(int)((n - 1) % ReservoirSize)] = elapsedTicks;
    }

    /// <summary>Clears both reservoirs and their counts.</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _transferCount, 0);
        Interlocked.Exchange(ref _convertCount, 0);
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
            Summarize(TransferTicks, Interlocked.Read(ref _transferCount)),
            Summarize(ConvertTicks, Interlocked.Read(ref _convertCount))
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
