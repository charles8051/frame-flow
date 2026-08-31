using System.Diagnostics;
using FrameFlow.Avalonia.Windows;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Unit tests for the pure <see cref="PresenterStallEvaluator"/> gate. These exist specifically to
/// pin down the false-positive boundaries the adversarial review flagged: the gate must fire on a
/// real present-loop hang (enqueue frozen while the sink keeps feeding) but NOT on the benign
/// no-frames windows (clip advance, pause, startup) that a naive "presented flat for N seconds"
/// check trips on. It must <i>also</i> fire on the second, distinct signature added by
/// ADR-0064's observability work — enqueue climbing while the compositor commit stays flat ("frames reaching the queue but
/// not the screen", the warm-sink orphaned-converter freeze) — without false-positiving when
/// commit merely lags enqueue by a constant.
/// </summary>
public sealed class PresenterStallEvaluatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static PresenterStallOutcome FoldOutcome(PresenterStallEvaluator eval, params PresenterSample[] samples)
    {
        var outcome = new PresenterStallOutcome(eval, false, 0);
        foreach (var s in samples)
        {
            outcome = eval.Observe(in s);
            eval = outcome.Next;
        }
        return outcome;
    }

    private static bool FoldStalled(PresenterStallEvaluator eval, params PresenterSample[] samples) =>
        FoldOutcome(eval, samples).Stalled;

    // Healthy compositor: commit tracks enqueue, so the ADR-0064 output-stall rule never fires.
    // (NowTicks, FramesPresented, FramesCommitted, FramesAccepted, LastBltStartedTicks)
    private static PresenterSample Healthy(double seconds, int presented, long accepted) =>
        new(Ticks(seconds), presented, presented, accepted, 0);

    [Fact]
    public void NormalPlayback_PresentedAndCommittedAdvance_NeverStalls()
    {
        var eval = PresenterStallEvaluator.Create(Timeout);
        bool everStalled = false;
        for (int i = 0; i < 40; i++)
        {
            var outcome = eval.Observe(Healthy(i * 0.5, 10 + i, 100 + i * 5L));
            eval = outcome.Next;
            everStalled |= outcome.Stalled;
        }
        Assert.False(everStalled);
    }

    [Fact]
    public void PresentLoopWedged_PresentedFlat_SinkStillFeeding_Stalls()
    {
        // Presentation warms up, then the enqueue loop freezes (convert/Blt wedged) while the
        // decoder keeps feeding the sink. Commit freezes in lockstep (nothing new to commit).
        var outcome = FoldOutcome(
            PresenterStallEvaluator.Create(Timeout),
            Healthy(0, 10, 100),
            Healthy(1, 20, 200),                 // present progress; origin = t=1
            new PresenterSample(Ticks(2), 20, 20, 224, 0),  // 1s flat, fed -> under timeout
            new PresenterSample(Ticks(5), 20, 20, 300, 0)); // 4s flat, fed -> STALL
        Assert.True(outcome.Stalled);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, outcome.Reason);
    }

    [Fact]
    public void ClipAdvance_PresentedFlat_NoFeed_DoesNotStall()
    {
        // The false-positive the review called out: a clip advance rebuilds the player off-thread,
        // so presented goes flat — but the sink stops being fed too, so it is NOT a stall.
        var stalled = FoldStalled(
            PresenterStallEvaluator.Create(Timeout),
            Healthy(0, 10, 100),
            Healthy(1, 20, 200),   // progress
            Healthy(5, 20, 200));  // 4s flat AND no fresh intake -> not a stall
        Assert.False(stalled);
    }

    [Fact]
    public void Paused_AllFlat_DoesNotStall()
    {
        var stalled = FoldStalled(
            PresenterStallEvaluator.Create(Timeout),
            Healthy(0, 50, 500),
            Healthy(10, 50, 500)); // long flat, no feed -> not a stall
        Assert.False(stalled);
    }

    [Fact]
    public void Startup_NeverPresented_DoesNotStall()
    {
        // Frames arrive but presentation never started (composition setup pending) -> not the freeze.
        var stalled = FoldStalled(
            PresenterStallEvaluator.Create(Timeout),
            new PresenterSample(Ticks(0), 0, 0, 100, 0),
            new PresenterSample(Ticks(5), 0, 0, 300, 0)); // accepted climbing, presented stuck at 0 -> not a stall
        Assert.False(stalled);
    }

    [Fact]
    public void UnderTimeout_PresentedFlat_Fed_DoesNotStallYet()
    {
        var stalled = FoldStalled(
            PresenterStallEvaluator.Create(Timeout),
            Healthy(0, 10, 100),
            Healthy(1, 20, 200),                            // progress; origin t=1
            new PresenterSample(Ticks(3), 20, 20, 250, 0)); // 2s flat, fed -> still under 3s
        Assert.False(stalled);
    }

    [Fact]
    public void StallThenPresentProgress_ClearsStall()
    {
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(Healthy(0, 10, 100)).Next;
        eval = eval.Observe(Healthy(1, 20, 200)).Next;                      // progress
        eval = eval.Observe(new PresenterSample(Ticks(2), 20, 20, 224, 0)).Next; // fed while present flat -> arms at t=2
        var stalledOutcome = eval.Observe(new PresenterSample(Ticks(5), 20, 20, 300, 0)); // 3s unpresented -> STALL
        Assert.True(stalledOutcome.Stalled);

        // Present resumes -> the next observation is not stalled.
        var recovered = stalledOutcome.Next.Observe(Healthy(6, 30, 400));
        Assert.False(recovered.Stalled);
    }

    // ── An idle feed gap (image hold) followed by a resumed feed
    //    must NOT be mistaken for a wedged present loop ──

    [Fact]
    public void ImageHoldThenVideoResume_IdleFeedGapThenResume_DoesNotStall()
    {
        // The false positive that stranded a deployment OutOfService: a video plays (present + sink
        // both advancing), then the playlist shows an IMAGE held longer than the 3s stall timeout
        // (no feed at all: presented AND accepted both flat across the hold — the watchdog keeps
        // sampling at its 500ms cadence), then the next VIDEO begins. The next video's first decoded
        // frame lands in the warm sink (accepted climbs) while the first frame's keyed-mutex /
        // composition latency keeps the enqueue flat for one tick. Pre-fix this tripped a phantom
        // PresentLoopWedged because the present-progress origin still pointed at the previous clip's
        // last presented frame, ~4s back. The idle-gap re-anchor must keep this benign.
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(Healthy(0.0, 10, 100)).Next;
        eval = eval.Observe(Healthy(0.5, 20, 110)).Next;   // video playing: present + sink advance

        // Image hold > 3s: no feed, no present, sampled every 500ms.
        for (double t = 1.0; t <= 5.0; t += 0.5)
            eval = eval.Observe(Healthy(t, 20, 110)).Next;  // presented flat, accepted flat

        // Next video resumes: first frame accepted, but presented still flat for one tick (first-frame
        // present latency). accepted - acceptedAtPresented > 0 and now - (stale) origin would be >3s
        // pre-fix -> must NOT stall.
        var resume = eval.Observe(new PresenterSample(Ticks(5.5), 20, 20, 130, 0));
        Assert.False(resume.Stalled);

        // And once the first frame is actually presented, steady playback stays healthy.
        var playing = resume.Next.Observe(Healthy(6.0, 21, 140));
        Assert.False(playing.Stalled);
    }

    [Fact]
    public void SinkSwapDuringIdle_AcceptedCounterResets_DoesNotStall()
    {
        // Codex review guard: a host swap of CompositionInteropVideoView.Sink makes SampleStall start
        // reading the NEW sink's FramesAccepted from zero, so the counter goes backwards. A decrease is
        // a reset, not fresh intake; treating it as feed progress would arm Rule A during an idle hold
        // and trip a phantom PresentLoopWedged. Monotonic-increase detection ignores the reset.
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(Healthy(0.0, 20, 5000)).Next;   // warm: presented=20, accepted=5000
        eval = eval.Observe(Healthy(0.5, 20, 5000)).Next;   // idle hold begins (all flat)
        // Sink swapped: FramesAccepted resets to the new sink's baseline (0); presented still flat.
        eval = eval.Observe(new PresenterSample(Ticks(1.0), 20, 20, 0, 0)).Next;   // reset, not intake
        // Hold continues well past the timeout with the new sink idle (no video feed yet).
        var outcome = eval.Observe(new PresenterSample(Ticks(5.0), 20, 20, 0, 0)); // 4s later, no feed
        Assert.False(outcome.Stalled);
    }

    [Fact]
    public void SinkSwapWhileArmed_AcceptedCounterResets_DisarmsAndDoesNotStall()
    {
        // Codex review guard (already-armed path): Rule A arms while sink #1 feeds with the present
        // loop flat, THEN the host swaps the sink so FramesAccepted resets to the new sink's lower
        // baseline while the view-level FramesPresented stays flat. The pending frame belonged to the
        // old sink; a counter decrease must DISARM (not be carried), or the stale arm trips
        // PresentLoopWedged after the timeout even though the new sink is idle.
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(Healthy(0.0, 20, 5000)).Next;
        eval = eval.Observe(new PresenterSample(Ticks(0.5), 20, 20, 5024, 0)).Next; // sink#1 feeds, present flat -> ARMS
        eval = eval.Observe(new PresenterSample(Ticks(1.0), 20, 20, 5048, 0)).Next; // still armed (since 0.5)
        // Sink swapped: FramesAccepted resets to the new sink baseline (10), present still flat -> disarm.
        eval = eval.Observe(new PresenterSample(Ticks(1.5), 20, 20, 10, 0)).Next;
        // New sink idle through the timeout window -> the old arm is gone, so no stall.
        var outcome = eval.Observe(new PresenterSample(Ticks(5.0), 20, 20, 10, 0));
        Assert.False(outcome.Stalled);
    }

    [Fact]
    public void GenuineWedge_ContinuousFeedEveryInterval_PresentedFlat_StillStalls()
    {
        // Regression guard for the idle-gap fix: a REAL present-loop wedge — the sink keeps accepting
        // fresh frames on every 500ms sample while the enqueue stays frozen, with no idle gap — must
        // still trip. The oldest accepted-but-unpresented frame arms the clock at t=1.0 and the arm
        // survives, so the wedge fires once that frame has gone unpresented for the timeout.
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(Healthy(0.0, 10, 100)).Next;
        eval = eval.Observe(Healthy(0.5, 20, 110)).Next;   // last present progress

        PresenterStallOutcome outcome = default;
        long accepted = 110;
        for (double t = 1.0; t <= 4.0; t += 0.5)
        {
            accepted += 12;                                 // sink keeps feeding every interval
            outcome = eval.Observe(new PresenterSample(Ticks(t), 20, 20, accepted, 0)); // presented frozen
            eval = outcome.Next;
        }
        Assert.True(outcome.Stalled);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, outcome.Reason);
    }

    [Fact]
    public void GenuineWedge_SparseLowFpsFeed_PresentedFlat_StillStalls()
    {
        // Codex review guard: a REAL wedge on a sparse, low-FPS feed (e.g. 1 fps, so FramesAccepted
        // advances only every OTHER 500ms watchdog sample) must still trip. A per-interval "feed idle
        // this tick -> re-anchor" check would discard the accumulated wait on every quiet sample and
        // never reach the timeout. The arm-from-arrival model holds the earliest unpresented-frame
        // time across the quiet samples, so the wedge fires at arrival + timeout.
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(Healthy(0.0, 10, 100)).Next;
        eval = eval.Observe(Healthy(0.5, 20, 110)).Next;   // last present progress

        PresenterStallOutcome outcome = default;
        long accepted = 110;
        int i = 0;
        for (double t = 1.0; t <= 4.0; t += 0.5, i++)
        {
            if (i % 2 == 0) accepted += 10;                 // 1 fps: a new frame only every other tick
            outcome = eval.Observe(new PresenterSample(Ticks(t), 20, 20, accepted, 0)); // presented frozen
            eval = outcome.Next;
        }
        Assert.True(outcome.Stalled);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, outcome.Reason);
    }

    [Fact]
    public void OutputStall_SparseEnqueue_CommittedFlat_StillStalls()
    {
        // Codex review guard, Rule B sibling: a REAL output stall where the enqueue (presented)
        // advances only every other sample while the compositor commit stays frozen must still trip.
        // The oldest enqueued-but-uncommitted frame arms at t=1.0 and the arm survives the quiet
        // samples, so OutputNotComposited fires at arrival + timeout. (Feed advances only when the
        // enqueue does, so Rule A never arms and the reason is unambiguously the commit stall.)
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(new PresenterSample(Ticks(0.0), 10, 10, 100, 0)).Next;
        eval = eval.Observe(new PresenterSample(Ticks(0.5), 20, 20, 110, 0)).Next; // commit progress

        PresenterStallOutcome outcome = default;
        int presented = 20;
        long accepted = 110;
        int i = 0;
        for (double t = 1.0; t <= 4.0; t += 0.5, i++)
        {
            if (i % 2 == 0) { presented += 4; accepted += 10; } // sparse enqueue; commit frozen at 20
            outcome = eval.Observe(new PresenterSample(Ticks(t), presented, 20, accepted, 0));
            eval = outcome.Next;
        }
        Assert.True(outcome.Stalled);
        Assert.Equal(PresenterStalledReason.OutputNotComposited, outcome.Reason);
    }

    [Fact]
    public void ShortVideoToVideoGap_NoFeed_StillDoesNotStall()
    {
        // The existing short clip-advance behavior must be unchanged: a video->video advance is a
        // few hundred ms of off-thread player rebuild (presented flat, no fresh intake), well under
        // the 3s timeout, and resumes cleanly. (Companion to ClipAdvance_PresentedFlat_NoFeed.)
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(Healthy(0.0, 10, 100)).Next;
        eval = eval.Observe(Healthy(0.5, 20, 110)).Next;   // clip A playing
        eval = eval.Observe(Healthy(1.0, 20, 110)).Next;   // ~500ms advance gap: flat, no feed
        // Clip B's first frame lands and is presented on the next tick.
        var resumed = eval.Observe(Healthy(1.3, 21, 120));
        Assert.False(resumed.Stalled);
    }

    [Fact]
    public void OutputStall_ImageHoldThenResume_IdleEnqueueGap_DoesNotStall()
    {
        // Rule B sibling of the idle-gap false positive: across an image hold the enqueue (presented) is
        // flat too, so the commit-progress origin would otherwise span the hold. When the next video
        // resumes and the enqueue starts climbing again, sinceCommitted would be > 3s against the
        // pre-gap commit baseline -> a phantom OutputNotComposited. The idle-enqueue re-anchor keeps
        // it benign.
        var eval = PresenterStallEvaluator.Create(Timeout);
        eval = eval.Observe(new PresenterSample(Ticks(0.0), 10, 10, 100, 0)).Next;
        eval = eval.Observe(new PresenterSample(Ticks(0.5), 20, 20, 110, 0)).Next; // commit origin = 0.5

        // Image hold > 3s: presented AND committed flat, no feed.
        for (double t = 1.0; t <= 5.0; t += 0.5)
            eval = eval.Observe(new PresenterSample(Ticks(t), 20, 20, 110, 0)).Next;

        // Next video resumes: enqueue starts climbing again, commit follows a tick later. The commit
        // origin must have re-anchored across the idle enqueue, so this does not trip Rule B.
        var resume = eval.Observe(new PresenterSample(Ticks(5.5), 22, 20, 130, 0));
        Assert.False(resume.Stalled);
        var draining = resume.Next.Observe(new PresenterSample(Ticks(6.0), 24, 24, 145, 0));
        Assert.False(draining.Stalled);
    }

    // ── ADR-0064 §Observability: enqueue climbs, commit flat -> OutputNotComposited ──

    [Fact]
    public void OutputNotComposited_PresentedClimbs_CommittedFlat_Stalls()
    {
        // The warm-sink orphaned-converter freeze (ADR-0064): the present loop keeps enqueuing frames
        // to the compositor (presented climbs) but the compositor never drains them (committed
        // frozen). The old enqueue-only counter saw healthy "presented" climbing and missed it.
        var outcome = FoldOutcome(
            PresenterStallEvaluator.Create(Timeout),
            new PresenterSample(Ticks(0), 10, 10, 100, 0),
            new PresenterSample(Ticks(1), 20, 20, 200, 0),  // both advance; commit origin = t=1
            new PresenterSample(Ticks(2), 24, 20, 224, 0),  // presented climbs, commit flat (1s)
            new PresenterSample(Ticks(5), 30, 20, 300, 0)); // commit flat 4s while presented climbed -> STALL
        Assert.True(outcome.Stalled);
        Assert.Equal(PresenterStalledReason.OutputNotComposited, outcome.Reason);
    }

    [Fact]
    public void CommitLagsPresentByConstant_BothAdvance_DoesNotStall()
    {
        // Commit trails enqueue by a fixed offset but both keep advancing — a healthy pipeline with
        // one frame in flight. Must NOT be mistaken for an output stall.
        var eval = PresenterStallEvaluator.Create(Timeout);
        bool everStalled = false;
        for (int i = 0; i < 30; i++)
        {
            var s = new PresenterSample(Ticks(i * 0.5), 20 + i, 18 + i, 200 + i * 5L, 0);
            var outcome = eval.Observe(in s);
            eval = outcome.Next;
            everStalled |= outcome.Stalled;
        }
        Assert.False(everStalled);
    }

    [Fact]
    public void OutputNotComposited_UnderTimeout_DoesNotStallYet()
    {
        var stalled = FoldStalled(
            PresenterStallEvaluator.Create(Timeout),
            new PresenterSample(Ticks(0), 10, 10, 100, 0),
            new PresenterSample(Ticks(1), 20, 20, 200, 0),  // commit origin t=1
            new PresenterSample(Ticks(3), 28, 20, 280, 0)); // commit flat 2s -> under 3s
        Assert.False(stalled);
    }

    // ── Recovery confirmation ────────────────────────────────────────────────────────────────
    // The stall latch a host installs needs an evidence-based clear-path, or a transient wedge
    // strands the host until an operator intervenes: in one production incident a
    // ~9s wedge held a kiosk OutOfService for nearly two hours. These pin that "recovered" means SUSTAINED
    // forward progress on the counter that actually froze — never a bare verdict flip.

    /// <summary>Folds every sample and returns each outcome, so a test can assert on the edges.</summary>
    private static List<PresenterStallOutcome> FoldAll(
        PresenterStallEvaluator eval, params PresenterSample[] samples)
    {
        var outcomes = new List<PresenterStallOutcome>(samples.Length);
        foreach (var s in samples)
        {
            var outcome = eval.Observe(in s);
            eval = outcome.Next;
            outcomes.Add(outcome);
        }
        return outcomes;
    }

    /// <summary>The wedge from PresentLoopWedged_PresentedFlat_SinkStillFeeding_Stalls, as a prefix.</summary>
    private static readonly PresenterSample[] WedgePrefix =
    [
        Healthy(0, 10, 100),
        Healthy(1, 20, 200),
        new PresenterSample(Ticks(2), 20, 20, 224, 0),
        new PresenterSample(Ticks(5), 20, 20, 300, 0),   // STALL (PresentLoopWedged)
    ];

    [Fact]
    public void PresentLoopWedged_PresentingResumes_ConfirmsRecoveryAfterTheStreak()
    {
        // The host rebuilt the decode pipeline and the enqueue loop is running again. Recovery is
        // confirmed on the 4th consecutive advancing sample — not the 1st.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            [.. WedgePrefix,
             Healthy(5.5, 21, 310),
             Healthy(6.0, 22, 320),
             Healthy(6.5, 23, 330),
             Healthy(7.0, 24, 340)]);

        var recoveries = outcomes.FindAll(o => o.Recovered);
        Assert.Single(recoveries);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, recoveries[0].Reason);
        // The confirming sample is the 4th advancing one (index 3 of the tail), and not before.
        Assert.True(outcomes[^1].Recovered);
        Assert.False(outcomes[^2].Recovered);
    }

    [Fact]
    public void PresentLoopWedged_ProgressStopsShortOfTheStreak_NeverConfirms()
    {
        // Three advancing samples then flat again: not enough evidence, so the host stays latched.
        // This is the property that keeps a single post-wedge twitch from passing for recovery.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            [.. WedgePrefix,
             Healthy(5.5, 21, 310),
             Healthy(6.0, 22, 320),
             Healthy(6.5, 23, 330),
             new PresenterSample(Ticks(7.0), 23, 23, 340, 0)]);

        Assert.DoesNotContain(outcomes, o => o.Recovered);
    }

    [Fact]
    public void PresentLoopWedged_StreakBreaksThenRestarts_ConfirmsOnlyOnAFullRun()
    {
        // A broken streak must RESET, not resume: 3 advances, a flat sample, then 3 more is 6
        // advancing samples in total but never 4 consecutive, so it must not confirm.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            [.. WedgePrefix,
             Healthy(5.5, 21, 310),
             Healthy(6.0, 22, 320),
             Healthy(6.5, 23, 330),
             new PresenterSample(Ticks(7.0), 23, 23, 340, 0),  // flat -> streak resets
             Healthy(7.5, 24, 350),
             Healthy(8.0, 25, 360),
             Healthy(8.5, 26, 370)]);

        Assert.DoesNotContain(outcomes, o => o.Recovered);
    }

    [Fact]
    public void CounterReset_IsNotProgress_DoesNotConfirmRecovery()
    {
        // The load-bearing negative: a view swap re-bases FramesPresented to zero, which makes the
        // stall VERDICT go false. That is a disarm, not a recovery — confirming on it would clear a
        // host's latch for a presenter that never presented again. Four post-reset samples that do
        // not advance must produce nothing.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            [.. WedgePrefix,
             new PresenterSample(Ticks(5.5), 0, 0, 0, 0),     // view+sink swap, counters re-based
             new PresenterSample(Ticks(6.0), 0, 0, 0, 0),
             new PresenterSample(Ticks(6.5), 0, 0, 0, 0),
             new PresenterSample(Ticks(7.0), 0, 0, 0, 0)]);

        Assert.DoesNotContain(outcomes, o => o.Recovered);
    }

    [Fact]
    public void CounterReset_ThenGenuineProgress_ConfirmsAgainstTheRebasedCounter()
    {
        // The positive half of the reset case: after the swap the presenter really does start
        // presenting, so the evidence rebuilds against the new baseline and recovery confirms.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            [.. WedgePrefix,
             new PresenterSample(Ticks(5.5), 0, 0, 0, 0),     // re-based; not an advance
             Healthy(6.0, 1, 10),
             Healthy(6.5, 2, 20),
             Healthy(7.0, 3, 30),
             Healthy(7.5, 4, 40)]);

        var recoveries = outcomes.FindAll(o => o.Recovered);
        Assert.Single(recoveries);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, recoveries[0].Reason);
    }

    [Fact]
    public void OutputNotComposited_RecoversOnCommitProgress_NotOnEnqueueProgress()
    {
        // Rule B froze on COMMIT, so only commit progress is evidence. Enqueue climbing while the
        // compositor still refuses to drain is the very signature of the fault — it must not read
        // as recovery.
        // Same arming shape as OutputNotComposited_PresentedClimbs_CommittedFlat_Stalls: the
        // uncommitted clock arms at t=2 and fires at arrival + timeout.
        var stallThenEnqueueOnly = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            new PresenterSample(Ticks(0), 10, 10, 100, 0),
            new PresenterSample(Ticks(1), 20, 20, 200, 0),   // commit origin
            new PresenterSample(Ticks(2), 24, 20, 224, 0),   // enqueue climbs, commit flat -> arms
            new PresenterSample(Ticks(5), 30, 20, 300, 0),   // commit flat 3s -> STALL
            new PresenterSample(Ticks(5.5), 35, 20, 350, 0), // enqueue climbing, commit still flat
            new PresenterSample(Ticks(6.0), 40, 20, 400, 0),
            new PresenterSample(Ticks(6.5), 45, 20, 450, 0),
            new PresenterSample(Ticks(7.0), 50, 20, 500, 0));
        Assert.DoesNotContain(stallThenEnqueueOnly, o => o.Recovered);

        var withCommit = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            new PresenterSample(Ticks(0), 10, 10, 100, 0),
            new PresenterSample(Ticks(1), 20, 20, 200, 0),
            new PresenterSample(Ticks(2), 24, 20, 224, 0),   // arms
            new PresenterSample(Ticks(5), 30, 20, 300, 0),   // STALL
            Healthy(5.5, 35, 350),                            // commit tracks enqueue again
            Healthy(6.0, 40, 400),
            Healthy(6.5, 45, 450),
            Healthy(7.0, 50, 500));
        var recoveries = withCommit.FindAll(o => o.Recovered);
        Assert.Single(recoveries);
        Assert.Equal(PresenterStalledReason.OutputNotComposited, recoveries[0].Reason);
    }

    [Fact]
    public void NoStall_NeverReportsRecovery()
    {
        // Recovery is meaningless without a preceding stall; healthy playback must stay silent on
        // both edges.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            [.. Enumerable.Range(0, 20).Select(i => Healthy(i * 0.5, 10 + i, 100 + i * 5L))]);

        Assert.DoesNotContain(outcomes, o => o.Recovered);
        Assert.DoesNotContain(outcomes, o => o.Stalled);
    }

    [Fact]
    public void RecoveryConfirmed_ThenASecondWedge_StallsAndRecoversAgain()
    {
        // The pair must be re-armable: a box that wedges twice in one session has to report both,
        // or the second freeze is invisible to the host.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 2),
            [.. WedgePrefix,
             Healthy(5.5, 21, 310),
             Healthy(6.0, 22, 320),                            // recovery #1 confirmed
             new PresenterSample(Ticks(6.5), 22, 22, 340, 0),  // fed, unpresented -> arms
             new PresenterSample(Ticks(10.0), 22, 22, 420, 0), // 3.5s flat -> STALL #2
             Healthy(10.5, 23, 430),
             Healthy(11.0, 24, 440)]);                         // recovery #2 confirmed

        Assert.Equal(2, outcomes.FindAll(o => o.Recovered).Count);
        Assert.Equal(2, outcomes.FindAll(o => o.Stalled).Count);
    }

    [Fact]
    public void StallTypeChangesMidRecovery_RekeysToTheNewFrozenCounter()
    {
        // A wedge that is partway through proving recovery, and then trips the OTHER signature,
        // must re-key rather than carry its half-built evidence across. The in-flight streak was
        // evidence about the enqueue loop; the presenter is now failing at the compositor, which
        // that evidence says nothing about. Concretely: enqueue resumes (3 of 4 samples toward a
        // PresentLoopWedged recovery) while commit stays frozen until Rule B trips — the host must
        // NOT be told it recovered, and the eventual recovery must name OutputNotComposited.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            [.. WedgePrefix,                                     // STALL #1: PresentLoopWedged
             new PresenterSample(Ticks(5.5), 25, 20, 310, 0),    // enqueue resumes, commit frozen (streak 1)
             new PresenterSample(Ticks(6.0), 30, 20, 320, 0),    // streak 2
             new PresenterSample(Ticks(6.5), 35, 20, 330, 0),    // streak 3 — one short
             new PresenterSample(Ticks(8.6), 40, 20, 340, 0),    // commit flat 3.1s -> STALL #2
             Healthy(9.0, 45, 350),                              // compositor drains again
             Healthy(9.5, 50, 360),
             Healthy(10.0, 55, 370),
             Healthy(10.5, 60, 380)]);

        var stalls = outcomes.FindAll(o => o.Stalled);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, stalls[0].Reason);
        Assert.Equal(PresenterStalledReason.OutputNotComposited, stalls[^1].Reason);

        // Exactly one recovery, and it names the signature that was outstanding when it confirmed —
        // never the abandoned PresentLoopWedged whose streak got to 3.
        var recoveries = outcomes.FindAll(o => o.Recovered);
        Assert.Single(recoveries);
        Assert.Equal(PresenterStalledReason.OutputNotComposited, recoveries[0].Reason);
    }

    [Fact]
    public void StallTypeChangesTheOtherWay_RuleBThenRuleA_StillRecovers()
    {
        // The mirror of the case above, called out as unpinned in review. A compositor stall that
        // is then joined by an enqueue wedge re-keys to PresentLoopWedged (Rule A has priority), so
        // recovery must key on the ENQUEUE counter from that point. The direction matters because
        // it is the one where a host could plausibly be left latched forever: if the streak stayed
        // keyed to the abandoned OutputNotComposited, a presenter whose compositor never drained
        // again could never confirm, even with the enqueue loop healthy.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            new PresenterSample(Ticks(0), 10, 10, 100, 0),
            new PresenterSample(Ticks(1), 20, 20, 200, 0),
            new PresenterSample(Ticks(2), 24, 20, 224, 0),    // arms Rule B
            new PresenterSample(Ticks(5), 30, 20, 300, 0),    // STALL #1: OutputNotComposited
            new PresenterSample(Ticks(5.5), 30, 20, 310, 0),  // enqueue goes flat too; sink still feeding
            new PresenterSample(Ticks(9.0), 30, 20, 400, 0),  // unpresented 3.5s -> STALL #2: Rule A wins
            Healthy(9.5, 35, 410),                             // both loops resume
            Healthy(10.0, 40, 420),
            Healthy(10.5, 45, 430),
            Healthy(11.0, 50, 440));

        var stalls = outcomes.FindAll(o => o.Stalled);
        Assert.Equal(PresenterStalledReason.OutputNotComposited, stalls[0].Reason);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, stalls[^1].Reason);

        var recoveries = outcomes.FindAll(o => o.Recovered);
        Assert.Single(recoveries);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, recoveries[0].Reason);
    }

    [Fact]
    public void CounterWraparound_IsAbsorbedAsARebase_RecoveryStillConfirms()
    {
        // Review raised int wraparound as making recovery structurally impossible ("latched
        // forever"). It is not: a wrap makes the counter go BACKWARDS, which the evaluator already
        // classifies as a re-base rather than progress. The wrapping sample costs one sample of
        // evidence, the baseline re-bases to the wrapped value, and the streak rebuilds from there.
        // Pinned so the claim stays answered rather than re-litigated.
        var outcomes = FoldAll(
            PresenterStallEvaluator.Create(Timeout, recoverySamples: 4),
            [.. WedgePrefix,                                                   // STALL: PresentLoopWedged
             new PresenterSample(Ticks(5.5), int.MinValue, int.MinValue, 310, 0),   // wrap: not an advance
             new PresenterSample(Ticks(6.0), int.MinValue + 1, int.MinValue + 1, 320, 0),
             new PresenterSample(Ticks(6.5), int.MinValue + 2, int.MinValue + 2, 330, 0),
             new PresenterSample(Ticks(7.0), int.MinValue + 3, int.MinValue + 3, 340, 0),
             new PresenterSample(Ticks(7.5), int.MinValue + 4, int.MinValue + 4, 350, 0)]);

        var recoveries = outcomes.FindAll(o => o.Recovered);
        Assert.Single(recoveries);
        Assert.Equal(PresenterStalledReason.PresentLoopWedged, recoveries[0].Reason);
    }

    [Fact]
    public void RecoverySamples_MustBeAtLeastOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PresenterStallEvaluator.Create(Timeout, recoverySamples: 0));
    }
}
