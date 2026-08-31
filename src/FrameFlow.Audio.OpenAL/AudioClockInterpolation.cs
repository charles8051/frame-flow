// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Audio.OpenAL;

/// <summary>
/// The last device-reported clock value and when it was observed. Threaded through
/// <see cref="AudioClockInterpolation.Read"/> by the sink; never mutated in place.
/// </summary>
/// <param name="RawPosition">The most recent position the device actually reported.</param>
/// <param name="ObservedAtTicks">Monotonic timestamp of that observation.</param>
/// <param name="Valid">False before any observation, and after a discontinuity.</param>
internal readonly record struct AudioClockAnchor(
    TimeSpan RawPosition,
    long ObservedAtTicks,
    TimeSpan LastPublished,
    bool Valid
)
{
    /// <summary>No observation yet: the next read anchors rather than interpolating.</summary>
    public static AudioClockAnchor None => new(TimeSpan.Zero, 0, TimeSpan.Zero, Valid: false);
}

/// <summary>
/// Smooths the audio master clock between device updates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The published position comes from <c>AL_SAMPLE_OFFSET</c>, which OpenAL Soft
/// updates once per mixing period — measured on this machine at exactly 20.00 ms, every
/// step, i.e. its default 50 Hz refresh. Everything paced against that clock therefore moves
/// in 20 ms increments, so a 60 fps source releases at 50 fps and the extra frames are
/// discarded as late by the presenter's select-by-clock. That was the residual ~10 fps in
/// issue #125 once the presenter ceiling (#128) was fixed. Asking the driver for a faster
/// refresh via <c>ALC_REFRESH</c> did not change the step; the smoothing has to be ours.
/// </para>
/// <para>
/// <b>What.</b> Between device updates the position advances by elapsed wall time, re-anchored
/// on every observed device change. The audio device remains the master exactly as ADR-0003
/// requires — this changes how its value is <i>read</i> between samples, not which clock leads.
/// Long-term position is still the device's; only the sub-period interval is filled in.
/// </para>
/// <para>
/// <b>Bounded and non-decreasing.</b> Extrapolation is capped, so a stalled device (underrun,
/// pause, teardown) makes the clock stop rather than run away — the cap is the most the clock
/// can ever lead the device by. The value never goes backwards: an interpolated reading is
/// only ever returned above its own anchor, and a device update that lands below the last
/// value returned is held rather than rewound, because a master clock that steps back makes
/// every consumer's "is this frame late?" answer wrong.
/// </para>
/// <para>
/// <b>Discontinuities</b> — seek, deactivate — invalidate the anchor rather than smoothing
/// across, so the clock jumps with the device as it should.
/// </para>
/// </remarks>
internal static class AudioClockInterpolation
{
    /// <summary>
    /// Default extrapolation ceiling: exactly the 20 ms mixing period measured on this
    /// device. Sized so the clock arrives at the next value just as the device reports it —
    /// larger would overshoot and then have to stall to stay monotonic, smaller would leave a
    /// gap the interpolation was meant to fill. A device whose period is longer simply stalls
    /// at the cap, which is honest and no worse than the un-interpolated step.
    /// </summary>
    public static readonly TimeSpan DefaultMaxExtrapolation = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Produces the position to publish, and the anchor to carry into the next read.
    /// </summary>
    /// <param name="anchor">The anchor from the previous read.</param>
    /// <param name="rawPosition">The position the device reports right now.</param>
    /// <param name="nowTicks">A monotonic timestamp for this read.</param>
    /// <param name="ticksPerSecond">Frequency of that timestamp.</param>
    /// <param name="maxExtrapolation">Ceiling on how far ahead of the device to run.</param>
    /// <param name="interpolate">
    /// False when the stream is not advancing — paused, or not yet playing — so the clock
    /// tracks the device exactly rather than creeping forward while stopped.
    /// </param>
    public static (AudioClockAnchor Anchor, TimeSpan Position) Read(
        AudioClockAnchor anchor,
        TimeSpan rawPosition,
        long nowTicks,
        long ticksPerSecond,
        TimeSpan maxExtrapolation,
        bool interpolate
    )
    {
        if (!interpolate || ticksPerSecond <= 0)
            return (new AudioClockAnchor(rawPosition, nowTicks, rawPosition, Valid: true), rawPosition);

        // First read, or a discontinuity the caller flagged: take the device value as-is,
        // including backwards. A seek must move the clock where it was told to.
        if (!anchor.Valid)
            return (new AudioClockAnchor(rawPosition, nowTicks, rawPosition, Valid: true), rawPosition);

        // The device moved: ground truth, so re-anchor — but never publish below what was
        // already published. Interpolation can lead the device by up to the cap, and a master
        // clock that steps back makes every consumer's "is this frame late?" answer wrong.
        if (rawPosition != anchor.RawPosition)
        {
            var published = rawPosition > anchor.LastPublished ? rawPosition : anchor.LastPublished;
            return (new AudioClockAnchor(rawPosition, nowTicks, published, Valid: true), published);
        }

        // The device has not moved since the anchor. Fill the gap with elapsed wall time,
        // capped. Keeping the anchor unchanged means the cap measures from the last real
        // observation rather than creeping forward one read at a time.
        var elapsedTicks = nowTicks - anchor.ObservedAtTicks;
        if (elapsedTicks <= 0)
            return (anchor, anchor.LastPublished);

        var elapsed = TimeSpan.FromSeconds((double)elapsedTicks / ticksPerSecond);
        if (elapsed > maxExtrapolation)
            elapsed = maxExtrapolation;

        var candidate = rawPosition + elapsed;
        if (candidate < anchor.LastPublished)
            candidate = anchor.LastPublished;

        return (anchor with { LastPublished = candidate }, candidate);
    }
}
