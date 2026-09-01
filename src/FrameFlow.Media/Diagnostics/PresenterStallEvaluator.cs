// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;

namespace FrameFlow.Media.Diagnostics;

/// <summary>
/// One sample of the presenter's liveness counters, taken by <see cref="PresenterStallWatchdog"/>.
/// A pure input to <see cref="PresenterStallEvaluator.Observe"/> — it carries the clock
/// (<see cref="NowTicks"/>, a <see cref="Stopwatch.GetTimestamp"/> value) so the evaluator itself
/// stays clock-free and total.
/// </summary>
/// <param name="NowTicks">The sample instant, as a <see cref="Stopwatch.GetTimestamp"/> value.</param>
/// <param name="FramesPresented">Frames the view has <i>enqueued</i> to the compositor so far (the present hand-off was posted).</param>
/// <param name="FramesCommitted">Frames the compositor actually <i>committed</i> so far (the hand-off task completed — ADR-0064 §Observability).</param>
/// <param name="FramesAccepted">Frames the sink has accepted from the decoder so far (the feed signal).</param>
/// <param name="LastBltStartedTicks">When the most recent convert/Blt started (diagnostic).</param>
public readonly record struct PresenterSample(
    long NowTicks,
    int FramesPresented,
    int FramesCommitted,
    long FramesAccepted,
    long LastBltStartedTicks);

/// <summary>
/// Why the presenter is considered stalled (ADR-0064 §Observability). Distinguishes the two structurally
/// different freezes the evaluator can see.
/// </summary>
public enum PresenterStalledReason
{
    /// <summary>Not stalled.</summary>
    None = 0,

    /// <summary>
    /// The present loop itself is wedged: <see cref="PresenterSample.FramesPresented"/> (enqueue)
    /// stopped advancing while the sink kept accepting frames. The UI-thread convert + enqueue is
    /// stuck (historically a hung <c>VideoProcessorBlt</c>, now a wedged <c>ExecuteCommandList</c> /
    /// keyed-mutex acquire) — frames are available but never even reach the compositor's queue.
    /// </summary>
    PresentLoopWedged = 1,

    /// <summary>
    /// Frames are reaching the compositor's queue but not the screen:
    /// <see cref="PresenterSample.FramesPresented"/> (enqueue) keeps climbing while
    /// <see cref="PresenterSample.FramesCommitted"/> (commit) stays flat — the compositor is not
    /// draining the hand-offs. The class the enqueue-only counter was blind to; the warm-sink
    /// orphaned-converter freeze (ADR-0064) lives here.
    /// </summary>
    OutputNotComposited = 2,
}

/// <summary>
/// Result of <see cref="PresenterStallEvaluator.Observe"/>: the threaded-through next state,
/// whether the presenter is stalled as of this sample, why, and how long the relevant progress
/// signal has been absent (for the stall log).
/// </summary>
/// <param name="Next">The threaded-through evaluator state for the next sample.</param>
/// <param name="Stalled">The raw per-sample stall verdict. Unchanged by the recovery signal below —
/// <see cref="Recovered"/> is a separate, slower edge, so a host that only cares about stalls keeps
/// byte-for-byte the pre-recovery behaviour.</param>
/// <param name="SinceProgressTicks">How long the relevant progress signal has been absent.</param>
/// <param name="Reason">Which stall signature fired — or, when <see cref="Recovered"/> is set, which
/// one the presenter recovered <i>from</i>.</param>
/// <param name="Recovered">
/// Set on the single sample that <b>confirms</b> recovery from a previously reported stall: the
/// counter that froze has advanced for
/// <see cref="PresenterStallEvaluator.DefaultRecoverySamples"/> consecutive samples. Deliberately
/// evidence-based rather than "the stall verdict went false" — the verdict also goes false when a
/// sink/view swap resets the counters (a disarm, not progress), and clearing a host's health latch
/// on a teardown would report recovery for a presenter that never presented again.
/// </param>
public readonly record struct PresenterStallOutcome(
    PresenterStallEvaluator Next,
    bool Stalled,
    long SinceProgressTicks,
    PresenterStalledReason Reason = PresenterStalledReason.None,
    bool Recovered = false);

/// <summary>
/// Pure stall detector for the composition-interop presenter — the functional core of
/// <see cref="PresenterStallWatchdog"/>. Decides "the presenter has stalled" from the liveness
/// counters alone: no IO, no clock, no mutable state carried across calls (the prior state is
/// threaded through <see cref="PresenterStallOutcome.Next"/>), so the gate is exhaustively
/// unit-testable without a GPU.
/// </summary>
/// <remarks>
/// <para>
/// The stall signature is <b>"frames presented stopped advancing while the sink kept accepting
/// new frames."</b> When the UI-thread <c>VideoProcessorBlt</c> hangs in the GPU driver (the
/// 2026-06-12 present-stall, investigation §9), <see cref="PresenterSample.FramesPresented"/>
/// freezes — the render tick is stuck in the Blt and never reaches the present increment —
/// while the decoder keeps depositing frames into the sink (<see cref="PresenterSample.FramesAccepted"/>
/// climbs, off the UI thread). That asymmetry is the freeze.
/// </para>
/// <para>
/// The <b>sink-accepted gate is essential</b>: a naive "presented flat for N seconds" check
/// false-positives on every benign no-frames window — a playlist clip advance rebuilds the
/// player off-thread for hundreds of ms with a live render timer and no new frames (the same
/// false-positive class that turned the device-loss poll into a rebuild storm).
/// Requiring fresh sink intake during the stall window suppresses those. The evaluator also
/// never reports a stall until presentation has actually started (presented &gt; 0), so a
/// composition-setup failure at startup is not mistaken for a freeze.
/// </para>
/// <para>
/// The window is measured as the <b>age of the oldest accepted-but-not-yet-presented frame</b>,
/// not as the elapsed time since the present loop last advanced. The watchdog runs continuously
/// over a warm presenter whose counters are cumulative across every clip, so a "time since the
/// last present advance" origin would span an <i>idle</i> gap (a signage image held longer than
/// the stall timeout, a pause, dead air between clips). When the next feed resumed, fresh intake
/// reappeared while the first frame's keyed-mutex / composition latency kept the enqueue flat for
/// one tick, and the gate that suppresses the short gap <i>inverted into a false
/// PresentLoopWedged</i> once the gap exceeded the timeout.
/// </para>
/// <para>
/// Arming from the frame's <i>arrival</i> fixes this for an idle gap of any length: across an idle
/// gap the sink produces nothing, so nothing arms; when the feed resumes the clock starts at the
/// resume, giving the present loop a full timeout to show the first frame. It also keeps a genuine
/// wedge detectable on a <b>sparse, low-frame-rate feed</b> (frames arriving slower than the
/// sample interval): the first unpresented frame arms the clock and the earliest arm time is held
/// across the quiet samples between frames, so the wedge still trips at <c>arrival + timeout</c>.
/// Rule B is the exact mirror with the enqueue loop as producer and the compositor commit as
/// consumer: it measures the age of the oldest enqueued-but-not-yet-committed frame.
/// </para>
/// </remarks>
public readonly struct PresenterStallEvaluator
{
    private readonly bool _seeded;
    // Last-seen counters, so each Observe can tell which signals advanced this interval.
    private readonly int _lastPresented;
    private readonly int _lastCommitted;
    private readonly long _lastAccepted;
    // Rule A — present loop wedged. The clock is "armed" when the sink has accepted a frame the
    // present loop has not yet shown; the arm timestamp is the arrival of the OLDEST such frame.
    private readonly bool _unpresentedArmed;
    private readonly long _unpresentedSinceTicks;     // NowTicks the oldest unpresented frame arrived
    // Rule B — output not composited (ADR-0064 §Observability). Mirror of Rule A with the enqueue loop as the
    // producer and the compositor commit as the consumer.
    private readonly bool _uncommittedArmed;
    private readonly long _uncommittedSinceTicks;     // NowTicks the oldest uncommitted enqueue arrived
    private readonly long _stallTimeoutTicks;
    // Recovery tracking. _stallReason is the signature currently being recovered FROM (None when no
    // stall is outstanding); _recoveryStreak counts consecutive samples in which that signature's
    // own counter strictly advanced. Confirmation at _recoverySamples clears both.
    private readonly PresenterStalledReason _stallReason;
    private readonly int _recoveryStreak;
    private readonly int _recoverySamples;

    private PresenterStallEvaluator(
        bool seeded,
        int lastPresented, int lastCommitted, long lastAccepted,
        bool unpresentedArmed, long unpresentedSinceTicks,
        bool uncommittedArmed, long uncommittedSinceTicks,
        long stallTimeoutTicks,
        PresenterStalledReason stallReason, int recoveryStreak, int recoverySamples)
    {
        _seeded = seeded;
        _lastPresented = lastPresented;
        _lastCommitted = lastCommitted;
        _lastAccepted = lastAccepted;
        _unpresentedArmed = unpresentedArmed;
        _unpresentedSinceTicks = unpresentedSinceTicks;
        _uncommittedArmed = uncommittedArmed;
        _uncommittedSinceTicks = uncommittedSinceTicks;
        _stallTimeoutTicks = stallTimeoutTicks;
        _stallReason = stallReason;
        _recoveryStreak = recoveryStreak;
        _recoverySamples = recoverySamples;
    }

    /// <summary>
    /// How many consecutive samples of forward progress confirm a recovery by default. At the
    /// watchdog's 500ms cadence that is ~2s of sustained presenting — long enough that a single
    /// post-wedge frame (or a teardown twitch) cannot pass for recovery, short enough that a host
    /// latch clears while the freeze is still the operator's live problem rather than yesterday's.
    /// </summary>
    public const int DefaultRecoverySamples = 4;

    /// <summary>
    /// Creates an unseeded evaluator that reports a stall only after the relevant progress signal
    /// has been absent for <paramref name="stallTimeout"/> (see <see cref="PresenterStalledReason"/>),
    /// and confirms recovery only after <paramref name="recoverySamples"/> consecutive samples of
    /// forward progress on the counter that froze.
    /// </summary>
    public static PresenterStallEvaluator Create(
        TimeSpan stallTimeout,
        int recoverySamples = DefaultRecoverySamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recoverySamples, 1);
        // Named arguments deliberately: the private ctor is 12 positional parameters, six of them
        // same-typed flags and counts, so a future field added in the wrong slot would thread
        // silently through a readonly struct with nothing to catch it.
        return new(
            seeded: false,
            lastPresented: 0,
            lastCommitted: 0,
            lastAccepted: 0,
            unpresentedArmed: false,
            unpresentedSinceTicks: 0,
            uncommittedArmed: false,
            uncommittedSinceTicks: 0,
            stallTimeoutTicks: (long)(stallTimeout.TotalSeconds * Stopwatch.Frequency),
            stallReason: PresenterStalledReason.None,
            recoveryStreak: 0,
            recoverySamples: recoverySamples);
    }

    /// <summary>
    /// Folds one <paramref name="sample"/> into the evaluator. Pure: returns the next state plus
    /// the verdict and mutates nothing. The first call seeds the baseline and never reports a stall.
    /// Evaluates two independent stall signatures (ADR-0064 §Observability): the present loop wedging
    /// (an accepted frame goes unpresented past the timeout —
    /// <see cref="PresenterStalledReason.PresentLoopWedged"/>) and the compositor not draining
    /// (an enqueued frame goes uncommitted past the timeout —
    /// <see cref="PresenterStalledReason.OutputNotComposited"/>).
    /// </summary>
    public PresenterStallOutcome Observe(in PresenterSample sample)
    {
        if (!_seeded)
        {
            return new PresenterStallOutcome(Seed(sample), Stalled: false, SinceProgressTicks: 0);
        }

        // Strictly-increasing comparisons, not "changed": the counters can go BACKWARDS when the host
        // swaps (or briefly clears) the sink/view and the new instance starts from zero. A decrease is
        // a reset/reseed, not progress — treating it as fresh intake would falsely arm a rule during
        // an idle hold and trip a phantom stall when the sink is replaced (review on PR #78). After a
        // reset the baseline re-bases to the new low value, so genuine increases from there are seen.
        bool presentedAdvanced = sample.FramesPresented > _lastPresented;
        bool committedAdvanced = sample.FramesCommitted > _lastCommitted;
        bool feedAdvanced = sample.FramesAccepted > _lastAccepted;
        // A counter going BACKWARDS is a sink/view swap reseeding from zero — not progress. It must
        // also DISARM an already-armed rule: the pending frame belonged to the old instance, so the
        // current (idle) instance must not inherit a stale arm and trip a phantom stall (review on
        // PR #78). A sink swap resets FramesAccepted alone; a view swap resets the presented/committed
        // pair. After a reset the baseline re-bases to the new low value, so later genuine increases
        // arm fresh.
        bool feedReset = sample.FramesAccepted < _lastAccepted;
        bool presentedReset = sample.FramesPresented < _lastPresented;
        bool committedReset = sample.FramesCommitted < _lastCommitted;

        // Rule A — measure the age of the oldest accepted-but-not-yet-presented frame. Present
        // progress disarms (the loop caught up), as does a sink/view counter reset (the pending frame
        // is moot). Otherwise the first fresh sink intake arms the clock at the frame's ARRIVAL, and
        // that earliest arm time is held across later samples. Measuring from arrival — not from the
        // last present advance — makes an idle gap benign (across an image hold / pause the sink
        // produces nothing, so nothing arms; the next feed arms from its resume, with a full timeout
        // to show the first frame) AND keeps a genuine wedge detectable on a sparse,
        // low-FPS feed (the arm survives the quiet samples between frames).
        bool unpresentedArmed;
        long unpresentedSince;
        if (presentedAdvanced || feedReset || presentedReset)
        {
            unpresentedArmed = false;
            unpresentedSince = 0;
        }
        else if (_unpresentedArmed)
        {
            unpresentedArmed = true;
            unpresentedSince = _unpresentedSinceTicks;   // keep the oldest unpresented arrival
        }
        else if (feedAdvanced)
        {
            unpresentedArmed = true;
            unpresentedSince = sample.NowTicks;          // first unpresented frame since last present
        }
        else
        {
            unpresentedArmed = false;
            unpresentedSince = 0;
        }

        // Rule B — mirror of Rule A: the age of the oldest enqueued-but-not-yet-committed frame.
        // Commit progress disarms, as does a view counter reset; otherwise the first fresh enqueue
        // arms. An idle enqueue (present flat across an image hold / pause) produces nothing to
        // commit, so nothing arms.
        bool uncommittedArmed;
        long uncommittedSince;
        if (committedAdvanced || committedReset || presentedReset)
        {
            uncommittedArmed = false;
            uncommittedSince = 0;
        }
        else if (_uncommittedArmed)
        {
            uncommittedArmed = true;
            uncommittedSince = _uncommittedSinceTicks;
        }
        else if (presentedAdvanced)
        {
            uncommittedArmed = true;
            uncommittedSince = sample.NowTicks;
        }
        else
        {
            uncommittedArmed = false;
            uncommittedSince = 0;
        }

        // Rule A fires when an accepted frame has gone unpresented past the timeout. The
        // presented > 0 guard keeps a never-started presenter (composition setup pending) from
        // being mistaken for a freeze.
        long sincePresented = unpresentedArmed ? sample.NowTicks - unpresentedSince : 0;
        bool wedged = sample.FramesPresented > 0
            && unpresentedArmed
            && sincePresented >= _stallTimeoutTicks;

        // Rule B fires when an enqueued frame has gone uncommitted past the timeout.
        long sinceCommitted = uncommittedArmed ? sample.NowTicks - uncommittedSince : 0;
        bool outputStall = sample.FramesPresented > 0
            && uncommittedArmed
            && sinceCommitted >= _stallTimeoutTicks;

        // Rule A takes priority: if the loop never enqueued, "not composited" is a downstream
        // symptom, not the root signal.
        var stalledNow = wedged
            ? PresenterStalledReason.PresentLoopWedged
            : outputStall
                ? PresenterStalledReason.OutputNotComposited
                : PresenterStalledReason.None;

        // ── Recovery confirmation ────────────────────────────────────────────────────────────
        // A stall we reported stays outstanding until the counter that FROZE advances for
        // _recoverySamples consecutive samples. Keyed on the frozen counter specifically: a
        // PresentLoopWedged recovers when the enqueue loop resumes (FramesPresented), an
        // OutputNotComposited when the compositor drains (FramesCommitted). Anything else —
        // the verdict merely going false, a sink/view swap resetting a counter, one lone frame
        // — is not evidence, and a host that clears a health latch on it would report a
        // recovery for a presenter that never presented again.
        var nextStallReason = _stallReason;
        int recoveryStreak = _recoveryStreak;
        bool recovered = false;
        if (stalledNow != PresenterStalledReason.None)
        {
            // Still stalled (or stalled again on the other signature): restart the evidence count.
            nextStallReason = stalledNow;
            recoveryStreak = 0;
        }
        else if (_stallReason != PresenterStalledReason.None)
        {
            bool frozenCounterAdvanced = _stallReason == PresenterStalledReason.PresentLoopWedged
                ? presentedAdvanced
                : committedAdvanced;
            // A reset is not an advance, so it lands here and zeroes the streak — the evidence has
            // to be rebuilt against the re-based counter.
            recoveryStreak = frozenCounterAdvanced ? _recoveryStreak + 1 : 0;
            if (recoveryStreak >= _recoverySamples)
            {
                recovered = true;
                nextStallReason = PresenterStalledReason.None;
                recoveryStreak = 0;
            }
        }

        var next = new PresenterStallEvaluator(
            seeded: true,
            lastPresented: sample.FramesPresented,
            lastCommitted: sample.FramesCommitted,
            lastAccepted: sample.FramesAccepted,
            unpresentedArmed: unpresentedArmed,
            unpresentedSinceTicks: unpresentedSince,
            uncommittedArmed: uncommittedArmed,
            uncommittedSinceTicks: uncommittedSince,
            stallTimeoutTicks: _stallTimeoutTicks,
            stallReason: nextStallReason,
            recoveryStreak: recoveryStreak,
            recoverySamples: _recoverySamples);

        if (wedged)
            return new PresenterStallOutcome(next, Stalled: true, sincePresented, PresenterStalledReason.PresentLoopWedged);
        if (outputStall)
            return new PresenterStallOutcome(next, Stalled: true, sinceCommitted, PresenterStalledReason.OutputNotComposited);
        // On the confirming sample, Reason names the signature recovered FROM (_stallReason, which
        // nextStallReason has already cleared) so the host can log what came back.
        return recovered
            ? new PresenterStallOutcome(next, Stalled: false, SinceProgressTicks: 0, _stallReason, Recovered: true)
            : new PresenterStallOutcome(next, Stalled: false, SinceProgressTicks: 0);
    }

    private PresenterStallEvaluator Seed(in PresenterSample sample) =>
        new(
            seeded: true,
            lastPresented: sample.FramesPresented,
            lastCommitted: sample.FramesCommitted,
            lastAccepted: sample.FramesAccepted,
            unpresentedArmed: false,
            unpresentedSinceTicks: 0,
            uncommittedArmed: false,
            uncommittedSinceTicks: 0,
            stallTimeoutTicks: _stallTimeoutTicks,
            stallReason: _stallReason,
            recoveryStreak: _recoveryStreak,
            recoverySamples: _recoverySamples);
}
