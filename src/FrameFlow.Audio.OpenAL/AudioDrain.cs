// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Audio.OpenAL;

/// <summary>
/// The execution state of an OpenAL source, all four of them, as the sink sees it. The drain
/// needs the split <see cref="AlSourceState"/> folds away: a source that has never been started
/// and one that is playing call for opposite actions at the end of a stream.
/// </summary>
internal enum DrainSourceState
{
    /// <summary><c>AL_INITIAL</c>, on a sink that is not paused: buffers may be queued, nothing has played.</summary>
    Initial,

    /// <summary><c>AL_PLAYING</c>.</summary>
    Playing,

    /// <summary>
    /// The sink is paused: <c>AL_PAUSED</c>, or whatever state the pause found, since pausing a
    /// source that has not started leaves it in <c>AL_INITIAL</c>. Nothing plays until something
    /// resumes it.
    /// </summary>
    Paused,

    /// <summary><c>AL_STOPPED</c>: it played its queue to the end, or was stopped.</summary>
    Stopped,
}

/// <summary>What the shell reads from the device on one pass of the drain loop.</summary>
/// <param name="Staged">Whether the sink still holds samples it has not handed the device.</param>
/// <param name="QueuedBuffers">The source's <c>AL_BUFFERS_QUEUED</c>.</param>
/// <param name="State">The source's execution state.</param>
/// <param name="PlayedSamples">
/// How far the device has played this activation, in samples per channel: the samples already
/// credited to the clock plus the source's <c>AL_SAMPLE_OFFSET</c>. It only grows while the
/// device plays, whoever recycles buffers in between, which is what makes it a progress measure.
/// </param>
/// <param name="Now">Time since the drain began, on the sink's time provider.</param>
internal readonly record struct DrainObservation(
    bool Staged,
    int QueuedBuffers,
    DrainSourceState State,
    long PlayedSamples,
    TimeSpan Now
);

/// <summary>What the shell does after one observation.</summary>
internal enum DrainStep
{
    /// <summary>The device holds nothing it has not played. The drain is over.</summary>
    Complete,

    /// <summary>
    /// The source holds audio and was never started, because the stream ended before the queue
    /// reached the pre-buffer depth. Start it, then keep watching.
    /// </summary>
    StartSource,

    /// <summary>Audio is still playing, or the source is paused. Look again after a slice.</summary>
    Wait,

    /// <summary>
    /// The source says it is playing, but the device has played nothing for the stall limit.
    /// Give up, so a device that stopped consuming cannot hold the caller forever.
    /// </summary>
    Stalled,
}

/// <summary>Result of <see cref="AudioDrain.Observe"/>: the next state and the step to take.</summary>
internal readonly record struct DrainOutcome(AudioDrain Next, DrainStep Step);

/// <summary>
/// The pure core of <see cref="OpenAlAudioSink.DrainAsync"/>: decides, from one reading of the
/// device, whether the end of the stream has been heard, whether the source needs starting, or
/// whether the device has stopped making progress.
/// </summary>
/// <remarks>
/// <para>
/// <b>Done is the device's verdict, not the sink's.</b> The last
/// <see cref="OpenAlAudioSink.PresentAsync"/> leaves up to a pool of buffers queued ahead of the
/// play cursor, and a partial block in staging. The drain is over when nothing is staged and the
/// source has either let go of every buffer or stopped. OpenAL 1.1 §4.3.5: a playing source
/// enters <c>AL_STOPPED</c> when it completes the last buffer in its queue.
/// </para>
/// <para>
/// <b>A source that never started.</b> The sink starts a source only once the queue reaches its
/// pre-buffer depth. A stream shorter than that, or the re-prime after an underrun at the very
/// end, leaves buffers on a source in <c>AL_INITIAL</c>, which would wait for the next buffer
/// forever. At the end of the stream there is no next buffer, so the drain starts it.
/// </para>
/// <para>
/// <b>Bounded by progress, not by a deadline.</b> The limit runs from the last time the device
/// moved, so a long queue is never cut short, and it does not run while the source is paused: a
/// pause is the caller holding playback, not the device failing. A dead endpoint is no stall
/// either, because OpenAL Soft keeps reporting buffers processed over it (#127), so its drain
/// completes.
/// </para>
/// </remarks>
internal readonly record struct AudioDrain
{
    private AudioDrain(TimeSpan stallLimit, long lastPlayed, TimeSpan lastProgressAt)
    {
        StallLimit = stallLimit;
        LastPlayed = lastPlayed;
        LastProgressAt = lastProgressAt;
    }

    /// <summary>How long the device may make no progress while playing before the drain gives up.</summary>
    public TimeSpan StallLimit { get; }

    /// <summary>The played position at the last observation that showed progress, or -1 before the first.</summary>
    public long LastPlayed { get; }

    /// <summary>When the device last made progress, or when it was paused, or when the drain began.</summary>
    public TimeSpan LastProgressAt { get; }

    /// <summary>A drain that has observed nothing yet.</summary>
    /// <param name="stallLimit">How long the device may stand still while playing.</param>
    /// <param name="now">The time the drain begins, on the clock later observations use.</param>
    public static AudioDrain Begin(TimeSpan stallLimit, TimeSpan now)
    {
        if (stallLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stallLimit), stallLimit, "The stall limit must be positive.");
        return new AudioDrain(stallLimit, lastPlayed: -1, lastProgressAt: now);
    }

    /// <summary>Folds one reading of the device into the drain and says what to do next.</summary>
    public DrainOutcome Observe(DrainObservation seen)
    {
        // Nothing staged and nothing queued: there is nothing left anywhere to play.
        if (!seen.Staged && seen.QueuedBuffers == 0)
            return new DrainOutcome(this, DrainStep.Complete);

        switch (seen.State)
        {
            case DrainSourceState.Stopped when !seen.Staged:
                // Played to the end of its queue. Staging that is still pending is uploaded by
                // the shell's next flush, which re-primes a stopped source first.
                return new DrainOutcome(this, DrainStep.Complete);

            case DrainSourceState.Initial when seen.QueuedBuffers > 0:
                // Not progress: if starting it does not take, the stall limit still runs.
                return new DrainOutcome(this, StalledBy(seen.Now) ? DrainStep.Stalled : DrainStep.StartSource);

            case DrainSourceState.Paused:
                // Held by the caller. The stall limit starts again from the resume.
                return new DrainOutcome(Progressed(seen), DrainStep.Wait);
        }

        if (seen.PlayedSamples > LastPlayed)
            return new DrainOutcome(Progressed(seen), DrainStep.Wait);

        return new DrainOutcome(this, StalledBy(seen.Now) ? DrainStep.Stalled : DrainStep.Wait);
    }

    private bool StalledBy(TimeSpan now) => now - LastProgressAt >= StallLimit;

    private AudioDrain Progressed(DrainObservation seen) =>
        new(StallLimit, Math.Max(LastPlayed, seen.PlayedSamples), seen.Now);
}
