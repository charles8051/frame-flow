using FrameFlow.Decoding;
using FrameFlow.Playback;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The selection rule from the lateness-driven-decode-skip ADR, which is the part
/// of that decision still carrying an open acceptance condition. These pin the
/// rule's shape; whether the shape converges on a real pipeline is a question for
/// a measured run, not for a unit test.
/// </summary>
public sealed class LatenessRecoveryPolicyTests
{
    private static readonly LatenessRecoveryOptions Options = new()
    {
        Enabled = true,
        EscalateAbove = TimeSpan.FromMilliseconds(400),
        RelaxBelow = TimeSpan.FromMilliseconds(120),
        MinImprovement = TimeSpan.FromMilliseconds(150),
    };

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Recovered for this many settle windows.</summary>
    private static TimeSpan Recovered(int windows) => Options.SettleWindow * windows;

    // ── The band ─────────────────────────────────────────────────────────

    [Fact]
    public void LatenessInsideTheBandMovesNothing()
    {
        // Between RelaxBelow and EscalateAbove is the whole point of having two
        // numbers: a pipeline sitting near the line must not alternate every window.
        var decision = LatenessRecoveryPolicy.Decide(2, Ms(300), Ms(310), Recovered(0), Options);

        Assert.Equal(2, decision.StepIndex);
        Assert.Equal(RecoveryMove.Hold, decision.Move);
    }

    [Fact]
    public void RecoveredLatenessGivesARungBack()
    {
        var decision = LatenessRecoveryPolicy.Decide(3, Ms(50), Ms(2000), Options.RelaxAfter, Options);

        Assert.Equal(2, decision.StepIndex);
        Assert.Equal(RecoveryMove.Relax, decision.Move);
    }

    [Fact]
    public void AHealthyPipelineNeverLeavesStepZero()
    {
        var decision = LatenessRecoveryPolicy.Decide(0, TimeSpan.Zero, null, Options.RelaxAfter, Options);

        Assert.Equal(0, decision.StepIndex);
        Assert.Equal(RecoveryMove.Hold, decision.Move);
    }

    // ── Walking up ───────────────────────────────────────────────────────

    [Fact]
    public void TheFirstMoveOffHealthyIsAlwaysTheCheapestRung()
    {
        // Nothing to have improved on yet, and the cheapest rung costs no decoded
        // frames, so there is nothing to weigh.
        var decision = LatenessRecoveryPolicy.Decide(0, Ms(5000), null, Recovered(0), Options);

        Assert.Equal(1, decision.StepIndex);
        Assert.Equal(RecoveryMove.Advance, decision.Move);
        Assert.Equal(2, LatenessRecoveryPolicy.Path[decision.StepIndex].ReadbackEveryN);
        Assert.Equal(DecodeDiscardLevel.None, LatenessRecoveryPolicy.Path[decision.StepIndex].Discard);
    }

    [Fact]
    public void ARungThatHelpedEarnsTheNextRungInTheSameSection()
    {
        // 5 s down to 3 s: the copy is clearly part of the cost, so keep thinning it
        // rather than start discarding decoded frames.
        var decision = LatenessRecoveryPolicy.Decide(1, Ms(3000), Ms(5000), Recovered(0), Options);

        Assert.Equal(2, decision.StepIndex);
        Assert.Equal(RecoveryMove.Advance, decision.Move);
        Assert.True(decision.StepIndex < LatenessRecoveryPolicy.FirstDiscardStep);
    }

    // ── The selection rule ───────────────────────────────────────────────

    [Fact]
    public void ARungThatBoughtNothingIsTriedSparserRatherThanAbandoned()
    {
        // The prototype's second finding, and it reverses what the ADR first said.
        // A readback rung failing means it was too dense, not that the copy is the
        // wrong lever: measured, 1 in 2 recovered this pipeline from a small deficit
        // and lost ground against a large one. Jumping on that reading sent a
        // readback-bound pipeline into the destructive steps with 1 in 4 untried.
        var decision = LatenessRecoveryPolicy.Decide(1, Ms(5000), Ms(5010), Recovered(0), Options);

        Assert.Equal(2, decision.StepIndex);
        Assert.Equal(RecoveryMove.Advance, decision.Move);
        Assert.True(decision.StepIndex < LatenessRecoveryPolicy.FirstDiscardStep);
    }

    [Fact]
    public void OnlyTheLastReadbackRungFailingCrossesIntoDiscarding()
    {
        // Running out of the harmless section is the evidence about the section.
        var last = LatenessRecoveryPolicy.FirstDiscardStep - 1;
        var decision = LatenessRecoveryPolicy.Decide(last, Ms(5000), Ms(5010), Recovered(0), Options);

        Assert.Equal(LatenessRecoveryPolicy.FirstDiscardStep, decision.StepIndex);
        Assert.Equal(RecoveryMove.SwitchToDiscard, decision.Move);
        Assert.Equal(
            DecodeDiscardLevel.NonReference,
            LatenessRecoveryPolicy.Path[decision.StepIndex].Discard
        );
    }

    [Fact]
    public void AnImprovementUnderTheBarCountsAsNotHelping()
    {
        // 40 ms of movement against a 150 ms bar, mid-section: try sparser.
        var decision = LatenessRecoveryPolicy.Decide(2, Ms(4960), Ms(5000), Recovered(0), Options);

        Assert.Equal(3, decision.StepIndex);
        Assert.Equal(RecoveryMove.Advance, decision.Move);
    }

    [Fact]
    public void ARungWithNoWindowUnderItYetHoldsInsteadOfMoving()
    {
        // The bug the prototype found on its first run. Moving here means the next
        // reading is judged against a window that ran under the PREVIOUS rung, so
        // every move begets another and the improvement rule never executes — the
        // walk climbed the whole readback section blind.
        var decision = LatenessRecoveryPolicy.Decide(2, Ms(5000), null, Recovered(0), Options);

        Assert.Equal(2, decision.StepIndex);
        Assert.Equal(RecoveryMove.Hold, decision.Move);
    }

    [Fact]
    public void ARungIsNotGivenBackUntilRecoveryHasHeld()
    {
        // One recovered window is not enough. Unwinding as fast as it escalates lets
        // lateness rebuild mid-descent, and the walk hunts instead of settling —
        // measured on the first prototype run, which cycled 0 to 8 and back twice in
        // thirty seconds.
        var early = LatenessRecoveryPolicy.Decide(3, Ms(50), Ms(60), Recovered(1), Options);
        Assert.Equal(3, early.StepIndex);
        Assert.Equal(RecoveryMove.Hold, early.Move);

        var held = LatenessRecoveryPolicy.Decide(3, Ms(50), Ms(60), Options.RelaxAfter, Options);
        Assert.Equal(2, held.StepIndex);
        Assert.Equal(RecoveryMove.Relax, held.Move);
    }

    [Fact]
    public void AStalledDiscardStepAdvancesInsteadOfJumping()
    {
        // Past the switch there is nowhere to jump to, so the same "did not help"
        // reading walks one step instead.
        var decision = LatenessRecoveryPolicy.Decide(
            LatenessRecoveryPolicy.FirstDiscardStep,
            Ms(5000),
            Ms(5000),
            TimeSpan.Zero,
            Options
        );

        Assert.Equal(LatenessRecoveryPolicy.FirstDiscardStep + 1, decision.StepIndex);
        Assert.Equal(RecoveryMove.Advance, decision.Move);
    }

    [Fact]
    public void TheWalkStopsAtTheEndOfThePath()
    {
        var decision = LatenessRecoveryPolicy.Decide(
            LatenessRecoveryPolicy.LastStep,
            Ms(60_000),
            Ms(60_000),
            TimeSpan.Zero,
            Options
        );

        Assert.Equal(LatenessRecoveryPolicy.LastStep, decision.StepIndex);
        Assert.Equal(RecoveryMove.Hold, decision.Move);
    }

    // ── The options ──────────────────────────────────────────────────────

    [Fact]
    public void InvertedThresholdsAreRejected()
    {
        // No per-property setter can catch this: an init accessor sees its own value
        // and whatever the other happened to be, which depends on the order the
        // caller wrote them in. Inverted, the bands overlap and lateness inside the
        // overlap takes the relax branch — a pipeline late enough to need escalating
        // is handed a rung back instead.
        var inverted = new LatenessRecoveryOptions
        {
            EscalateAbove = TimeSpan.FromMilliseconds(400),
            RelaxBelow = TimeSpan.FromMilliseconds(500),
        };

        Assert.Throws<ArgumentException>(inverted.Validate);
    }

    [Fact]
    public void EqualThresholdsAreRejectedToo()
    {
        // No gap is no hysteresis, which is the whole reason there are two numbers.
        var flat = new LatenessRecoveryOptions
        {
            EscalateAbove = TimeSpan.FromMilliseconds(400),
            RelaxBelow = TimeSpan.FromMilliseconds(400),
        };

        Assert.Throws<ArgumentException>(flat.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveDurationsAreRejectedWhereTheyAreWritten(int ms)
    {
        // A zero settle window reaches PeriodicTimer inside the worker, where the
        // catch that stops a recovery fault taking playback down would report it as
        // a playback fault.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LatenessRecoveryOptions { SettleWindow = TimeSpan.FromMilliseconds(ms) }
        );
    }

    [Fact]
    public void TheDefaultsFormABand()
    {
        new LatenessRecoveryOptions().Validate();
    }

    // ── The path itself ──────────────────────────────────────────────────

    [Fact]
    public void ThePathIsOrderedByDamage()
    {
        // Every readback rung comes before every discard step, and neither section
        // ever gets cheaper as the index rises. If this ever fails, the walk is
        // reaching for decoded frames before it has finished with copies.
        var path = LatenessRecoveryPolicy.Path;

        for (var i = 0; i < LatenessRecoveryPolicy.FirstDiscardStep; i++)
            Assert.Equal(DecodeDiscardLevel.None, path[i].Discard);

        for (var i = 1; i < path.Count; i++)
        {
            Assert.True(path[i].ReadbackEveryN >= path[i - 1].ReadbackEveryN);
            Assert.True(path[i].Discard >= path[i - 1].Discard);
        }
    }

    [Fact]
    public void StepZeroIsUntouchedPlayback()
    {
        // The off position has to be free. A pipeline that never goes late must be
        // bit-identical to one with the policy disabled.
        Assert.Equal(1, LatenessRecoveryPolicy.Path[0].ReadbackEveryN);
        Assert.Equal(DecodeDiscardLevel.None, LatenessRecoveryPolicy.Path[0].Discard);
    }

    [Fact]
    public void EveryReadbackRungKeepsMorePictureThanKeyframesOnlyWould()
    {
        // Measured: on the 2160p60 fixture NONKEY leaves 114 frames in 30 s, and the
        // last readback rung leaves 232. The ordering only pays if that stays true —
        // otherwise the "harmless" section is the more destructive one.
        const int keyframesOnlyFramesPer30s = 114;
        const int receivedPer30s = 1864;

        for (var i = 1; i < LatenessRecoveryPolicy.FirstDiscardStep; i++)
        {
            var kept = receivedPer30s / LatenessRecoveryPolicy.Path[i].ReadbackEveryN;
            Assert.True(
                kept > keyframesOnlyFramesPer30s,
                $"readback rung {i} keeps {kept}, which is not more than keyframes-only"
            );
        }
    }
}
