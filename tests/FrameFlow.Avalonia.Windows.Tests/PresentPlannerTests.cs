using FrameFlow.Avalonia.Windows;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Unit tests for the pure per-frame present planner (architecture deepening §4.1): given the
/// presenter's current ring/source/size state and an incoming frame's <see cref="FrameDescriptor"/>,
/// decide which free ring slot to present into (or that the frame must be dropped), whether the
/// GPU<->CPU source flipped (re-import the ring), and whether the video size changed (update the
/// layout). This is the same shape as <see cref="CompositionInteropVideoView.EvaluateConverterAction"/>
/// and <see cref="PresenterStallEvaluator"/> — a total value transform with no GPU, so the
/// slot-rotation, source-flip, size-change, and drop logic is exhaustively testable on the build box.
/// </summary>
public sealed class PresentPlannerTests
{
    private const int Ring = 3; // mirrors D3D11Nv12SharedConverter.BufferCount

    // A frame descriptor at the canonical test size.
    private static FrameDescriptor Frame(bool isGpu, int w = 1920, int h = 1080) => new(isGpu, w, h);

    // Slot-freedom predicate from an explicit free/busy mask (true = free).
    private static Func<int, bool> Mask(params bool[] free) => i => free[i];

    // Every slot free — the steady state once prior presents have all completed.
    private static readonly Func<int, bool> AllFree = _ => true;

    // ── Slot rotation ──────────────────────────────────────────────

    [Fact]
    public void FirstGpuFrame_PicksSlotZero_FlipsSourceFromUnbound_UpdatesLayout()
    {
        // Initial state is unbound (ActiveIsGpu == null), so the very first frame is a "flip" that
        // binds the ring, and the size is unknown so the layout updates. Slot 0 is chosen.
        var plan = PresentPlanner.Advance(PresentState.Initial(Ring), Frame(isGpu: true), AllFree);

        Assert.False(plan.Drop);
        Assert.Equal(0, plan.SlotIndex);
        Assert.True(plan.ReimportRing);   // null -> true is a source flip (binds the unbound ring)
        Assert.True(plan.UpdateLayout);   // 0x0 -> 1920x1080
        Assert.Equal(true, plan.NextState.ActiveIsGpu);
        Assert.Equal(1, plan.NextState.NextBuffer);
        Assert.Equal(1920, plan.NextState.VideoWidth);
        Assert.Equal(1080, plan.NextState.VideoHeight);
    }

    [Fact]
    public void SteadyState_RotatesThroughSlots_ZeroOneTwoZero()
    {
        // Source and size unchanged, ring fully free each tick: the cursor walks 0,1,2,0,...
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080);

        var indices = new int[4];
        for (var n = 0; n < 4; n++)
        {
            var plan = PresentPlanner.Advance(state, Frame(isGpu: true), AllFree);
            indices[n] = plan.SlotIndex;
            // Steady frames: no flip, no layout churn.
            Assert.False(plan.ReimportRing);
            Assert.False(plan.UpdateLayout);
            state = plan.NextState;
        }

        Assert.Equal(new[] { 0, 1, 2, 0 }, indices);
    }

    [Fact]
    public void CursorSkipsBusySlots_PicksFirstFreeFromCursor()
    {
        // Cursor at 1, slot 1 still has a present in flight, slot 2 is free -> pick 2.
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080)
            .WithNextBuffer(1);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true), Mask(true, false, true));

        Assert.Equal(2, plan.SlotIndex);
        Assert.Equal(0, plan.NextState.NextBuffer); // (2 + 1) % 3
    }

    [Fact]
    public void CursorWrapsAround_ToFindFreeSlotBeforeIt()
    {
        // Cursor at 2, slot 2 busy, wrap to 0 (free) before 1.
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080)
            .WithNextBuffer(2);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true), Mask(true, true, false));

        Assert.Equal(0, plan.SlotIndex);
        Assert.Equal(1, plan.NextState.NextBuffer);
    }

    // ── Source flip detection (GPU<->CPU) ──────────────────────────

    [Fact]
    public void GpuToCpu_Flip_ReimportsRing_ResetsCursorToZero()
    {
        // Bound to GPU at cursor 2; a CPU frame arrives -> flip. The shell clears the ring on a
        // flip, so the plan ignores slot freedom and picks 0 from a fully-free ring.
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080)
            .WithNextBuffer(2);

        // Even with slots reported busy, a flip frees the whole ring -> slot 0.
        var plan = PresentPlanner.Advance(state, Frame(isGpu: false), Mask(false, false, false));

        Assert.True(plan.ReimportRing);
        Assert.Equal(0, plan.SlotIndex);
        Assert.Equal(false, plan.NextState.ActiveIsGpu); // now bound to CPU
        Assert.Equal(1, plan.NextState.NextBuffer);
        Assert.False(plan.UpdateLayout); // same size across the flip
    }

    [Fact]
    public void CpuToGpu_Flip_ReimportsRing()
    {
        var state = PresentState.Initial(Ring)
            .WithActiveSource(false)
            .WithVideoSize(1280, 720)
            .WithNextBuffer(1);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true, 1280, 720), Mask(false, false, false));

        Assert.True(plan.ReimportRing);
        Assert.Equal(0, plan.SlotIndex);
        Assert.Equal(true, plan.NextState.ActiveIsGpu);
    }

    [Fact]
    public void SameSource_NoFlip()
    {
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true), AllFree);

        Assert.False(plan.ReimportRing);
    }

    // ── Size-change detection ──────────────────────────────────────

    [Fact]
    public void SizeChange_SameSource_UpdatesLayout_NoReimport()
    {
        // A mid-stream resolution change on the same source: layout updates, ring is NOT re-imported.
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true, 1280, 720), AllFree);

        Assert.True(plan.UpdateLayout);
        Assert.False(plan.ReimportRing);
        Assert.Equal(1280, plan.NextState.VideoWidth);
        Assert.Equal(720, plan.NextState.VideoHeight);
    }

    [Fact]
    public void WidthOnlyChange_UpdatesLayout()
    {
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true, 1900, 1080), AllFree);

        Assert.True(plan.UpdateLayout);
    }

    [Fact]
    public void UnchangedShape_NoFlip_NoLayout_ReusesCleanly()
    {
        // The common case: same source, same size, free ring. No re-import, no layout update,
        // just the next slot. This is the "reuses correctly" guard mirroring SameDevice_Reuses.
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080)
            .WithNextBuffer(1);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true), AllFree);

        Assert.False(plan.Drop);
        Assert.False(plan.ReimportRing);
        Assert.False(plan.UpdateLayout);
        Assert.Equal(1, plan.SlotIndex);
        Assert.Equal(2, plan.NextState.NextBuffer);
    }

    // ── Drop when no free slot ─────────────────────────────────────

    [Fact]
    public void NoFreeSlot_Drops_LeavesCursorPut()
    {
        // Every slot still has a present in flight (compositor can't keep up) -> drop. The cursor
        // does not advance (nothing was claimed), but the source/size still thread forward.
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080)
            .WithNextBuffer(2);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true), Mask(false, false, false));

        Assert.True(plan.Drop);
        Assert.Equal(PresentPlan.DropSlot, plan.SlotIndex);
        Assert.Equal(2, plan.NextState.NextBuffer); // unchanged on a drop
        Assert.Equal(true, plan.NextState.ActiveIsGpu);
    }

    [Fact]
    public void DropThenFreeSlot_RecoversFromSameCursor()
    {
        // After a drop the cursor stayed at 2; once slot 2 frees, the next frame claims it.
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080)
            .WithNextBuffer(2);

        var dropped = PresentPlanner.Advance(state, Frame(isGpu: true), Mask(false, false, false));
        Assert.True(dropped.Drop);

        var recovered = PresentPlanner.Advance(dropped.NextState, Frame(isGpu: true), Mask(false, false, true));
        Assert.False(recovered.Drop);
        Assert.Equal(2, recovered.SlotIndex);
        Assert.Equal(0, recovered.NextState.NextBuffer);
    }

    [Fact]
    public void SizeChangeWithNoFreeSlot_StillUpdatesLayout_AndDrops()
    {
        // Layout/size threading is independent of the slot outcome: a size change is recorded and
        // the layout flagged even when the frame is ultimately dropped for want of a slot (matches
        // the shell, which sets _videoWidth/_videoHeight before the slot scan).
        var state = PresentState.Initial(Ring)
            .WithActiveSource(true)
            .WithVideoSize(1920, 1080);

        var plan = PresentPlanner.Advance(state, Frame(isGpu: true, 1280, 720), Mask(false, false, false));

        Assert.True(plan.Drop);
        Assert.True(plan.UpdateLayout);
        Assert.Equal(1280, plan.NextState.VideoWidth);
        Assert.Equal(720, plan.NextState.VideoHeight);
    }
}
