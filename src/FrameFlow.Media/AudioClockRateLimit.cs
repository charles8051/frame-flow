// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// The last accepted device reading and the elapsed playing time it was accepted at.
/// Threaded through <see cref="AudioClockRateLimit.Apply"/> by the sink; never mutated
/// in place.
/// </summary>
/// <param name="Position">The position last published by the limiter.</param>
/// <param name="SessionElapsed">Elapsed playing time at that reading.</param>
/// <param name="Valid">False before the first reading, and after a discontinuity.</param>
public readonly record struct AudioClockRateAnchor(
    TimeSpan Position,
    TimeSpan SessionElapsed,
    bool Valid
)
{
    /// <summary>No reading yet: the next one is taken as-is.</summary>
    public static AudioClockRateAnchor None => new(TimeSpan.Zero, TimeSpan.Zero, Valid: false);
}

/// <summary>
/// Holds the audio master clock to the rate a playing device can actually consume at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The clock is the device's sample counter, and the sink credits that counter
/// with every buffer OpenAL reports processed. "Processed" means the device let go of the
/// buffer, not that it played it: a device that discards its queue reports the whole queue
/// processed at once, and a source that is marked started but is not producing reports
/// every new buffer processed the moment it is queued. Both make the counter advance faster
/// than sound can be heard, and the clock followed it — a 1.09&#160;s step across a pause,
/// and 209&#160;s of clock in 176&#160;ms after it (#127).
/// </para>
/// <para>
/// <b>What.</b> A playing device consumes one second of audio per second, so the counter
/// may not advance further than elapsed playing time did over the same interval. A reading
/// that exceeds it by more than <c>slack</c> is audio the device dropped rather
/// than played; the limiter publishes what real time allows and re-anchors there, so the
/// excess is discarded once rather than recovered on the next read. The device stays the
/// master whenever it is telling the truth, which is every reading that fits.
/// </para>
/// <para>
/// <b>Why the delta and not the value.</b> This guard existed once as
/// <c>min(audioTime, sessionElapsed)</c> and was removed within days, because the two
/// are different coordinates: the audio value is source-stream PTS, seated on a seek
/// target or a first buffer's PTS, while elapsed playing time restarts at zero. After a
/// seek the min clamped the clock back to ~0 and froze video for the length of the seek.
/// Comparing <i>advances</i> has no such problem — a seek moves both sides together — but
/// it does require the anchor to be dropped at every discontinuity that reseats the clock,
/// which is the same set of transitions that already drop the interpolation anchor.
/// </para>
/// <para>
/// <b>Sizing the slack.</b> The counter moves in mixing-period steps and a buffer boundary
/// can land inside any one read interval, so a reading may legitimately lead elapsed time
/// by up to a buffer. <see cref="DefaultSlack"/> is several times that, because the
/// failures this catches overshoot by whole seconds and a false positive costs more than a
/// late detection. Host-versus-device clock drift does not accumulate here: every accepted
/// reading re-anchors, so the comparison is always over one read interval.
/// </para>
/// </remarks>
public static class AudioClockRateLimit
{
    /// <summary>
    /// Default tolerance on a single reading: how far ahead of elapsed playing time the
    /// device may legitimately report before the reading is treated as dropped audio.
    /// </summary>
    public static readonly TimeSpan DefaultSlack = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Produces the position to publish, and the anchor to carry into the next read.
    /// </summary>
    /// <param name="anchor">The anchor from the previous read.</param>
    /// <param name="raw">The position derived from the device counter right now.</param>
    /// <param name="sessionElapsed">Elapsed playing time, paused intervals excluded.</param>
    /// <param name="slack">Tolerance on a single reading. See <see cref="DefaultSlack"/>.</param>
    public static (AudioClockRateAnchor Anchor, TimeSpan Position) Apply(
        AudioClockRateAnchor anchor,
        TimeSpan raw,
        TimeSpan sessionElapsed,
        TimeSpan slack
    )
    {
        // First read, or a discontinuity the caller flagged: the device is ground truth and
        // there is no interval to measure an advance over.
        if (!anchor.Valid)
            return (new AudioClockRateAnchor(raw, sessionElapsed, Valid: true), raw);

        var wall = sessionElapsed - anchor.SessionElapsed;
        if (wall < TimeSpan.Zero)
            wall = TimeSpan.Zero;

        // Within tolerance — including every reading that goes backwards or stands still,
        // which are the device's business and not this guard's.
        if (raw <= anchor.Position + wall + slack)
            return (new AudioClockRateAnchor(raw, sessionElapsed, Valid: true), raw);

        // Beyond it. Publish the most a playing device could have reached, and anchor on
        // that rather than on `raw`: anchoring on the reading would let the excess back in
        // one slack at a time, and adding the slack to the ceiling would do the same.
        var ceiling = anchor.Position + wall;
        return (new AudioClockRateAnchor(ceiling, sessionElapsed, Valid: true), ceiling);
    }
}
