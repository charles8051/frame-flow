// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// The shape of an incoming frame as the present decision sees it: the source domain
/// (GPU zero-copy vs CPU upload) and the video size. A pure input to
/// <see cref="PresentPlanner.Advance"/> — it carries nothing native (no texture, no device,
/// no PTS); those stay in the imperative shell. Width/height are the decoded frame's
/// dimensions, used to detect a mid-stream size change that requires a layout update.
/// </summary>
/// <param name="IsGpu">
/// <see langword="true"/> when the frame is presented zero-copy from a D3D11VA GPU surface;
/// <see langword="false"/> for a software BGRA frame on the CPU-upload fallback path. A change
/// in this value between frames is the source flip that re-imports the ring.
/// </param>
/// <param name="Width">The frame's pixel width.</param>
/// <param name="Height">The frame's pixel height.</param>
public readonly record struct FrameDescriptor(bool IsGpu, int Width, int Height);

/// <summary>
/// The ring/source/size state the per-frame present decision reads and threads forward — the
/// pure mirror of the scattered mutable fields the presenter shell owns
/// (<c>_activeIsGpu</c>, <c>_nextBuffer</c>, <c>_videoWidth</c>, <c>_videoHeight</c>, and the
/// completed-ness of each <c>_presentTasks</c> slot). Immutable: <see cref="PresentPlanner.Advance"/>
/// returns the next state rather than mutating this one, so the decision is a total value transform
/// with no IO, no clock, and no shared mutable state (the same shape as
/// <see cref="PresenterStallEvaluator"/>).
/// </summary>
public readonly struct PresentState
{
    /// <summary>
    /// Number of buffers in the shared keyed-mutex present ring. Mirrors
    /// <c>D3D11Nv12SharedConverter.BufferCount</c>; carried on the state so the pure core has no
    /// dependency on the converter type.
    /// </summary>
    public int RingSize { get; }

    /// <summary>
    /// Which source the imported ring is currently bound to: <see langword="true"/> = GPU,
    /// <see langword="false"/> = CPU, <see langword="null"/> = unbound (no frame presented yet, or
    /// just reset by a source flip / device-loss rebuild in the shell). A descriptor whose
    /// <see cref="FrameDescriptor.IsGpu"/> differs from this is the source flip.
    /// </summary>
    public bool? ActiveIsGpu { get; }

    /// <summary>The ring index the next free-slot scan starts from (round-robin cursor).</summary>
    public int NextBuffer { get; }

    /// <summary>The video width the layout was last sized for; <c>0</c> until the first frame.</summary>
    public int VideoWidth { get; }

    /// <summary>The video height the layout was last sized for; <c>0</c> until the first frame.</summary>
    public int VideoHeight { get; }

    private PresentState(int ringSize, bool? activeIsGpu, int nextBuffer, int videoWidth, int videoHeight)
    {
        RingSize = ringSize;
        ActiveIsGpu = activeIsGpu;
        NextBuffer = nextBuffer;
        VideoWidth = videoWidth;
        VideoHeight = videoHeight;
    }

    /// <summary>
    /// The initial state for a ring of <paramref name="ringSize"/> buffers: unbound source, cursor
    /// at 0, no known video size. Equivalent to the presenter's field defaults before the first
    /// frame.
    /// </summary>
    public static PresentState Initial(int ringSize) => new(ringSize, null, 0, 0, 0);

    /// <summary>Returns a copy with the round-robin cursor moved to <paramref name="nextBuffer"/>.</summary>
    public PresentState WithNextBuffer(int nextBuffer) =>
        new(RingSize, ActiveIsGpu, nextBuffer, VideoWidth, VideoHeight);

    /// <summary>Returns a copy with the bound source set to <paramref name="activeIsGpu"/>.</summary>
    public PresentState WithActiveSource(bool? activeIsGpu) =>
        new(RingSize, activeIsGpu, NextBuffer, VideoWidth, VideoHeight);

    /// <summary>Returns a copy with the recorded video size set to <paramref name="width"/> x <paramref name="height"/>.</summary>
    public PresentState WithVideoSize(int width, int height) =>
        new(RingSize, ActiveIsGpu, NextBuffer, width, height);
}

/// <summary>
/// The decision <see cref="PresentPlanner.Advance"/> reaches for one frame: which ring slot to
/// present into (or that the frame must be dropped), whether the imported ring must be re-imported
/// from scratch (source flipped), whether the surface layout must be updated (video size changed),
/// and the threaded-through next <see cref="PresentState"/>. The shell reads this and performs the
/// named outcome — it carries no native handles, only the verdict.
/// </summary>
public readonly record struct PresentPlan(
    PresentState NextState,
    int SlotIndex,
    bool ReimportRing,
    bool UpdateLayout)
{
    /// <summary>
    /// <see langword="true"/> when no free ring slot was available and the frame must be dropped
    /// (every buffer still had a present in flight). <see cref="SlotIndex"/> is
    /// <see cref="DropSlot"/> in this case.
    /// </summary>
    public bool Drop => SlotIndex == DropSlot;

    /// <summary>The <see cref="SlotIndex"/> sentinel meaning "no free slot — drop this frame".</summary>
    public const int DropSlot = -1;
}

/// <summary>
/// Pure per-frame present planner for the composition-interop presenter — the functional core of
/// <c>CompositionInteropVideoView.PresentRing</c>'s decision. Given the current ring/source/size
/// state and the incoming frame's <see cref="FrameDescriptor"/>, it decides, with <b>zero</b>
/// D3D11 / compositor / IO / clock, whether to re-import the ring (source flip), update the layout
/// (size change), and which free ring slot to fill — or to drop the frame. A total value transform
/// in the spirit of <c>EvaluateConverterAction</c> and <see cref="PresenterStallEvaluator"/>, so
/// the slot-rotation, source-flip, size-change, and drop logic is exhaustively unit-testable
/// without a GPU.
/// </summary>
/// <remarks>
/// <para>
/// The shell performs the outcome the plan names, in plan order: on
/// <see cref="PresentPlan.ReimportRing"/> it disposes the old imported ring through the compositor
/// (which clears every in-flight present, freeing all slots — so the plan scans a fully-free ring
/// from index 0 on a flip); on <see cref="PresentPlan.UpdateLayout"/> it re-letterboxes the surface;
/// then it fills, imports, and hands off the chosen <see cref="PresentPlan.SlotIndex"/>. A
/// <see cref="PresentPlan.Drop"/> means the shell increments its dropped counter and presents
/// nothing. The shell still owns the <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> Blt
/// stamp, the keyed-mutex hand-off, and all disposal.
/// </para>
/// <para>
/// <b>Slot freedom is an input.</b> The shell knows which slots have a present still in flight
/// (its <c>_presentTasks[i]</c> not yet completed); it passes that as the <c>slotFree</c> predicate.
/// The planner does not touch tasks — it only reads "is slot i available". On a source flip the
/// shell will clear the ring, so the planner ignores the passed freedom and treats the whole ring
/// as free (matching the shell's <c>Array.Clear(_presentTasks)</c>).
/// </para>
/// </remarks>
public static class PresentPlanner
{
    /// <summary>
    /// Folds one incoming frame's <paramref name="frame"/> descriptor into <paramref name="state"/>
    /// and returns the present <see cref="PresentPlan"/>. Pure: mutates nothing, depends only on its
    /// arguments. <paramref name="slotFree"/> reports whether ring slot <c>i</c> has no present in
    /// flight (the shell's completed-ness check on <c>_presentTasks[i]</c>); it is consulted only on
    /// the non-flip path, since a source flip clears the ring in the shell.
    /// </summary>
    /// <param name="state">The current ring/source/size state.</param>
    /// <param name="frame">The incoming frame's source domain and size.</param>
    /// <param name="slotFree">
    /// Predicate: <see langword="true"/> if ring slot <c>i</c> is free to fill (its previous present
    /// completed). Called for indices in <c>[0, <see cref="PresentState.RingSize"/>)</c>.
    /// </param>
    public static PresentPlan Advance(in PresentState state, in FrameDescriptor frame, Func<int, bool> slotFree)
    {
        ArgumentNullException.ThrowIfNull(slotFree);

        // (1) Source flip (GPU<->CPU): the imported ring is bound to one source's shared handles, so
        //     a flip re-imports from scratch. The shell disposes the old ring through the compositor
        //     and clears the in-flight present tasks, which resets the cursor to 0 and frees every
        //     slot — so on a flip we scan a fully-free ring from index 0, never the passed freedom.
        bool reimport = state.ActiveIsGpu != frame.IsGpu;
        int scanStart = reimport ? 0 : state.NextBuffer;

        // (2) Size change: the recorded video size differs, so the surface must be re-letterboxed.
        bool updateLayout = state.VideoWidth != frame.Width || state.VideoHeight != frame.Height;

        // (3) Free-slot pick: from the cursor, the first slot whose previous present has completed.
        //     After a flip the whole ring is free (see above), so this resolves to scanStart (0).
        int idx = PresentPlan.DropSlot;
        for (var n = 0; n < state.RingSize; n++)
        {
            var i = (scanStart + n) % state.RingSize;
            if (reimport || slotFree(i))
            {
                idx = i;
                break;
            }
        }

        // Thread the source/size forward regardless of the slot outcome — the shell sets
        // _activeIsGpu / _videoWidth / _videoHeight on a flip / size change before the slot scan,
        // and those stick even when the frame is ultimately dropped for want of a slot.
        var next = state
            .WithActiveSource(frame.IsGpu)
            .WithVideoSize(frame.Width, frame.Height);

        if (idx == PresentPlan.DropSlot)
        {
            // No free slot: drop. The cursor does not advance (nothing was claimed).
            return new PresentPlan(next, PresentPlan.DropSlot, reimport, updateLayout);
        }

        next = next.WithNextBuffer((idx + 1) % state.RingSize);
        return new PresentPlan(next, idx, reimport, updateLayout);
    }
}
