// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.MotionClip;

/// <summary>
/// Where the clip recorder sits in its motion-triggered lifecycle. The pure half of the
/// <c>Idle → Building → Idle</c> state machine ADR-0052 §6 describes — the
/// <c>Saving</c> leg lives in the substrate (a buffered gate→encoder edge), not here.
/// </summary>
public enum GatePhase
{
    /// <summary>Between clips: buffering pre-roll, waiting for motion to start one.</summary>
    Idle,

    /// <summary>A clip is being assembled: accumulating frames, counting down the post-roll window.</summary>
    Building,
}

/// <summary>
/// The threaded, immutable recorder state — the entire portion of "where the gate is" that
/// is ours to model as a value (the frame list, the clone, and the wall clock are the
/// shell's). A value, not a mutable cell.
/// </summary>
/// <param name="Phase">Idle or Building.</param>
/// <param name="PostRollRemaining">
/// Frames of continued quiet still required to finalise the clip. Reset to the post-roll
/// window on every renewed motion while Building; decremented on each quiet frame; <c>0</c>
/// while Idle.
/// </param>
/// <param name="FrameCount">
/// Frames accumulated in the in-progress clip (pre-roll + trigger + post-roll so far),
/// against which the per-clip cap is checked; <c>0</c> while Idle.
/// </param>
public readonly record struct GateState(GatePhase Phase, int PostRollRemaining, int FrameCount)
{
    /// <summary>The resting state before any motion: Idle, nothing buffered.</summary>
    public static GateState Initial => new(GatePhase.Idle, PostRollRemaining: 0, FrameCount: 0);
}

/// <summary>The fixed per-clip limits the fold is parameterised by (config, not state).</summary>
/// <param name="PostRollFrames">Frames of "no motion" required to finalise a clip.</param>
/// <param name="MaxFramesPerClip">
/// Hard cap on a clip's frame count; reaching it emits regardless of motion, bounding the
/// "infinite recording under continuous motion" failure mode (ADR-0052).
/// </param>
public readonly record struct GateLimits(int PostRollFrames, int MaxFramesPerClip);

/// <summary>What the shell should do with the frame list in response to one folded frame.</summary>
public enum ClipAction
{
    /// <summary>
    /// Stay where you are. While Idle: add the frame to the pre-roll ring. While Building:
    /// the frame is part of the clip and the clip continues.
    /// </summary>
    Continue,

    /// <summary>
    /// Idle saw motion: snapshot+clear the pre-roll ring into a fresh clip, append the
    /// trigger frame, and begin Building.
    /// </summary>
    StartClip,

    /// <summary>
    /// Building reached a boundary: finalise and emit the clip (see
    /// <see cref="ClipDecision.Reason"/>), then return to Idle. The frame that triggered the
    /// emit is already part of the clip.
    /// </summary>
    EmitClip,
}

/// <summary>One step of the gate: the next state paired with the action the shell must perform.</summary>
/// <param name="State">The threaded next state.</param>
/// <param name="Action">What the shell does with its frame list.</param>
/// <param name="Reason">
/// When <see cref="Action"/> is <see cref="ClipAction.EmitClip"/>, why the clip ended;
/// otherwise <see langword="null"/>.
/// </param>
public readonly record struct ClipDecision(
    GateState State,
    ClipAction Action,
    ClipEndReason? Reason
);

/// <summary>
/// The pure motion-clip gate fold (§5.3): <c>Advance(GateState, moved, preRollCount, limits)
/// → (GateState, ClipDecision)</c>, the trigger / pre-roll / post-roll / max-frames decision
/// lifted out of <see cref="RecordingGate"/>'s imperative <c>switch</c>-over-mutable-fields.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> ADR-0052 §6 calls the recorder a state machine, but it was written
/// as a <c>switch</c> over mutable fields (<c>_state</c>, <c>_postRollRemaining</c>,
/// <c>_clip.Count</c>) with <c>DateTime.UtcNow</c> read inline and the clip carried as a
/// mutable <c>List&lt;IVideoFrame&gt;?</c> across calls — state, IO, and timing fused.
/// Lifting the sequencing into this total function over an immutable <see cref="GateState"/>
/// makes the whole trigger/post-roll/cap decision exhaustively unit-testable with no camera,
/// no clock, and no frames — mirroring <c>LoopStallEvaluator</c> and <c>DecodeProtocol</c>.
/// </para>
/// <para>
/// This core owns <b>no</b> frames, no clone, no clock, no logging. The shell
/// (<see cref="RecordingGate"/>) appends/snapshots the frame list, clones frames, stamps the
/// trigger time from a shell-owned clock, and emits the <see cref="ClipSegment"/> — acting on
/// the <see cref="ClipDecision"/> this returns.
/// </para>
/// <para>
/// <b>Frame-count convention.</b> A clip opens with its pre-roll snapshot plus the trigger
/// frame, so on the Idle→Building transition the returned <see cref="GateState.FrameCount"/>
/// is <c>preRollCount + 1</c>. While Building, each folded frame is treated as already part
/// of the clip — <see cref="GateState.FrameCount"/> is incremented for it before the cap is
/// checked — exactly the order the original used (add the frame, then test
/// <c>_clip.Count &gt;= MaxFramesPerClip</c>, where <c>_clip.Count</c> already counted the
/// pre-roll). The shell appends the frame to its list in lockstep with calling
/// <see cref="Advance"/>.
/// </para>
/// </remarks>
public static class GateCore
{
    /// <summary>
    /// Fold one frame's motion verdict into the gate, returning the next state and the action
    /// the shell must perform on its frame list.
    /// </summary>
    /// <param name="state">The current threaded state.</param>
    /// <param name="moved">Whether the motion detector reported motion on this frame.</param>
    /// <param name="preRollCount">
    /// The number of pre-roll frames the shell would snapshot if a clip starts on this frame.
    /// Consulted only on the Idle→Building transition, to seed the clip's frame count
    /// (<c>preRollCount + 1</c> for the trigger). Ignored while Building.
    /// </param>
    /// <param name="limits">The per-clip post-roll window and frame cap.</param>
    /// <returns>The next state and the clip action (with an end reason when emitting).</returns>
    public static ClipDecision Advance(
        GateState state,
        bool moved,
        int preRollCount,
        GateLimits limits
    ) =>
        state.Phase switch
        {
            // Idle: buffer pre-roll until motion starts a clip. The clip opens with the
            // snapshotted pre-roll plus the trigger frame, so FrameCount seeds at
            // preRollCount + 1 — and the cap counts those pre-roll frames, as the original did.
            GatePhase.Idle => moved
                ? new ClipDecision(
                    new GateState(
                        GatePhase.Building,
                        PostRollRemaining: limits.PostRollFrames,
                        FrameCount: preRollCount + 1
                    ),
                    ClipAction.StartClip,
                    Reason: null
                )
                : new ClipDecision(state, ClipAction.Continue, Reason: null),

            // Building: the current frame is part of the clip (FrameCount++). Renewed motion
            // re-arms the full post-roll window; otherwise the window counts down. The clip
            // ends on post-roll-elapsed, or — checked second, exactly as the original — on
            // reaching the per-clip frame cap regardless of motion.
            GatePhase.Building => AdvanceBuilding(state, moved, limits),

            _ => throw new InvalidOperationException(
                $"GateCore.Advance is not defined for phase {state.Phase}."
            ),
        };

    private static ClipDecision AdvanceBuilding(GateState state, bool moved, GateLimits limits)
    {
        int frameCount = state.FrameCount + 1;

        // Post-roll bookkeeping: renewed motion re-arms the window; quiet decrements it.
        int postRoll = moved ? limits.PostRollFrames : state.PostRollRemaining - 1;

        // Emit on post-roll elapsed (quiet ran out). Motion can never trip this branch
        // because it just re-armed the window above.
        if (!moved && postRoll <= 0)
        {
            return Emit(ClipEndReason.PostRollElapsed);
        }

        // Then — and only if post-roll did not already emit — the hard frame cap. Mirrors the
        // original's `emit is null && _clip.Count >= MaxFramesPerClip` ordering.
        if (frameCount >= limits.MaxFramesPerClip)
        {
            return Emit(ClipEndReason.MaxFramesReached);
        }

        // Clip continues.
        return new ClipDecision(
            new GateState(GatePhase.Building, postRoll, frameCount),
            ClipAction.Continue,
            Reason: null
        );

        static ClipDecision Emit(ClipEndReason reason) =>
            new(GateState.Initial, ClipAction.EmitClip, reason);
    }
}
