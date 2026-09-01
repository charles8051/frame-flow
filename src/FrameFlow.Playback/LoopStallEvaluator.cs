// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;

namespace FrameFlow.Playback;

/// <summary>
/// One sample of loop-liveness state, taken by the loop-stall watchdog in
/// <see cref="PlaybackControllerCore"/> on each position tick.
/// </summary>
/// <param name="NowTicks"><see cref="Stopwatch.GetTimestamp"/> when sampled (monotonic).</param>
/// <param name="PositionTicks">Current playback position, in <see cref="TimeSpan.Ticks"/>.</param>
/// <param name="DurationTicks">Loaded item duration, in <see cref="TimeSpan.Ticks"/> (0 if unknown).</param>
/// <param name="RepeatOne">Whether repeat mode is <c>RepeatMode.One</c> (single-item loop).</param>
/// <param name="Playing">Whether playback is actively presenting (Playing and not seeking).</param>
/// <param name="LoopCount">The controller's monotonic successful-loop-restart counter.</param>
public readonly record struct LoopStallSample(
    long NowTicks,
    long PositionTicks,
    long DurationTicks,
    bool RepeatOne,
    bool Playing,
    int LoopCount
);

/// <summary>
/// Result of <see cref="LoopStallEvaluator.Observe"/>: the threaded-through next
/// state, whether the loop is stalled as of this sample, and how long the
/// position has been past the item duration without a restart (for the log).
/// </summary>
public readonly record struct LoopStallOutcome(
    LoopStallEvaluator Next,
    bool Stalled,
    long OverrunTicks
);

/// <summary>
/// Pure detector for a <b>failed single-item loop restart</b> — the functional
/// core of the loop-stall watchdog (mirrors
/// <c>FrameFlow.Media.Diagnostics.PresenterStallEvaluator</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The signature it catches.</b> While <c>RepeatMode.One</c> is active and
/// playback is Playing, a healthy loop wraps the position back to zero at every
/// item boundary, so the position never sits past the item duration for more
/// than a wrap's worth of time. When the loop restart silently fails, frame
/// delivery stops but the (wall) clock keeps advancing, so the position climbs
/// <i>past</i> the duration and never returns — exactly the "video frozen on the
/// last frame while the seeker keeps moving" symptom. This evaluator reports a
/// stall once the position has been past the duration for longer than
/// <c>stallTimeout</c> with no increment of the loop counter.
/// </para>
/// <para>
/// <b>Scope.</b> Targets the wall-clock-mastered case, where the position keeps
/// advancing after the stall. On the audio-master clock the sample counter
/// freezes at EOS, so the position stalls <i>at</i> the duration rather than
/// overrunning it — a different signature, deliberately out of scope here.
/// </para>
/// <para>
/// <b>Why a loop-count gate.</b> Requiring the loop counter to stay unchanged
/// across the overrun window suppresses a benign transient overshoot right at a
/// boundary (the wrap completes in milliseconds, far under the timeout), and lets
/// a genuinely fast-looping sub-second clip never trip.
/// </para>
/// <para>
/// Pure and immutable — state is threaded through <see cref="LoopStallOutcome.Next"/>
/// and nothing is mutated — so the gate is exhaustively unit-testable with no
/// clock, no player, and no real timing.
/// </para>
/// </remarks>
public readonly struct LoopStallEvaluator
{
    private readonly bool _inOverrun;
    private readonly long _overrunSinceTicks; // NowTicks when the position first went past duration
    private readonly int _loopCountAtOverrun; // loop counter captured at that moment
    private readonly long _stallTimeoutTicks;

    private LoopStallEvaluator(
        bool inOverrun,
        long overrunSinceTicks,
        int loopCountAtOverrun,
        long stallTimeoutTicks
    )
    {
        _inOverrun = inOverrun;
        _overrunSinceTicks = overrunSinceTicks;
        _loopCountAtOverrun = loopCountAtOverrun;
        _stallTimeoutTicks = stallTimeoutTicks;
    }

    /// <summary>
    /// Creates an evaluator that reports a stall only after the position has been
    /// past the item duration for <paramref name="stallTimeout"/> continuously,
    /// with no loop restart in that window.
    /// </summary>
    public static LoopStallEvaluator Create(TimeSpan stallTimeout) =>
        new(
            inOverrun: false,
            overrunSinceTicks: 0,
            loopCountAtOverrun: 0,
            stallTimeoutTicks: (long)(stallTimeout.TotalSeconds * Stopwatch.Frequency)
        );

    /// <summary>Folds one <paramref name="sample"/>, returning the next state and the verdict. Mutates nothing.</summary>
    public LoopStallOutcome Observe(in LoopStallSample sample)
    {
        // Eligible only while a single-item loop is actively presenting a
        // known-duration item AND the position has actually overrun the duration.
        // Anything else closes the overrun episode (not stalled).
        bool pastEnd =
            sample.RepeatOne
            && sample.Playing
            && sample.DurationTicks > 0
            && sample.PositionTicks > sample.DurationTicks;

        if (!pastEnd)
            return new LoopStallOutcome(Reset(), Stalled: false, OverrunTicks: 0);

        // Open the overrun episode if not already in one.
        long since = _inOverrun ? _overrunSinceTicks : sample.NowTicks;
        int loopAtStart = _inOverrun ? _loopCountAtOverrun : sample.LoopCount;

        // A loop restart completed since the episode opened → healthy wrap in
        // flight; the position will drop back below duration shortly. Reset.
        if (sample.LoopCount != loopAtStart)
            return new LoopStallOutcome(Reset(), Stalled: false, OverrunTicks: 0);

        long overrun = sample.NowTicks - since;
        bool stalled = overrun >= _stallTimeoutTicks;
        return new LoopStallOutcome(
            new LoopStallEvaluator(
                inOverrun: true,
                overrunSinceTicks: since,
                loopCountAtOverrun: loopAtStart,
                stallTimeoutTicks: _stallTimeoutTicks
            ),
            stalled,
            overrun
        );
    }

    private LoopStallEvaluator Reset() => new(false, 0, 0, _stallTimeoutTicks);
}
