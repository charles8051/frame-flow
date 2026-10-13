// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Avalonia.Windows.Core;

/// <summary>Where a <see cref="CompositionInteropVideoView"/> is in its life (#650).</summary>
internal enum InteropViewPhase
{
    /// <summary>Nothing is set up on the compositor yet, and no teardown has run.</summary>
    New = 0,

    /// <summary>The compositor surface, the GPU interop and the stall watchdog are up.</summary>
    Presenting = 1,

    /// <summary>
    /// A detach tore the compositor side down. The view is reusable: once it is initialized and
    /// back in the visual tree, it presents again.
    /// </summary>
    Detached = 2,

    /// <summary><c>DisposeAsync</c> ran. Nothing starts again.</summary>
    Disposed = 3,
}

/// <summary>What the view does for one lifecycle event, in the order listed.</summary>
[Flags]
internal enum InteropViewWork
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>
    /// End the sink's attachment and dispose the sink if the view owns it. A sink the caller
    /// assigned is kept, unbound, for the next attachment.
    /// </summary>
    ReleaseSink = 1 << 0,

    /// <summary>With <see cref="ReleaseSink"/>: forget a sink the caller assigned, too.</summary>
    ForgetCallerSink = 1 << 1,

    /// <summary>
    /// The ordered teardown of the compositor side: stop the stall watchdog, detach the surface
    /// visual, dispose the imported ring, drain the presents in flight and dispose the producers.
    /// </summary>
    StopPresenting = 1 << 2,

    /// <summary>
    /// Bind the kept sink, set the compositor surface and the GPU interop up, and start the stall
    /// watchdog.
    /// </summary>
    StartPresenting = 1 << 3,
}

/// <summary>The state a lifecycle event moves the view to, and the work it takes to get there.</summary>
internal readonly record struct InteropViewTransition(InteropViewLifecycle Next, InteropViewWork Work);

/// <summary>
/// The lifecycle of a <see cref="CompositionInteropVideoView"/>: which state <c>Initialize</c>, a
/// visual-tree attach, a detach and <c>DisposeAsync</c> move it to, and what each has to set up or
/// tear down (#650). Pure: a state and an event in, a state and the work out.
/// </summary>
/// <remarks>
/// <para>
/// The view presents while it is initialized and in the visual tree, and not disposed. A detach
/// tears the compositor side down and leaves the view reusable, as <c>FrameFlowVideoView</c> is:
/// the view's own sink is disposed, so the next <c>EnsureSink</c> makes a new one, and attaching
/// again sets the surface up again. Only <c>DisposeAsync</c> is final.
/// </para>
/// <para>
/// Every start is followed by exactly one stop before the next start, so each attachment tears
/// down what it set up, once. The first detach or dispose of a new view also runs the teardown,
/// as the view always has, so a view that never presented still reports how its teardown went. A
/// dispose after a detach has no compositor side left to stop, and only releases the sink.
/// </para>
/// </remarks>
/// <param name="Phase">Where the view is in its life.</param>
/// <param name="Initialized">Whether <c>Initialize</c> has run.</param>
/// <param name="Attached">Whether the view is in the visual tree.</param>
internal readonly record struct InteropViewLifecycle(InteropViewPhase Phase, bool Initialized, bool Attached)
{
    /// <summary>A view just constructed.</summary>
    public static InteropViewLifecycle Initial => default;

    /// <summary>
    /// Whether frames are held rather than presented: the compositor side is torn down. True from a
    /// detach until the view presents again, and for good after a dispose.
    /// </summary>
    public bool HoldsFrames => Phase is InteropViewPhase.Detached or InteropViewPhase.Disposed;

    /// <summary><c>Initialize</c> ran.</summary>
    public InteropViewTransition Initialize() => StartIfReady(this with { Initialized = true });

    /// <summary>The view joined the visual tree.</summary>
    public InteropViewTransition Attach() => StartIfReady(this with { Attached = true });

    /// <summary>The view left the visual tree.</summary>
    public InteropViewTransition Detach()
    {
        var left = this with { Attached = false };
        if (Phase == InteropViewPhase.Disposed)
            return new(left, InteropViewWork.None);

        return new(left with { Phase = InteropViewPhase.Detached }, InteropViewWork.ReleaseSink | StopIfUp(Phase));
    }

    /// <summary><c>DisposeAsync</c> ran.</summary>
    public InteropViewTransition Dispose()
    {
        if (Phase == InteropViewPhase.Disposed)
            return new(this, InteropViewWork.None);

        return new(
            this with { Phase = InteropViewPhase.Disposed },
            InteropViewWork.ReleaseSink | InteropViewWork.ForgetCallerSink | StopIfUp(Phase));
    }

    // Presenting needs both Initialize and the visual tree; whichever comes second starts it.
    private static InteropViewTransition StartIfReady(InteropViewLifecycle next) =>
        next is { Phase: InteropViewPhase.New or InteropViewPhase.Detached, Initialized: true, Attached: true }
            ? new(next with { Phase = InteropViewPhase.Presenting }, InteropViewWork.StartPresenting)
            : new(next, InteropViewWork.None);

    // A detach has already stopped a Detached view, and nothing has started since.
    private static InteropViewWork StopIfUp(InteropViewPhase phase) =>
        phase is InteropViewPhase.New or InteropViewPhase.Presenting
            ? InteropViewWork.StopPresenting
            : InteropViewWork.None;
}
