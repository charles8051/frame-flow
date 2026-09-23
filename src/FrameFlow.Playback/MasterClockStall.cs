// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// The threaded state of a master-clock liveness probe: the highest reading seen so far,
/// and how many consecutive observations have failed to beat it.
/// </summary>
/// <param name="HighWaterTicks">
/// The highest clock reading observed, in <see cref="TimeSpan.Ticks"/>. A reading below it
/// does not lower it: a clock that goes backwards has been reseated, which is not progress
/// and must not read as progress on the next observation either.
/// </param>
/// <param name="ConsecutiveStalls">Observations since the last one that advanced.</param>
internal readonly record struct MasterClockProbe(long HighWaterTicks, int ConsecutiveStalls)
{
    /// <summary>The probe's starting state, seeded with the clock's current reading.</summary>
    public static MasterClockProbe From(TimeSpan reading) => new(reading.Ticks, 0);
}

/// <summary>
/// Result of <see cref="MasterClockStall.Observe"/>: the threaded next state, and whether
/// the master has stopped as of this observation.
/// </summary>
internal readonly record struct MasterClockProbeOutcome(MasterClockProbe Next, bool Stopped);

/// <summary>
/// Pure detector for a master clock that has <b>stopped</b>, as distinct from one that is
/// merely slow (#359). Sibling of <see cref="LoopStallEvaluator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> <see cref="ClockSelectVideoSink"/>'s end-of-content hold waits for
/// the master clock to reach the last frame's <c>Pts + Duration</c> before it drains and
/// lets end-of-stream fire, so the final frame gets its full display interval. When the
/// master is the audio sink, that target can be past the end of the audio: AAC carries
/// encoder delay and padding, so a clip's decoded audio is a little shorter than its video,
/// and the audio clock stops at its own end. The hold then waits out its whole cap. Measured
/// on a 2s clip: video's last frame ends at 2.000, the audio clock stops at ~1.97, and
/// <c>Ended</c> arrives 5.2 seconds after the picture did — five seconds of stillness at the
/// end of a clip, and five seconds between items on a playlist.
/// </para>
/// <para>
/// <b>Why a liveness probe rather than a smaller cap.</b> The cap has to stay generous
/// because a slow master must not be cut off mid-display — that is the defect the hold
/// exists to prevent (#249), and shrinking the cap trades one for the other. The two cases
/// are distinguishable without trading anything: a slow master still advances, a stopped one
/// does not. So the bound stays where it was, as a backstop, and this ends the hold early
/// only when the clock has demonstrably stopped moving.
/// </para>
/// <para>
/// <b>The consecutive-stall requirement.</b> One non-advancing reading is not enough, and
/// neither are two. The window has to clear two different things. A master publishes on its
/// own cadence — the OpenAL sink's device value moves once per mixing period, 20 ms on the
/// measured device — so a short window can see no change on a perfectly healthy clock purely
/// from where the samples landed. And a healthy master can stop publishing <i>transiently</i>
/// under load and then resume; declaring it stopped truncates the last frame's display, which
/// is the failure the hold exists to prevent.
/// </para>
/// <para>
/// So the window is sized against the worst of those rather than the best: at the caller's
/// slice it spans half a second, an order of magnitude over the device period, and any single
/// advance resets it. A master that stalls and resumes is never called stopped, however many
/// times it does it, because the count only survives consecutive misses.
/// </para>
/// <para>
/// A paused master does not advance either, so the caller does not observe while paused; the
/// clock is stopped because the user stopped it, and the hold is uncapped there (#127).
/// </para>
/// </remarks>
internal static class MasterClockStall
{
    /// <summary>
    /// Consecutive non-advancing observations before the master counts as stopped. At
    /// <see cref="ClockSelectVideoSink.HoldProbeSlice"/> this is a window of half a second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sized to outlast both the publish cadence of the masters in the tree and a transient
    /// stall in one of them, not to detect a stop as early as possible. The audio sink
    /// interpolates between device updates with elapsed wall time, so its <c>Latest</c>
    /// advances continuously while the device is running and half a second of no movement is
    /// not a sampling artifact; the wallclock master always advances.
    /// </para>
    /// <para>
    /// It buys latency, not correctness, and the trade is lopsided: the cost of waiting
    /// longer is a slightly later <c>Ended</c>, and the cost of deciding too early is a
    /// truncated final frame. The backstop cap still bounds the hold if this never trips.
    /// </para>
    /// </remarks>
    internal const int DefaultStallsBeforeStopped = 5;

    /// <summary>
    /// Folds one clock reading into the probe.
    /// </summary>
    /// <param name="prior">The state from the previous observation, or <see cref="MasterClockProbe.From"/>.</param>
    /// <param name="reading">The clock's value now.</param>
    /// <param name="stallsBeforeStopped">
    /// How many consecutive non-advancing observations mean stopped. A value below one makes
    /// the first non-advancing reading decisive.
    /// </param>
    public static MasterClockProbeOutcome Observe(
        MasterClockProbe prior,
        TimeSpan reading,
        int stallsBeforeStopped = DefaultStallsBeforeStopped
    )
    {
        bool advanced = reading.Ticks > prior.HighWaterTicks;
        int stalls = advanced ? 0 : prior.ConsecutiveStalls + 1;
        long highWater = advanced ? reading.Ticks : prior.HighWaterTicks;

        return new MasterClockProbeOutcome(
            new MasterClockProbe(highWater, stalls),
            Stopped: !advanced && stalls >= stallsBeforeStopped
        );
    }
}
