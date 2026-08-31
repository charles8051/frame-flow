// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>
/// Sizes the video packet queue so it holds more <i>time</i> than the audio queue, which is
/// the invariant the shared demux pump depends on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this exists to prevent.</b> Both packet queues were bounded at 512 packets. A
/// packet is a different amount of content on every stream: 512 AAC packets at 1024 samples /
/// 48 kHz is ~10.9 s, but 512 video packets at 60 fps is only ~8.5 s.
/// </para>
/// <para>
/// Two changes restore the ordering. Audio's queue drops to 128 packets — it is the pump's
/// throttle, and ~11 s of read-ahead is far more than playback needs — which lowers the bar
/// video must clear from ~13 s to ~3 s. Video's is then derived from the stream's frame rate,
/// so it scales with the content instead of being a fixed count that means a different
/// duration on every stream.
/// </para>
/// <para>
/// Audio blocks the pump when its queue is full; video drops the newest packet instead,
/// precisely so a slow video chain cannot wedge the pump and freeze the audio clock
/// (ADR-0060). That is safe only while <b>audio is the stream that fills first</b>. At 60 fps
/// it was not, so video's last-resort drop path became the routine outcome and the pump shed
/// continuously.
/// </para>
/// <para>
/// The damage was not a slow pipeline but a stalling one. The decoder trails the pump by a
/// queue's worth of packets, so each shed burst became a multi-second hole in the decoded
/// timeline; <c>ClockSelectVideoSink</c> then found its earliest buffered frame 2.6–3.4 s
/// ahead of the master clock and correctly waited it out. Averaged across those freezes the
/// pipeline read as ~42 fps while actually running at a clean 60 with multi-second gaps
/// (#145).
/// </para>
/// <para>
/// <b>Why capacity and not a per-packet time bound.</b> The obvious fix is to bound each
/// queue by the presentation span it holds. That needs per-packet timestamp tracking, and
/// timestamps make it a much harder problem than it looks: packets arrive in decode order
/// carrying presentation stamps, so the newest PTS routinely moves backwards on B-frames;
/// a consumed high-water mark does not describe the oldest packet still queued; streams
/// contain discontinuities that are not reordering; the arithmetic can overflow on arbitrary
/// stream timestamps; and the markers have to be reset in lockstep with the decode worker
/// across seeks. Every one of those is a way to silently disable the bound or, worse, park
/// the producer on a queue that will never drain.
/// </para>
/// <para>
/// None of it is necessary. Video's packet rate is its frame rate, known from stream metadata
/// before a single packet is read, so the capacity can be sized once at construction and the
/// queues stay ordinary bounded channels with no per-packet state, no shared mutable
/// watermarks, and nothing to reset.
/// </para>
/// </remarks>
public static class ReadAheadCapacity
{
    /// <summary>
    /// Video read-ahead target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bar it has to clear is the audio queue's span, now 128 packets — ~2.7 s of AAC
    /// (1024 samples @ 48 kHz), ~3.3 s of MP3 (1152 @ 44.1 kHz), ~2.6 s of 20 ms Opus. Twelve
    /// seconds clears the worst of those by more than 3x.
    /// </para>
    /// <para>
    /// That margin matters because this converts a frame rate into a packet count, and the two
    /// are not the same thing: <c>avg_frame_rate</c> is an average over the stream rather than
    /// a guarantee for any interval, and packetization does not always give one packet per
    /// displayed frame. Sizing to more than twice the bar keeps the ordering even on a stream
    /// producing packets at twice its average rate, and the <see cref="MinCapacity"/> floor
    /// means a stream we cannot measure at all still holds 512 packets — more time than the
    /// audio queue at any frame rate up to ~150 fps.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultVideoReadAhead = TimeSpan.FromSeconds(12);

    /// <summary>Floor, so a stream with no usable frame rate is never worse than before.</summary>
    public const int MinCapacity = 512;

    /// <summary>
    /// Ceiling. These are references to already-read packets, so the cost is the compressed
    /// bytes they hold — a few MB at broadcast rates — but an absurd declared frame rate
    /// should not turn into an unbounded queue.
    /// </summary>
    public const int MaxCapacity = 4096;

    /// <summary>
    /// Packet capacity for a video queue that should hold <paramref name="readAhead"/> of
    /// content at the stream's frame rate.
    /// </summary>
    /// <param name="frameRateNum">Stream <c>avg_frame_rate</c> numerator.</param>
    /// <param name="frameRateDen">Stream <c>avg_frame_rate</c> denominator.</param>
    /// <param name="readAhead">Target duration.</param>
    /// <returns>
    /// The capacity, clamped to <see cref="MinCapacity"/>..<see cref="MaxCapacity"/>.
    /// A frame rate that is missing, zero or nonsensical yields <see cref="MinCapacity"/> —
    /// the value that shipped — so an unmeasurable stream degrades to the old behaviour
    /// rather than to something new and untested.
    /// </returns>
    public static int ForVideo(int frameRateNum, int frameRateDen, TimeSpan readAhead)
    {
        if (frameRateNum <= 0 || frameRateDen <= 0 || readAhead <= TimeSpan.Zero)
            return MinCapacity;

        var fps = frameRateNum / (double)frameRateDen;
        if (double.IsNaN(fps) || double.IsInfinity(fps) || fps <= 0)
            return MinCapacity;

        var packets = Math.Ceiling(fps * readAhead.TotalSeconds);
        if (packets <= MinCapacity)
            return MinCapacity;

        return packets >= MaxCapacity ? MaxCapacity : (int)packets;
    }
}
