namespace FrameFlow.MotionClip.Tests;

/// <summary>
/// Unit tests for the pure motion-clip gate fold (<c>GateCore.Advance</c>) — §5.3.
/// </summary>
/// <remarks>
/// The trigger / pre-roll / post-roll / max-frames state machine ADR-0052 §6 describes is now
/// a total function over an immutable <see cref="GateState"/>, so it is exercised here with
/// <b>no camera, no clock, and no frames</b> — the shell (<c>RecordingGate</c>) owns all of
/// those. Each test drives the fold directly and asserts the next state and the action the
/// shell is told to perform, mirroring <c>DecodeProtocolTests</c> and the
/// <c>LoopStallEvaluator</c> tests.
/// </remarks>
public sealed class GateCoreTests
{
    // A representative config: a 3-frame post-roll window, capped at 10 frames per clip.
    private static readonly GateLimits Limits = new(PostRollFrames: 3, MaxFramesPerClip: 10);

    // ── Idle ──────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_NoMotion_StaysIdle_AndBuffersPreRoll()
    {
        var decision = GateCore.Advance(GateState.Initial, moved: false, preRollCount: 5, Limits);

        Assert.Equal(ClipAction.Continue, decision.Action);
        Assert.Equal(GatePhase.Idle, decision.State.Phase);
        Assert.Equal(GateState.Initial, decision.State); // nothing threaded while idle
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Idle_Motion_StartsClip_SeedingPostRollAndFrameCount()
    {
        // First motion starts a clip: post-roll arms to the full window, and the clip opens
        // with the snapshotted pre-roll (here 5) plus the trigger frame == 6.
        var decision = GateCore.Advance(GateState.Initial, moved: true, preRollCount: 5, Limits);

        Assert.Equal(ClipAction.StartClip, decision.Action);
        Assert.Equal(GatePhase.Building, decision.State.Phase);
        Assert.Equal(Limits.PostRollFrames, decision.State.PostRollRemaining);
        Assert.Equal(6, decision.State.FrameCount); // preRollCount + trigger
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Idle_Motion_WithEmptyPreRoll_OpensClipAtOneFrame()
    {
        var decision = GateCore.Advance(GateState.Initial, moved: true, preRollCount: 0, Limits);

        Assert.Equal(ClipAction.StartClip, decision.Action);
        Assert.Equal(1, decision.State.FrameCount); // just the trigger frame
    }

    // ── Building: post-roll countdown and reset ─────────────────────────────

    [Fact]
    public void Building_NoMotion_CountsDownPostRoll_AndContinues()
    {
        // Building with a full window; one quiet frame decrements the window by one and keeps
        // building. Frame count advances by one (the quiet frame joins the clip).
        var building = new GateState(GatePhase.Building, PostRollRemaining: 3, FrameCount: 6);

        var decision = GateCore.Advance(building, moved: false, preRollCount: 99, Limits);

        Assert.Equal(ClipAction.Continue, decision.Action);
        Assert.Equal(GatePhase.Building, decision.State.Phase);
        Assert.Equal(2, decision.State.PostRollRemaining); // 3 - 1
        Assert.Equal(7, decision.State.FrameCount); // 6 + 1
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Building_RenewedMotion_RearmsPostRollWindow()
    {
        // The window had wound down to 1; renewed motion re-arms it to the full PostRollFrames,
        // extending the clip. (preRollCount is ignored while building.)
        var building = new GateState(GatePhase.Building, PostRollRemaining: 1, FrameCount: 6);

        var decision = GateCore.Advance(building, moved: true, preRollCount: 0, Limits);

        Assert.Equal(ClipAction.Continue, decision.Action);
        Assert.Equal(Limits.PostRollFrames, decision.State.PostRollRemaining); // re-armed to 3
        Assert.Equal(7, decision.State.FrameCount);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Building_PostRollElapsed_EmitsAndReturnsToIdle()
    {
        // Window at 1; one more quiet frame takes it to 0 → emit PostRollElapsed, back to Idle.
        var building = new GateState(GatePhase.Building, PostRollRemaining: 1, FrameCount: 6);

        var decision = GateCore.Advance(building, moved: false, preRollCount: 0, Limits);

        Assert.Equal(ClipAction.EmitClip, decision.Action);
        Assert.Equal(ClipEndReason.PostRollElapsed, decision.Reason);
        Assert.Equal(GateState.Initial, decision.State); // resets to Idle
    }

    /// <summary>
    /// The headline behavior: a post-roll countdown that is repeatedly interrupted by renewed
    /// motion never emits — the clip extends for as long as motion keeps coming, then finalises
    /// only after a full quiet window. Folds a whole frame sequence through the pure core.
    /// </summary>
    [Fact]
    public void Building_CountdownResetsOnRenewedMotion_ThenEmitsAfterFullQuietWindow()
    {
        var limits = new GateLimits(PostRollFrames: 3, MaxFramesPerClip: 1000);
        GateState state = GateCore
            .Advance(GateState.Initial, moved: true, preRollCount: 0, limits)
            .State; // Building, postRoll=3, frames=1

        // Two quiet frames wind the window 3→2→1; a motion frame re-arms it to 3; two more
        // quiet frames wind it 3→2→1 again — never reaching 0, so nothing emits mid-script.
        bool[] script = [false, false, true, false, false];
        foreach (bool moved in script)
        {
            var step = GateCore.Advance(state, moved, preRollCount: 0, limits);
            Assert.Equal(ClipAction.Continue, step.Action); // never emits while the window holds
            state = step.State;
        }

        Assert.Equal(GatePhase.Building, state.Phase);
        Assert.Equal(1, state.PostRollRemaining); // window stands at 1 after the re-arm + 2 quiet

        // The very next quiet frame takes the (re-armed) window 1→0 → emit PostRollElapsed.
        var final = GateCore.Advance(state, moved: false, preRollCount: 0, limits);
        Assert.Equal(ClipAction.EmitClip, final.Action);
        Assert.Equal(ClipEndReason.PostRollElapsed, final.Reason);
        Assert.Equal(GateState.Initial, final.State);
    }

    // ── Building: max-frames cap ────────────────────────────────────────────

    [Fact]
    public void Building_ReachingMaxFrames_EmitsMaxFramesReached_EvenWithMotion()
    {
        // One frame short of the cap (9/10); the next frame — WITH motion, which re-arms the
        // post-roll and so can't trip the post-roll branch — still emits on the frame cap.
        var building = new GateState(GatePhase.Building, PostRollRemaining: 3, FrameCount: 9);

        var decision = GateCore.Advance(building, moved: true, preRollCount: 0, Limits);

        Assert.Equal(ClipAction.EmitClip, decision.Action);
        Assert.Equal(ClipEndReason.MaxFramesReached, decision.Reason);
        Assert.Equal(GateState.Initial, decision.State);
    }

    [Fact]
    public void Building_PostRollWins_WhenBothBoundariesCoincide()
    {
        // At the cap boundary AND post-roll just elapsed (quiet frame): the original checked
        // post-roll first, so PostRollElapsed is the reason, not MaxFramesReached.
        var building = new GateState(GatePhase.Building, PostRollRemaining: 1, FrameCount: 9);

        var decision = GateCore.Advance(building, moved: false, preRollCount: 0, Limits);

        Assert.Equal(ClipAction.EmitClip, decision.Action);
        Assert.Equal(ClipEndReason.PostRollElapsed, decision.Reason);
    }

    [Fact]
    public void Building_JustUnderCap_Continues()
    {
        // 8 → 9 frames, motion present (window stays armed), still under the 10-frame cap.
        var building = new GateState(GatePhase.Building, PostRollRemaining: 3, FrameCount: 8);

        var decision = GateCore.Advance(building, moved: true, preRollCount: 0, Limits);

        Assert.Equal(ClipAction.Continue, decision.Action);
        Assert.Equal(9, decision.State.FrameCount);
    }

    // ── Totality ────────────────────────────────────────────────────────────

    [Fact]
    public void Advance_FromInitial_IsTotalOverMotionFlag()
    {
        // Both motion verdicts are defined from the resting state — no throw, a real decision.
        Assert.Equal(
            ClipAction.Continue,
            GateCore.Advance(GateState.Initial, moved: false, preRollCount: 0, Limits).Action
        );
        Assert.Equal(
            ClipAction.StartClip,
            GateCore.Advance(GateState.Initial, moved: true, preRollCount: 0, Limits).Action
        );
    }
}
