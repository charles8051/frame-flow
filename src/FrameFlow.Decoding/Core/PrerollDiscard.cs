// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Core;

/// <summary>
/// What a decoder may skip while it decodes from a keyframe up to the start of a run (#784). Pure.
/// </summary>
/// <remarks>
/// <para>
/// A run that starts past a keyframe decodes from the keyframe, and every frame presented
/// before the start is released unseen. A non-reference frame among them is decoded for nothing:
/// no other frame is built from it, so skipping it changes no frame that is kept. The packets
/// before the start are therefore sent with <see cref="DecodeDiscardLevel.NonReference"/>, and
/// the level the decoder was given goes back on for the first packet at or past the start.
/// </para>
/// <para>
/// The decision is per packet, by the packet's own presentation time, rather than switched once
/// at the first frame past the start. With B-frames, decode order is not presentation order: a
/// reference frame past the start can be decoded before a B-frame that still comes before it, and
/// that B-frame is still waste.
/// </para>
/// <para>
/// The floor is spent by the first decoded frame at or past it, as the run's own floor is spent
/// where the decoded frames are refused (<c>SubstrateMedia</c>'s run floor). A non-reference frame
/// before the floor comes before that frame in presentation order, so its packet has been sent by
/// then. After it, nothing is refused at the source, so nothing more may be skipped here.
/// </para>
/// <para>
/// It only ever adds a skip. A lateness-recovery level that already skips more stands, and a
/// stream with no non-reference frames is left alone: the level would skip nothing there.
/// </para>
/// </remarks>
internal static class PrerollDiscard
{
    /// <summary>The discard level to decode the next packet with.</summary>
    /// <param name="level">The level the decoder was given (lateness recovery's, or none).</param>
    /// <param name="floor">Where the run starts, or <see langword="null"/> once spent or when it has none.</param>
    /// <param name="packetTime">
    /// The packet's presentation time, or <see langword="null"/> when it has none. A packet of
    /// unknown time is decoded: it cannot be shown to come before the start.
    /// </param>
    /// <param name="hasNonReferenceFrames">Whether the stream can carry non-reference frames.</param>
    public static DecodeDiscardLevel For(
        DecodeDiscardLevel level,
        TimeSpan? floor,
        TimeSpan? packetTime,
        bool hasNonReferenceFrames
    )
    {
        if (!hasNonReferenceFrames || floor is not { } start || packetTime is not { } time || time >= start)
            return level;

        return level >= DecodeDiscardLevel.NonReference ? level : DecodeDiscardLevel.NonReference;
    }

    /// <summary>The floor after a frame at <paramref name="frameTime"/> was decoded.</summary>
    /// <returns><see langword="null"/> once a frame at or past the floor has been decoded.</returns>
    public static TimeSpan? After(TimeSpan? floor, TimeSpan frameTime) =>
        floor is { } start && frameTime < start ? start : null;

    /// <summary>The floor a run arms: none for a run that starts at or before zero.</summary>
    public static TimeSpan? Arm(TimeSpan start) => start > TimeSpan.Zero ? start : null;
}
