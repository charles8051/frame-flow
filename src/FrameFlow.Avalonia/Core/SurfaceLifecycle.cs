// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Avalonia.Core;

/// <summary>Where a control is in its life (ADR-0085).</summary>
internal enum SurfacePhase
{
    /// <summary>Constructed, and not yet in the visual tree.</summary>
    New = 0,

    /// <summary>In the visual tree.</summary>
    Live = 1,

    /// <summary>
    /// Out of the visual tree after being in it. The control keeps its target, unbound, and binds
    /// it again when it rejoins.
    /// </summary>
    Detached = 2,

    /// <summary>Disposed. Nothing starts again.</summary>
    Disposed = 3,
}

/// <summary>What a control holds, and whether it owns it (ADR-0044).</summary>
internal enum SurfaceTarget
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>A target the control created, such as a view's own sink. The control disposes it.</summary>
    Owned = 1,

    /// <summary>
    /// A target a caller assigned, such as a caller's sink or a chrome control's transport. The
    /// control never disposes it.
    /// </summary>
    Borrowed = 2,
}

/// <summary>What the shell does for one event, in the order listed.</summary>
/// <remarks>
/// The shell holds at most one target at a time. <see cref="Create"/> and <see cref="Borrow"/>
/// give it one; every other step acts on the one it holds when it reaches that step, so a
/// replacement lets go of the old target before it takes up the new one.
/// </remarks>
[Flags]
internal enum SurfaceWork
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>
    /// End the binding to the held target: nothing it raises reaches the control any more. A
    /// chrome control stops following its transport.
    /// </summary>
    Unbind = 1 << 0,

    /// <summary>
    /// Stop presenting, and tear down what presenting set up. A chrome control stops its refresh
    /// timer.
    /// </summary>
    StopPresenting = 1 << 1,

    /// <summary>Dispose the held target, which the control owns, and hold nothing.</summary>
    DisposeTarget = 1 << 2,

    /// <summary>
    /// Let go of the held target, which a caller owns, without disposing it, and hold nothing.
    /// </summary>
    ForgetTarget = 1 << 3,

    /// <summary>Create a target the control owns, such as a view's own sink, and hold it.</summary>
    Create = 1 << 4,

    /// <summary>Hold the target the caller assigned. The caller still owns it.</summary>
    Borrow = 1 << 5,

    /// <summary>
    /// Bind the held target: what it raises reaches the control, and while the control presents,
    /// a frame the target kept is presented. A chrome control follows its transport from here.
    /// </summary>
    Bind = 1 << 6,

    /// <summary>
    /// Start presenting the bound target, beginning with a frame it kept. A chrome control starts
    /// its refresh timer.
    /// </summary>
    StartPresenting = 1 << 7,
}

/// <summary>The state an event moves the control to, and the work it takes to get there.</summary>
/// <param name="Next">The state after the event.</param>
/// <param name="Work">What the shell does, in the order <see cref="SurfaceWork"/> lists.</param>
/// <param name="Refused">
/// The control is disposed and refuses the event: the shell throws
/// <see cref="ObjectDisposedException"/> and does nothing else. <paramref name="Next"/> is the
/// state it was in, and <paramref name="Work"/> is <see cref="SurfaceWork.None"/>.
/// </param>
internal readonly record struct SurfaceTransition(SurfaceLifecycle Next, SurfaceWork Work, bool Refused = false);

/// <summary>
/// The lifecycle of a control that binds to something (ADR-0085): which state an initialise, a
/// visual-tree attach or detach, a dispose, an assigned target and a request for an owned target
/// move it to, and what the shell does for each. Pure: a state and an event in, a state and the
/// work out.
/// </summary>
/// <remarks>
/// <para>
/// A view's target is its sink: one it creates through <see cref="EnsureOwned"/>, or a caller's
/// through <see cref="Assign"/>. A chrome control's target is its transport, always borrowed: it
/// follows the transport while bound, runs its refresh timer while presenting, and needs no
/// dispose.
/// </para>
/// <para>
/// <b>The target follows the control; presenting follows the attachment.</b> A detach unbinds the
/// target and stops presenting, and the control keeps the target, owned or borrowed. A reattach
/// binds the same target again and presents it. An owned target is disposed only when
/// <see cref="Assign"/> replaces it or the control is disposed, never by a detach, so
/// <see cref="EnsureOwned"/> creates one target for the control's whole life unless
/// <see cref="Assign"/> replaced it.
/// </para>
/// <para>
/// <b>Bound and presenting.</b> The control is bound while it holds a target and is not detached
/// or disposed. A target assigned or created before the first attach is bound at once, and the
/// first attach starts presenting it. One assigned or created while the control is detached is
/// bound when the control rejoins. The control presents while it is in the visual tree,
/// initialised and bound, and at no other time: nothing presents before the first attach or
/// before <see cref="Initialize"/>. Assigning one target over another while presenting rebinds
/// without stopping.
/// </para>
/// <para>
/// <b>The binding number</b> advances by one on every transition that starts or ends a binding,
/// and only then: an attach that binds, a detach that unbinds, an <see cref="Assign"/> that
/// unbinds or binds, an <see cref="EnsureOwned"/> that binds, and a dispose that unbinds. Work
/// that commits a result compares the number it started under with the current one, and drops
/// its result when it has moved. The first attach of a control bound before it starts no binding
/// and leaves the number alone, so a chrome control's follow, bound before the attach, is still
/// current after it.
/// </para>
/// <para>
/// <b>Dispose</b> unbinds, stops presenting, disposes an owned target and forgets a borrowed one.
/// Afterwards <see cref="Initialize"/>, <see cref="EnsureOwned"/> and assigning a target are
/// refused; assigning none, an attach, a detach and another dispose do nothing.
/// </para>
/// <para>
/// A repeated attach, or a detach of a control that is not in the visual tree, changes nothing.
/// Whether a sink is claimed, and what it keeps while it is not presented, belong to the shell
/// and the sink, not to this core.
/// </para>
/// </remarks>
/// <param name="Phase">Where the control is in its life.</param>
/// <param name="Initialized">
/// Whether the control is initialised. Only the interop view waits for <see cref="Initialize"/>;
/// the others start initialised.
/// </param>
/// <param name="Target">What the control holds, and whether it owns it.</param>
/// <param name="Binding">The binding number.</param>
internal readonly record struct SurfaceLifecycle(SurfacePhase Phase, bool Initialized, SurfaceTarget Target, long Binding)
{
    /// <summary>A control just constructed that needs no <see cref="Initialize"/>.</summary>
    public static SurfaceLifecycle Initial => new(SurfacePhase.New, Initialized: true, SurfaceTarget.None, Binding: 0);

    /// <summary>A control just constructed that presents only after <see cref="Initialize"/>.</summary>
    public static SurfaceLifecycle AwaitingInitialize => Initial with { Initialized = false };

    /// <summary>Whether the control is in the visual tree.</summary>
    public bool InTree => Phase == SurfacePhase.Live;

    /// <summary>Whether the control is disposed.</summary>
    public bool IsDisposed => Phase == SurfacePhase.Disposed;

    /// <summary>
    /// Whether the held target is bound: the control holds one and is neither detached nor
    /// disposed.
    /// </summary>
    public bool IsBound => Target != SurfaceTarget.None && Phase is SurfacePhase.New or SurfacePhase.Live;

    /// <summary>Whether the control presents: it is in the visual tree, initialised and bound.</summary>
    public bool Presents => InTree && Initialized && IsBound;

    /// <summary><c>Initialize</c> ran.</summary>
    public SurfaceTransition Initialize() =>
        IsDisposed ? Refuse() : Move(this with { Initialized = true }, replacesTarget: false);

    /// <summary>The control joined the visual tree.</summary>
    public SurfaceTransition Attach() =>
        Phase is SurfacePhase.New or SurfacePhase.Detached
            ? Move(this with { Phase = SurfacePhase.Live }, replacesTarget: false)
            : Stay();

    /// <summary>The control left the visual tree.</summary>
    public SurfaceTransition Detach() =>
        Phase == SurfacePhase.Live
            ? Move(this with { Phase = SurfacePhase.Detached }, replacesTarget: false)
            : Stay();

    /// <summary>The control was disposed.</summary>
    public SurfaceTransition Dispose() =>
        IsDisposed
            ? Stay()
            : Move(this with { Phase = SurfacePhase.Disposed, Target = SurfaceTarget.None }, replacesTarget: false);

    /// <summary>
    /// A caller assigned a target in place of whatever the control held, or none. The shell sends
    /// this only for a target other than the one it holds.
    /// </summary>
    /// <param name="target">
    /// <see langword="true"/> when the caller assigned a target, which the control borrows;
    /// <see langword="false"/> when it assigned none.
    /// </param>
    public SurfaceTransition Assign(bool target)
    {
        if (IsDisposed)
            return target ? Refuse() : Stay();

        return Move(this with { Target = target ? SurfaceTarget.Borrowed : SurfaceTarget.None }, replacesTarget: true);
    }

    /// <summary>
    /// The control needs a target, and creates its own when it holds none. A target it already
    /// holds, owned or borrowed, is the answer, and nothing changes.
    /// </summary>
    public SurfaceTransition EnsureOwned()
    {
        if (IsDisposed)
            return Refuse();

        return Target == SurfaceTarget.None
            ? Move(this with { Target = SurfaceTarget.Owned }, replacesTarget: false)
            : Stay();
    }

    // Every rule above is a choice of next state. The work is the difference between the two
    // states, in SurfaceWork's order: let go of what ends, then take up what starts.
    // replacesTarget: the event puts a different target in place of the held one, even of the
    // same kind, so the held one is let go and its binding ends.
    private SurfaceTransition Move(SurfaceLifecycle next, bool replacesTarget)
    {
        var letsGo = Target != SurfaceTarget.None && (replacesTarget || next.Target == SurfaceTarget.None);
        var takesUp = next.Target != SurfaceTarget.None && (replacesTarget || Target == SurfaceTarget.None);
        var unbinds = IsBound && (letsGo || !next.IsBound);
        var binds = next.IsBound && (takesUp || !IsBound);

        var work = SurfaceWork.None;
        if (unbinds)
            work |= SurfaceWork.Unbind;
        if (Presents && !next.Presents)
            work |= SurfaceWork.StopPresenting;
        if (letsGo)
            work |= Target == SurfaceTarget.Owned ? SurfaceWork.DisposeTarget : SurfaceWork.ForgetTarget;
        if (takesUp)
            work |= next.Target == SurfaceTarget.Owned ? SurfaceWork.Create : SurfaceWork.Borrow;
        if (binds)
            work |= SurfaceWork.Bind;
        if (next.Presents && !Presents)
            work |= SurfaceWork.StartPresenting;

        return new(next with { Binding = unbinds || binds ? Binding + 1 : Binding }, work);
    }

    private SurfaceTransition Stay() => new(this, SurfaceWork.None);

    private SurfaceTransition Refuse() => new(this, SurfaceWork.None, Refused: true);
}
