// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Core;

/// <summary>
/// The search for a seek from which decoding a stream reaches the nearest keyframe at or before a
/// position (#483, #495).
/// </summary>
/// <remarks>
/// <para>
/// A container that seeks by timestamp alone, such as MPEG-TS, lands on whichever packet is
/// nearest, and a decoder handed a packet that is not a keyframe drops frames until the next
/// keyframe, which can be past the position. So each probe seeks to <see cref="Probe"/> and
/// reads the stream's packets from where it lands until one presents after
/// <see cref="Position"/>, or the source ends. Keyframes present in decode order, so no keyframe
/// after that packet is at or before the position. The last keyframe the probe read that is,
/// is the one to decode from. A probe that reads none is followed by one further back, twice as
/// far each time, down to the source's start.
/// </para>
/// <para>
/// The first probe seeks to the position itself. When the first packet of the stream it lands on
/// is a keyframe at or before the position, the search ends there: a seek lands on the last packet
/// it can at or before the position, and a keyframe presents no earlier than it decodes, so no
/// later keyframe presents at or before the position. A container that seeks to keyframes ends
/// the search after one packet.
/// </para>
/// <para>
/// Each later probe stops where the probe before it landed, since that one read on from there
/// and found no keyframe at or before the position. So a search reads the packets between where
/// its last probe lands and the position once, and reads to the source's end at most once: when
/// the stream ends before the position, only the first probe does.
/// </para>
/// <para>
/// A probe that lands on a keyframe is sought again by the same probe, which lands on it again.
/// A probe that reaches the source's end without reading a packet of the stream that has a
/// presentation time has nothing to place a keyframe by, and the search lands where a seek to the
/// position does.
/// </para>
/// </remarks>
/// <param name="Position">The position decoding has to reach from a keyframe.</param>
/// <param name="Probe">Where this probe seeks.</param>
/// <param name="Step">How much further back the next probe seeks when this one finds nothing.</param>
internal readonly record struct KeyframeSearch(TimeSpan Position, TimeSpan Probe, TimeSpan Step)
{
    private static readonly TimeSpan FirstStep = TimeSpan.FromSeconds(1);

    /// <summary>Whether this probe has read a packet of the stream since its seek.</summary>
    public bool Landed { get; private init; }

    /// <summary>Whether this probe has read a packet of the stream that has a presentation time.</summary>
    public bool Timed { get; private init; }

    /// <summary>
    /// The decode time of the first packet of the stream this probe read that has one, or null
    /// when it has read none.
    /// </summary>
    public TimeSpan? LandedAt { get; private init; }

    /// <summary>
    /// The decode time from which earlier probes read the stream's packets and found no keyframe
    /// at or before the position, or null on the first probe.
    /// </summary>
    public TimeSpan? ScannedFrom { get; private init; }

    /// <summary>
    /// Whether this probe has read enough: a packet presented after the position, a packet an
    /// earlier probe read, or, on the first probe, a keyframe at or before the position where it
    /// landed.
    /// </summary>
    public bool Finished { get; private init; }

    /// <summary>
    /// Where to seek to decode from the last keyframe at or before the position that this probe
    /// read, or null when it has read none.
    /// </summary>
    public TimeSpan? Found { get; private init; }

    /// <summary>
    /// Where to seek once this probe has read its packets: the keyframe it found; the position
    /// when the source ended before a packet of the stream with a presentation time; or the
    /// source's start when a probe from there found none. Null when the search goes on with
    /// <see cref="Back"/>.
    /// </summary>
    public TimeSpan? Landing =>
        Found
        ?? (
            !Finished && !Timed ? Position
            : Probe <= TimeSpan.Zero ? Probe
            : null
        );

    /// <summary>The search for <paramref name="position"/>, whose first probe seeks to it.</summary>
    public static KeyframeSearch For(TimeSpan position) => new(position, position, FirstStep);

    /// <summary>
    /// The search after one more packet of the stream, read after seeking to <see cref="Probe"/>.
    /// </summary>
    /// <param name="isKeyframe">Whether the packet is a keyframe.</param>
    /// <param name="presentation">Its presentation time, or null when it has none.</param>
    /// <param name="decode">
    /// Its decode time, or null when it has none. A keyframe read after the landing is sought by
    /// it: a container that lands off keyframes, as this one did, searches by decode time, so
    /// that seek lands on the keyframe or just before it. Its presentation time could land after
    /// it, on a frame that presents before it and decodes after it.
    /// </param>
    public KeyframeSearch Read(bool isKeyframe, TimeSpan? presentation, TimeSpan? decode)
    {
        if (Finished)
            return this;

        // False while either is null: a packet with no decode time is read like any other.
        if (decode >= ScannedFrom)
            return this with { Landed = true, Finished = true };

        var landing = !Landed;
        var read = this with
        {
            Landed = true,
            Timed = Timed || presentation is not null,
            LandedAt = LandedAt ?? decode,
        };
        return presentation switch
        {
            null => read,
            { } pts when pts > Position => read with { Finished = true },
            _ when !isKeyframe => read,
            _ when landing => read with { Found = Probe, Finished = IsFirstProbe },
            _ => read with { Found = decode ?? Probe },
        };
    }

    /// <summary>
    /// The next probe, after this one read its packets and <see cref="Landing"/> is null: one
    /// <see cref="Step"/> further back, never before the source's start, with the step doubled.
    /// It stops where this one landed.
    /// </summary>
    public KeyframeSearch Back() =>
        new(
            Position,
            Probe > Step ? Probe - Step : TimeSpan.Zero,
            Step < TimeSpan.MaxValue / 2 ? Step * 2 : TimeSpan.MaxValue
        )
        {
            ScannedFrom = LandedAt ?? ScannedFrom,
        };

    /// <summary>The first probe is the only one that seeks to the position.</summary>
    private bool IsFirstProbe => Probe == Position;
}
