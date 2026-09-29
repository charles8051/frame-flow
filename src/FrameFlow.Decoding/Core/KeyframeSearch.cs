// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Core;

/// <summary>What one packet of the searched stream, read after a probe's seek, says.</summary>
internal enum KeyframeScan
{
    /// <summary>Nothing yet: read the next packet.</summary>
    ReadOn,

    /// <summary>A keyframe presented at or before the position, which decoding can start from.</summary>
    Found,

    /// <summary>A packet presented after the position, so no keyframe after it is at or before it.</summary>
    Passed,
}

/// <summary>
/// The search for a seek from which decoding a stream reaches a keyframe at or before a
/// position (#483).
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
/// A container that seeks to keyframes lands on one at the first probe, and seeking to that
/// probe again lands on it again.
/// </para>
/// </remarks>
/// <param name="Position">The position decoding has to reach from a keyframe.</param>
/// <param name="Probe">Where this probe seeks.</param>
/// <param name="Step">How much further back the next probe seeks when this one finds nothing.</param>
internal readonly record struct KeyframeSearch(TimeSpan Position, TimeSpan Probe, TimeSpan Step)
{
    private static readonly TimeSpan FirstStep = TimeSpan.FromSeconds(1);

    /// <summary>The search for <paramref name="position"/>, whose first probe seeks to it.</summary>
    public static KeyframeSearch For(TimeSpan position) => new(position, position, FirstStep);

    /// <summary>What a packet of the stream, read after seeking to <see cref="Probe"/>, says.</summary>
    /// <param name="isKeyframe">Whether the packet is a keyframe.</param>
    /// <param name="presentation">Its presentation time, or null when it has none.</param>
    public KeyframeScan Scan(bool isKeyframe, TimeSpan? presentation) =>
        presentation switch
        {
            null => KeyframeScan.ReadOn,
            { } pts when pts > Position => KeyframeScan.Passed,
            _ when isKeyframe => KeyframeScan.Found,
            _ => KeyframeScan.ReadOn,
        };

    /// <summary>Where to seek to decode from a keyframe this probe found.</summary>
    /// <param name="landedOnIt">
    /// Whether it was the first packet of the stream the probe read. The same seek lands on it
    /// again, which is the one answer that holds on every container.
    /// </param>
    /// <param name="decodeTime">Its decode time, or null when it has none.</param>
    /// <returns>
    /// This probe when the seek landed on the keyframe or it has no decode time; otherwise its
    /// decode time. A container that lands off keyframes, as this one did, searches by decode
    /// time, so that seek lands on the keyframe or just before it. Its presentation time could
    /// land after it, on a frame that presents before it and decodes after it.
    /// </returns>
    public TimeSpan SeekFor(bool landedOnIt, TimeSpan? decodeTime) =>
        !landedOnIt && decodeTime is { } dts ? dts : Probe;

    /// <summary>
    /// The next probe after this one found no keyframe, or null when this one was at the
    /// source's start, which is then where decoding begins.
    /// </summary>
    public KeyframeSearch? Back() =>
        Probe <= TimeSpan.Zero
            ? null
            : this with
            {
                Probe = Probe > Step ? Probe - Step : TimeSpan.Zero,
                Step = Step < TimeSpan.MaxValue / 2 ? Step * 2 : TimeSpan.MaxValue,
            };
}
