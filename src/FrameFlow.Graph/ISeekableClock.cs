// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// An <see cref="IClockSource"/> whose timeline origin can be discontinuously
/// reseated to a known position. The seek orchestrator calls
/// <see cref="SeekBaseline"/> with the seek <em>target</em> so the master clock's
/// origin agrees with the post-seek frame PTS — rather than the clock author
/// re-discovering its origin from whatever the first post-seek sample/buffer
/// happens to carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> When an audio sink masters the clock, its origin was
/// previously captured from the first buffer arriving after a (re)activation.
/// On the seek path a stale pre-seek buffer leaking through the flush — or an
/// audio stream seeking to a different keyframe boundary than video — anchored
/// that origin off the seek target. The pacing operator (<c>PaceUntil</c>) then
/// either waited (effectively) forever for the clock to reach the frame PTS
/// (frozen video) or returned instantly for every frame (choppy fast-forward).
/// Reseating the origin to the known seek target removes that whole failure mode.
/// </para>
/// <para>
/// <b>Contract.</b> After <see cref="SeekBaseline"/>, <see cref="IClockSource.Latest"/>
/// reports a value at or near <paramref name="position"/> until the clock advances
/// from there. Implementations must apply the reseat atomically with respect to
/// their read surface and must tolerate being called while inactive/paused (the
/// new origin takes effect on the next activation/resume). Idempotent for repeated
/// calls with the same position.
/// </para>
/// </remarks>
public interface ISeekableClock : IClockSource
{
    /// <summary>
    /// Reseats the clock's timeline origin to <paramref name="position"/> (the seek
    /// target), so subsequent <see cref="IClockSource.Latest"/> reads and
    /// <see cref="IClockSource.WaitUntilAsync"/> waits are measured from there.
    /// </summary>
    /// <param name="position">The seek target the post-seek frame PTS are aligned to.</param>
    void SeekBaseline(TimeSpan position);
}
