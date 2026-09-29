// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// Where a demux pump stops feeding its decoders, for a run that ends at a media time (#483): a
/// cutoff for each stream the pump feeds, in that stream's own time base, and which streams have
/// passed theirs.
/// </summary>
/// <remarks>
/// <para>
/// A stream passes its cutoff at the first packet whose decode timestamp is at or after it. Every
/// frame presented before the end decodes from packets before the cutoff: a frame's decode
/// timestamp is at most its presentation timestamp, and everything it refers to decodes before
/// it. So the pump feeds nothing from a stream once it has passed, and stops reading once every
/// stream it feeds has. The frames decoded from before the cutoff that present at or after the
/// end are the consumer's to drop.
/// </para>
/// <para>
/// A packet with no decode timestamp is fed, because nothing says where it falls. A stream whose
/// packets never carry one never passes, and the pump reads it to the end of the source.
/// </para>
/// </remarks>
/// <param name="VideoCutoff">The video stream's cutoff, or null when the run has no end or the pump does not feed it.</param>
/// <param name="AudioCutoff">The audio stream's cutoff, or null when the run has no end or the pump does not feed it.</param>
/// <param name="VideoPast">Whether the video stream has passed its cutoff.</param>
/// <param name="AudioPast">Whether the audio stream has passed its cutoff.</param>
internal readonly record struct ReadBound(
    long? VideoCutoff,
    long? AudioCutoff,
    bool VideoPast = false,
    bool AudioPast = false
)
{
    /// <summary>No cutoff: every packet is fed, and the pump reads to the end of the source.</summary>
    public static ReadBound Unbounded => default;

    /// <summary>
    /// The routed outcome of the packet the pump just read, against this bound: the next bound,
    /// and the outcome the pump acts on.
    /// </summary>
    /// <param name="routed">The packet's outcome from <see cref="DemuxPump.Route"/>.</param>
    /// <param name="decodeTimestamp">The packet's decode timestamp in media time, in its stream's time base, or null when it has none.</param>
    /// <returns>
    /// <paramref name="routed"/> for a packet before its stream's cutoff, or for anything but a
    /// fed packet; <see cref="ReadOutcome.Unselected"/> for one at or after it while another fed
    /// stream has not passed; <see cref="ReadOutcome.EndOfStream"/> once every fed stream has.
    /// </returns>
    public (ReadBound Next, ReadOutcome Outcome) Offer(ReadOutcome routed, long? decodeTimestamp)
    {
        ReadBound next;
        switch (routed)
        {
            case ReadOutcome.SelectedVideo when VideoPast || Reached(decodeTimestamp, VideoCutoff):
                next = this with { VideoPast = true };
                break;
            case ReadOutcome.SelectedAudio when AudioPast || Reached(decodeTimestamp, AudioCutoff):
                next = this with { AudioPast = true };
                break;
            default:
                return (this, routed);
        }

        return (next, next.Finished ? ReadOutcome.EndOfStream : ReadOutcome.Unselected);
    }

    /// <summary>Every fed stream has passed its cutoff. A stream with no cutoff is not fed.</summary>
    private bool Finished => (VideoPast || VideoCutoff is null) && (AudioPast || AudioCutoff is null);

    private static bool Reached(long? timestamp, long? cutoff) =>
        timestamp is { } t && cutoff is { } c && t >= c;

    /// <summary>
    /// <paramref name="time"/> in a stream time base of <paramref name="timeBaseNum"/> /
    /// <paramref name="timeBaseDen"/> seconds, rounded up, so a timestamp at or after the result
    /// is at or after <paramref name="time"/>. <see cref="long.MaxValue"/>, which no timestamp
    /// reaches, for a degenerate time base or a result that does not fit: that stream is read to
    /// the end of the source.
    /// </summary>
    public static long CutoffIn(TimeSpan time, int timeBaseNum, int timeBaseDen)
    {
        if (timeBaseNum <= 0 || timeBaseDen <= 0)
            return long.MaxValue;

        Int128 numerator = (Int128)time.Ticks * timeBaseDen;
        Int128 denominator = (Int128)timeBaseNum * TimeSpan.TicksPerSecond;
        Int128 units = numerator / denominator;
        if (numerator % denominator > 0)
            units++;

        return units > long.MaxValue ? long.MaxValue : (long)units;
    }
}
