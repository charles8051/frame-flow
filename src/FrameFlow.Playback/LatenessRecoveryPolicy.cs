// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;

namespace FrameFlow.Playback;

/// <summary>
/// One rung of the ordered recovery path: how much of the readback to skip, and
/// how much of the decode.
/// </summary>
/// <param name="ReadbackEveryN">
/// Copy one frame in N from the decoder to host memory. 1 copies every frame.
/// </param>
/// <param name="Discard">What the decoder itself stops producing.</param>
public readonly record struct RecoveryStep(int ReadbackEveryN, DecodeDiscardLevel Discard);

/// <summary>Why <see cref="LatenessRecoveryPolicy"/> moved, for the transcript.</summary>
public enum RecoveryMove
{
    /// <summary>Lateness is inside the band; nothing changed.</summary>
    Hold,

    /// <summary>Recovered enough to give a rung back.</summary>
    Relax,

    /// <summary>The last rung helped but has not finished the job.</summary>
    Advance,

    /// <summary>
    /// The readback section is exhausted — its last rung ran and did not help — so
    /// the walk crosses into steps that cost decoded frames. Reached by running out
    /// of the harmless section, not by inferring from one rung.
    /// </summary>
    SwitchToDiscard,
}

/// <summary>A decision, and the reason, so a run can be read back afterwards.</summary>
public readonly record struct RecoveryDecision(int StepIndex, RecoveryMove Move);

/// <summary>
/// The ordered path a late pipeline walks, and the rule that walks it. Pure: no
/// clock, no decoder, no I/O. Every input is a parameter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordered by damage.</b> Skipping a readback discards a copy and nothing else
/// — every frame is still decoded, so no reference chain breaks. Skipping a decode
/// discards a frame that later frames may be built from. The harmless section is
/// exhausted first, and a readback-bound pipeline recovers inside it and never
/// reaches the rest.
/// </para>
/// <para>
/// <b>Selection is the walk, not a classification.</b> Nothing here asks whether
/// the pipeline is readback-bound or decode-bound, because no observable answers
/// that. A rung that improves lateness earns the next one; a rung that does not
/// is tried sparser, because a rung failing says it was too dense rather than
/// that the section is wrong. Only running out of readback rungs is evidence
/// about the section.
/// </para>
/// <para>
/// <b>This is the part under test.</b> Whether that inference actually converges
/// on the right section is the open acceptance condition on the
/// lateness-driven-decode-skip ADR. It can fail in both directions: a
/// decode-bound pipeline that improves briefly because a rung reduced contention
/// looks like the copy binding, and a readback-bound one whose improvement is
/// lost in noise looks like the opposite. <see cref="RecoveryMove"/> is on the
/// decision so a run can be replayed and the wrong turns found.
/// </para>
/// </remarks>
public static class LatenessRecoveryPolicy
{
    /// <summary>
    /// The path. Index 0 is untouched playback; the readback rungs run to
    /// <see cref="FirstDiscardStep"/>, and the decode steps follow.
    /// </summary>
    /// <remarks>
    /// The readback rungs stop at 1 in 8 rather than continuing: past there the
    /// presented rate is below what the destructive steps yield, so thinning has
    /// stopped being the cheaper option. N is held at its last value across the
    /// switch rather than reset — a pipeline limited by both costs keeps whatever
    /// the copy skipping bought it.
    /// </remarks>
    public static readonly IReadOnlyList<RecoveryStep> Path =
    [
        new(1, DecodeDiscardLevel.None),
        new(2, DecodeDiscardLevel.None),
        new(3, DecodeDiscardLevel.None),
        new(4, DecodeDiscardLevel.None),
        new(8, DecodeDiscardLevel.None),
        new(8, DecodeDiscardLevel.NonReference),
        new(8, DecodeDiscardLevel.Bidirectional),
        new(8, DecodeDiscardLevel.KeyframesOnly),
    ];

    /// <summary>First index that costs decoded frames rather than copies.</summary>
    public static readonly int FirstDiscardStep = 5;

    /// <summary>Last index on the path.</summary>
    public static int LastStep => Path.Count - 1;

    /// <summary>
    /// Decides the next rung from the current one and how lateness moved over the
    /// settle window just ended.
    /// </summary>
    /// <param name="stepIndex">The rung the window just ran at.</param>
    /// <param name="lag">Lateness now.</param>
    /// <param name="lagAtWindowStart">
    /// Lateness when this rung began, or <see langword="null"/> when the rung has
    /// not had a full window under it yet. That is not evidence of anything, so
    /// the walk holds and lets the window run — moving on a reading taken under
    /// the previous rung would credit or blame the wrong one.
    /// </param>
    /// <param name="recoveredWindows">
    /// Consecutive windows ending at or under <see cref="LatenessRecoveryOptions.RelaxBelow"/>.
    /// Giving a rung back needs several, because it costs nothing to hold one a
    /// little longer and it costs a visible lurch to give one back too early.
    /// </param>
    /// <param name="options">The band, the improvement bar, and the dwell.</param>
    public static RecoveryDecision Decide(
        int stepIndex,
        TimeSpan lag,
        TimeSpan? lagAtWindowStart,
        int recoveredWindows,
        LatenessRecoveryOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        var index = Math.Clamp(stepIndex, 0, LastStep);

        // Below the floor of the band: give a rung back. Two thresholds rather
        // than one, so a pipeline sitting exactly at the line does not alternate
        // every window.
        if (lag <= options.RelaxBelow)
        {
            return index > 0 && recoveredWindows >= options.RelaxAfterWindows
                ? new RecoveryDecision(index - 1, RecoveryMove.Relax)
                : new RecoveryDecision(index, RecoveryMove.Hold);
        }

        if (lag < options.EscalateAbove)
            return new RecoveryDecision(index, RecoveryMove.Hold);

        if (index >= LastStep)
            return new RecoveryDecision(LastStep, RecoveryMove.Hold);

        // Untouched playback has nothing to have improved on, and the cheapest rung
        // costs no decoded frames, so the first move off it needs no evidence.
        if (index == 0)
            return new RecoveryDecision(1, RecoveryMove.Advance);

        // A rung with no window under it yet cannot be judged. Hold, so the window
        // it is about to get is the one it is judged on. Advancing here is what
        // made the first prototype climb the whole section blind, never once
        // running the rule it exists to apply.
        if (lagAtWindowStart is not { } before)
            return new RecoveryDecision(index, RecoveryMove.Hold);

        var improvement = before - lag;
        if (improvement >= options.MinImprovement)
            return new RecoveryDecision(index + 1, RecoveryMove.Advance);

        // The rung bought nothing. That is not yet evidence about the section: a
        // readback rung that fails may simply be too dense, and the answer to a rung
        // that could not keep up is a sparser one. Measured — 1 in 2 recovered this
        // pipeline from a small deficit and lost ground against a large one, and
        // jumping on that reading sent a readback-bound pipeline into the destructive
        // steps with 1 in 4 still untried.
        //
        // So the section is walked, and only running out of it is evidence about it.
        if (index + 1 == FirstDiscardStep)
            return new RecoveryDecision(FirstDiscardStep, RecoveryMove.SwitchToDiscard);

        return new RecoveryDecision(index + 1, RecoveryMove.Advance);
    }
}

/// <summary>
/// The constants the walk needs. All four are unmeasured — see the ADR's open
/// questions — and the defaults here are starting points for the prototype, not
/// proposals.
/// </summary>
public sealed record LatenessRecoveryOptions
{
    /// <summary>
    /// Off unless asked for. The ADR gates shipping on the selection rule being
    /// measured, so nothing changes for anyone who does not opt in.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Lateness at or above which the walk escalates.</summary>
    public TimeSpan EscalateAbove
    {
        get => _escalateAbove;
        init => _escalateAbove = Positive(value);
    }

    private readonly TimeSpan _escalateAbove = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Lateness at or below which a rung is given back. Below
    /// <see cref="EscalateAbove"/> on purpose: the gap is the hysteresis.
    /// </summary>
    public TimeSpan RelaxBelow
    {
        get => _relaxBelow;
        init => _relaxBelow = Positive(value);
    }

    private readonly TimeSpan _relaxBelow = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How much a rung has to move lateness to count as having helped. The bar
    /// that separates a rung that worked from noise, and the one most likely to
    /// be wrong here.
    /// </summary>
    public TimeSpan MinImprovement
    {
        get => _minImprovement;
        init => _minImprovement = Positive(value);
    }

    private readonly TimeSpan _minImprovement = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long a rung runs before it is judged. Has to outlast a transient, or a
    /// decode-bound pipeline that dips briefly reads as recovering.
    /// </summary>
    public TimeSpan SettleWindow
    {
        get => _settleWindow;
        init => _settleWindow = Positive(value);
    }

    private readonly TimeSpan _settleWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Consecutive recovered windows before a rung is given back. Escalation is
    /// deliberately faster than relaxation: unwinding at the same speed lets
    /// lateness rebuild before the walk has finished stepping down, and the
    /// pipeline hunts instead of settling.
    /// </summary>
    public int RelaxAfterWindows
    {
        get => _relaxAfterWindows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _relaxAfterWindows = value;
        }
    }

    private readonly int _relaxAfterWindows = 3;

    // Rejected where the caller sets them, not where the walk reads them. A zero
    // SettleWindow reaches PeriodicTimer and throws inside the worker, where the
    // catch that exists to keep a recovery fault from taking playback down would
    // report it as a playback fault instead — a configuration mistake surfacing as
    // a pipeline failure, one layer away from the line that caused it.
    private static TimeSpan Positive(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
        return value;
    }

    /// <summary>
    /// Checks the one rule a per-property setter cannot: that the two thresholds
    /// still form a band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap between <see cref="RelaxBelow"/> and <see cref="EscalateAbove"/> is
    /// the hysteresis, and nothing about setting either one alone can tell whether
    /// the pair is ordered — an init accessor sees its own value and whatever the
    /// other happened to be at the time, which depends on the order the caller
    /// wrote them in.
    /// </para>
    /// <para>
    /// Inverted, the bands overlap and lateness inside the overlap satisfies both
    /// directions at once: <c>Decide</c> takes the relax branch first, so a
    /// pipeline late enough to need escalating is given a rung back instead.
    /// </para>
    /// </remarks>
    public void Validate()
    {
        if (RelaxBelow >= EscalateAbove)
        {
            throw new ArgumentException(
                $"RelaxBelow ({RelaxBelow}) must be below EscalateAbove ({EscalateAbove}); "
                    + "the gap between them is the hysteresis, and inverted they overlap.",
                nameof(RelaxBelow)
            );
        }
    }
}
