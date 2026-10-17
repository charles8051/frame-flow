// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FrameFlow.Decoding.Diagnostics;

/// <summary>
/// Process-wide timing of a video decoder's creation and first frames, split into stages (#784).
/// </summary>
/// <remarks>
/// <para>
/// The stages, tagged <c>stage</c>:
/// <list type="bullet">
/// <item><c>device</c>: borrowing the shared hardware device, or creating one.</item>
/// <item><c>codec_open</c>: <c>avcodec_open2</c>.</item>
/// <item><c>first_packet</c>: the first <c>avcodec_send_packet</c>. On a hardware decoder this is
/// where FFmpeg's <c>get_format</c> creates the frame pool and the driver's decoder object.</item>
/// <item><c>first_frame</c>: from the end of the first send to the first frame received.</item>
/// <item><c>preroll</c>: from a run's first decoded frame to its first frame at or past the run's
/// start, the decode from the keyframe that the pacer refuses.</item>
/// </list>
/// </para>
/// <para>
/// Internal for the reason <c>PlaylistStageMetrics</c> is: a consumer contracts with the meter
/// name, the instrument name and its tag, which a <see cref="MeterListener"/> or
/// <c>dotnet-counters</c> reads without this type.
/// </para>
/// </remarks>
internal static class DecoderWarmupMetrics
{
    private static readonly Meter Meter = new("FrameFlow.Decoding", "1.0.0");

    private static readonly Histogram<double> StageDurationMs = Meter.CreateHistogram<double>(
        "frameflow.decoder.warmup_stage_ms",
        unit: "ms",
        description: "Wall-clock time of one stage of a video decoder's start, tagged by stage: device, "
            + "codec_open, first_packet, first_frame or preroll."
    );

    public const string Device = "device";
    public const string CodecOpen = "codec_open";
    public const string FirstPacket = "first_packet";
    public const string FirstFrame = "first_frame";
    public const string Preroll = "preroll";

    /// <summary>Records one stage that started at <paramref name="startTimestamp"/>.</summary>
    public static void Record(string stage, long startTimestamp)
    {
        if (!StageDurationMs.Enabled)
            return;
        StageDurationMs.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", stage));
    }
}
