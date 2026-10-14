using FrameFlow.Avalonia.Core;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// <see cref="SurfaceLifecycle"/>, one lifecycle for the views and the chrome controls (ADR-0085):
/// which state each event moves a control to, and what the shell does for it.
/// </summary>
/// <remarks>
/// The named cases pin the sequences the ADR's decisions describe, among them #663's: a caller's
/// sink, a detach and a reattach. <see cref="EverySequence_KeepsTheLifecycleInvariants"/> then walks
/// every sequence of the seven events, of any length, from both initial states, playing the shell:
/// it does each transition's work, in order, on numbered targets, and checks the properties the
/// shell relies on after every step. It grew out of <c>InteropViewLifecycleTests</c>, which walks
/// the four events of the interop view's own core.
/// </remarks>
public sealed class SurfaceLifecycleTests
{
    public enum Event
    {
        Initialize,
        Attach,
        Detach,
        Dispose,

        /// <summary>A caller assigns a target: its sink, or a chrome control's transport.</summary>
        Assign,

        /// <summary>A caller assigns none.</summary>
        AssignNone,

        EnsureOwned,
    }

    private static readonly Event[] Events = Enum.GetValues<Event>();

    private static SurfaceTransition Step(SurfaceLifecycle state, Event e) => e switch
    {
        Event.Initialize => state.Initialize(),
        Event.Attach => state.Attach(),
        Event.Detach => state.Detach(),
        Event.Dispose => state.Dispose(),
        Event.Assign => state.Assign(target: true),
        Event.AssignNone => state.Assign(target: false),
        Event.EnsureOwned => state.EnsureOwned(),
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    private static List<SurfaceTransition> RunFrom(SurfaceLifecycle state, Event[] events)
    {
        var steps = new List<SurfaceTransition>();
        foreach (var e in events)
        {
            var t = Step(state, e);
            steps.Add(t);
            state = t.Next;
        }
        return steps;
    }

    // A CPU view or a chrome control: initialised from the start.
    private static List<SurfaceTransition> Run(params Event[] events) => RunFrom(SurfaceLifecycle.Initial, events);

    // The interop view: presents only after Initialize.
    private static List<SurfaceTransition> RunUninitialized(params Event[] events) =>
        RunFrom(SurfaceLifecycle.AwaitingInitialize, events);

    private const SurfaceWork Nothing = SurfaceWork.None;
    private const SurfaceWork Unbind = SurfaceWork.Unbind;
    private const SurfaceWork Stop = SurfaceWork.StopPresenting;
    private const SurfaceWork DisposeTarget = SurfaceWork.DisposeTarget;
    private const SurfaceWork Forget = SurfaceWork.ForgetTarget;
    private const SurfaceWork Create = SurfaceWork.Create;
    private const SurfaceWork Borrow = SurfaceWork.Borrow;
    private const SurfaceWork Bind = SurfaceWork.Bind;
    private const SurfaceWork Start = SurfaceWork.StartPresenting;

    // ── The sink follows the view (#663) ────────────────────────────

    [Fact]
    public void ACallersSink_IsBoundAgain_WhenTheViewRejoinsTheTree()
    {
        // #663: the CPU view kept a caller's sink across a detach and never bound it again.
        var steps = Run(Event.Assign, Event.Attach, Event.Detach, Event.Attach);

        Assert.Equal(Borrow | Bind, steps[0].Work);
        Assert.Equal(Start, steps[1].Work);
        Assert.Equal(Unbind | Stop, steps[2].Work);
        Assert.Equal(SurfaceTarget.Borrowed, steps[2].Next.Target);
        Assert.Equal(Bind | Start, steps[3].Work); // the same sink: nothing borrowed or forgotten
        Assert.True(steps[3].Next.Presents);
    }

    [Fact]
    public void ACallersSink_AssignedInTheTree_IsBoundAgainAfterAReattach()
    {
        var steps = Run(Event.Attach, Event.Assign, Event.Detach, Event.Attach);

        Assert.Equal(Nothing, steps[0].Work);
        Assert.Equal(Borrow | Bind | Start, steps[1].Work);
        Assert.Equal(Unbind | Stop, steps[2].Work);
        Assert.Equal(Bind | Start, steps[3].Work);
    }

    [Fact]
    public void AnOwnedSink_OutlivesADetach_AndTheReattachPresentsTheSameOne()
    {
        var steps = Run(Event.EnsureOwned, Event.Attach, Event.Detach, Event.Attach, Event.EnsureOwned);

        Assert.Equal(Create | Bind, steps[0].Work);
        Assert.Equal(Start, steps[1].Work);
        Assert.Equal(Unbind | Stop, steps[2].Work); // not disposed
        Assert.Equal(SurfaceTarget.Owned, steps[2].Next.Target);
        Assert.Equal(Bind | Start, steps[3].Work); // not created again
        Assert.Equal(Nothing, steps[4].Work); // asked again, the view has the same sink
    }

    [Fact]
    public void AViewThatCreatesItsSinkOnAttach_CreatesItOnce()
    {
        // The CPU view creates its own sink when it joins the tree holding none.
        var steps = Run(Event.Attach, Event.EnsureOwned, Event.Detach, Event.Attach, Event.EnsureOwned);

        Assert.Equal(Nothing, steps[0].Work);
        Assert.Equal(Create | Bind | Start, steps[1].Work);
        Assert.Equal(Unbind | Stop, steps[2].Work);
        Assert.Equal(Bind | Start, steps[3].Work);
        Assert.Equal(Nothing, steps[4].Work);
    }

    [Fact]
    public void AnOwnedSink_IsDisposedByTheDispose_NotByTheDetach()
    {
        var steps = Run(Event.EnsureOwned, Event.Attach, Event.Detach, Event.Dispose);

        Assert.DoesNotContain(steps.Take(3), s => s.Work.HasFlag(DisposeTarget));
        Assert.Equal(DisposeTarget, steps[3].Work); // the detach already unbound and stopped
    }

    // ── Before the first attach ─────────────────────────────────────

    [Fact]
    public void NothingPresents_BeforeTheFirstAttach()
    {
        var steps = Run(Event.EnsureOwned, Event.Initialize, Event.Assign, Event.Detach);

        Assert.DoesNotContain(steps, s => s.Work.HasFlag(Start));
        Assert.All(steps, s => Assert.False(s.Next.Presents));
    }

    [Fact]
    public void TheFirstAttach_PresentsWhatWasBoundBeforeIt()
    {
        var steps = Run(Event.EnsureOwned, Event.Attach);

        Assert.Equal(Create | Bind, steps[0].Work);
        Assert.Equal(Start, steps[1].Work); // already bound: the attach starts no binding
        Assert.Equal(steps[0].Next.Binding, steps[1].Next.Binding);
    }

    // ── Initialize, which only the interop view needs ───────────────

    [Fact]
    public void AttachThenInitialize_StartsOnInitialize()
    {
        var steps = RunUninitialized(Event.EnsureOwned, Event.Attach, Event.Initialize);

        Assert.Equal(Create | Bind, steps[0].Work);
        Assert.Equal(Nothing, steps[1].Work); // in the tree, not initialised
        Assert.Equal(Start, steps[2].Work);
    }

    [Fact]
    public void InitializeThenAttach_StartsOnAttach()
    {
        var steps = RunUninitialized(Event.EnsureOwned, Event.Initialize, Event.Attach);

        Assert.Equal(Nothing, steps[1].Work);
        Assert.Equal(Start, steps[2].Work);
    }

    [Fact]
    public void InitializeWhileDetached_WaitsForTheAttach()
    {
        var steps = RunUninitialized(Event.EnsureOwned, Event.Attach, Event.Detach, Event.Initialize, Event.Attach);

        Assert.Equal(Unbind, steps[2].Work); // bound, never presented: nothing to stop
        Assert.Equal(Nothing, steps[3].Work);
        Assert.Equal(Bind | Start, steps[4].Work);
    }

    [Fact]
    public void InitializeWhilePresenting_StartsNothingMore()
    {
        var steps = RunUninitialized(Event.EnsureOwned, Event.Initialize, Event.Attach, Event.Initialize);

        Assert.Equal(Nothing, steps[3].Work);
        Assert.True(steps[3].Next.Presents);
    }

    // ── Assign ──────────────────────────────────────────────────────

    [Fact]
    public void AssignOverAnOwnedSink_DisposesIt_AndPresentsTheNewOne()
    {
        var steps = Run(Event.EnsureOwned, Event.Attach, Event.Assign);

        // Presenting goes on with the new sink: nothing stops or starts.
        Assert.Equal(Unbind | DisposeTarget | Borrow | Bind, steps[2].Work);
        Assert.Equal(SurfaceTarget.Borrowed, steps[2].Next.Target);
        Assert.True(steps[2].Next.Presents);
    }

    [Fact]
    public void AssignOverABorrowedSink_ForgetsIt()
    {
        var steps = Run(Event.Assign, Event.Attach, Event.Assign);

        Assert.Equal(Unbind | Forget | Borrow | Bind, steps[2].Work);
    }

    [Fact]
    public void AssigningNone_StopsPresenting_AndLetsTheSinkGo()
    {
        var steps = Run(Event.EnsureOwned, Event.Attach, Event.AssignNone);

        Assert.Equal(Unbind | Stop | DisposeTarget, steps[2].Work);
        Assert.Equal(SurfaceTarget.None, steps[2].Next.Target);
        Assert.False(steps[2].Next.Presents);
    }

    [Fact]
    public void EnsureOwned_AfterAnAssignReplacedTheOwnedSink_CreatesANewOne()
    {
        var steps = Run(Event.EnsureOwned, Event.Attach, Event.AssignNone, Event.EnsureOwned);

        Assert.Equal(Create | Bind | Start, steps[3].Work);
    }

    [Fact]
    public void EnsureOwned_KeepsACallersSink()
    {
        var steps = Run(Event.Assign, Event.EnsureOwned);

        Assert.Equal(Nothing, steps[1].Work);
        Assert.Equal(SurfaceTarget.Borrowed, steps[1].Next.Target);
    }

    [Fact]
    public void AnAssignWhileDetached_LetsGoAtOnce_AndBindsWhenTheViewRejoins()
    {
        var steps = Run(Event.EnsureOwned, Event.Attach, Event.Detach, Event.Assign, Event.Attach);

        Assert.Equal(DisposeTarget | Borrow, steps[3].Work); // nothing is bound out of the tree
        Assert.Equal(Bind | Start, steps[4].Work);
    }

    // ── Dispose ─────────────────────────────────────────────────────

    [Fact]
    public void DisposeWhilePresenting_StopsAndDisposesAnOwnedSink()
    {
        var dispose = Run(Event.EnsureOwned, Event.Attach, Event.Dispose)[^1];

        Assert.Equal(Unbind | Stop | DisposeTarget, dispose.Work);
        Assert.Equal(SurfacePhase.Disposed, dispose.Next.Phase);
        Assert.Equal(SurfaceTarget.None, dispose.Next.Target);
    }

    [Fact]
    public void DisposeWhilePresenting_ForgetsABorrowedSink()
    {
        var dispose = Run(Event.Assign, Event.Attach, Event.Dispose)[^1];

        Assert.Equal(Unbind | Stop | Forget, dispose.Work);
    }

    [Fact]
    public void DisposingAViewNeverAttached_UnbindsAndDisposesItsSink()
    {
        var dispose = Run(Event.EnsureOwned, Event.Dispose)[^1];

        Assert.Equal(Unbind | DisposeTarget, dispose.Work);
    }

    [Theory]
    [InlineData(Event.Initialize)]
    [InlineData(Event.Assign)]
    [InlineData(Event.EnsureOwned)]
    public void AfterDispose_ANewTargetOrAnInitialize_IsRefused(Event e)
    {
        var disposed = Run(Event.EnsureOwned, Event.Attach, Event.Dispose)[^1].Next;

        var t = Step(disposed, e);

        Assert.True(t.Refused);
        Assert.Equal(Nothing, t.Work);
        Assert.Equal(disposed, t.Next);
    }

    [Theory]
    [InlineData(Event.Attach)]
    [InlineData(Event.Detach)]
    [InlineData(Event.Dispose)]
    [InlineData(Event.AssignNone)]
    public void AfterDispose_EverythingElse_DoesNothing(Event e)
    {
        var disposed = Run(Event.EnsureOwned, Event.Attach, Event.Dispose)[^1].Next;

        var t = Step(disposed, e);

        Assert.False(t.Refused);
        Assert.Equal(Nothing, t.Work);
        Assert.Equal(disposed, t.Next);
    }

    // ── A chrome control: a borrowed transport, no dispose ──────────

    [Fact]
    public void AChromeControl_FollowsATransportFromTheBind_AndTheFirstAttachKeepsThatFollowCurrent()
    {
        // As TransportFollower does: a transport bound before the first attach is followed at
        // once. The attach starts the refresh timer and leaves the binding number alone, so what
        // the follow delivers is not dropped as stale.
        var steps = Run(Event.Assign, Event.Attach);

        Assert.Equal(Borrow | Bind, steps[0].Work);
        Assert.Equal(Start, steps[1].Work);
        Assert.Equal(steps[0].Next.Binding, steps[1].Next.Binding);
    }

    [Fact]
    public void AChromeControl_GivenATransportWhileOut_LetsGoOfTheOldOne_AndFollowsTheNewOneOnReturn()
    {
        var steps = Run(Event.Assign, Event.Attach, Event.Detach, Event.Assign, Event.Attach);

        Assert.Equal(Unbind | Stop, steps[2].Work);
        Assert.Equal(Forget | Borrow, steps[3].Work);
        Assert.Equal(Bind | Start, steps[4].Work);
    }

    [Fact]
    public void AChromeControl_NeverDisposesItsTransport()
    {
        var steps = Run(
            Event.Assign,
            Event.Attach,
            Event.Assign,
            Event.AssignNone,
            Event.Assign,
            Event.Detach,
            Event.Attach,
            Event.Dispose
        );

        Assert.DoesNotContain(steps, s => s.Work.HasFlag(DisposeTarget) || s.Work.HasFlag(Create));
        Assert.Equal(Unbind | Stop | Forget, steps[^1].Work);
    }

    // ── The binding number ──────────────────────────────────────────

    [Fact]
    public void TheBindingNumber_MovesOnEveryBindAndUnbind()
    {
        // Bind, (start), unbind, bind, swap, unbind.
        var steps = Run(Event.Assign, Event.Attach, Event.Detach, Event.Attach, Event.Assign, Event.AssignNone);

        Assert.Equal(new long[] { 1, 1, 2, 3, 4, 5 }, steps.Select(s => s.Next.Binding));
    }

    [Fact]
    public void TheBindingNumber_StaysPut_WhenNoBindingStartsOrEnds()
    {
        var unbound = Run(Event.Attach, Event.Detach, Event.Attach, Event.AssignNone);
        Assert.All(unbound, s => Assert.Equal(0L, s.Next.Binding));

        var initialize = RunUninitialized(Event.EnsureOwned, Event.Attach, Event.Initialize, Event.EnsureOwned);
        Assert.Equal(new long[] { 1, 1, 1, 1 }, initialize.Select(s => s.Next.Binding));
    }

    // ── Every sequence ──────────────────────────────────────────────

    [Fact]
    public void EverySequence_KeepsTheLifecycleInvariants()
    {
        var seen = new HashSet<(SurfacePhase, bool, SurfaceTarget, ShellModel)>();
        foreach (var initial in new[] { SurfaceLifecycle.Initial, SurfaceLifecycle.AwaitingInitialize })
            Walk(initial, initial, ShellModel.Empty, [], seen);

        // The walk reached every phase holding every kind of target, initialised or not, except
        // that a disposed control holds nothing.
        var expected =
            from phase in Enum.GetValues<SurfacePhase>()
            from initialized in new[] { false, true }
            from target in Enum.GetValues<SurfaceTarget>()
            where phase != SurfacePhase.Disposed || target == SurfaceTarget.None
            select (phase, initialized, target);
        var reached = seen.Select(s => (s.Item1, s.Item2, s.Item3)).Distinct();
        Assert.Equal(expected.Order(), reached.Order());
    }

    // Depth first over the states the walk can be in, each visited once. What a transition does,
    // and what Check finds, depends on the lifecycle's phase, initialisation and target, on which
    // of the shell's targets are held and bound, and on the history the model keeps. It does not
    // depend on a target's number, only on which numbers are equal, or on the binding number's
    // value: the machine only adds to it, and Check compares it before and after. So a state is
    // visited once under its canonical key, and that covers every sequence that reaches it, of any
    // length. The path is the sequence that first reached it, for the failure message.
    private static void Walk(
        SurfaceLifecycle initial,
        SurfaceLifecycle state,
        ShellModel shell,
        List<Event> path,
        HashSet<(SurfacePhase, bool, SurfaceTarget, ShellModel)> seen
    )
    {
        if (!seen.Add((state.Phase, state.Initialized, state.Target, Canonical(shell))))
            return;

        foreach (var e in Events)
        {
            path.Add(e);
            var t = Step(state, e);
            var after = Check(initial, state, shell, t, path);
            Walk(initial, t.Next, after, path, seen);
            path.RemoveAt(path.Count - 1);
        }
    }

    // Renumbers the shell's targets: 1 for the one it holds, 2 for one bound but not held, and the
    // next new target 3. Every check compares target numbers only for equality, and a new target's
    // number is unlike any other, so the renumbered model checks the same.
    private static ShellModel Canonical(ShellModel shell) =>
        shell with
        {
            Held = shell.Held == 0 ? 0 : 1,
            Bound = shell.Bound == 0 ? 0 : shell.Bound == shell.Held ? 1 : 2,
            NextTarget = 3,
        };

    /// <summary>
    /// What the shell holds once it has done the work so far. Targets are numbered as they are
    /// created or borrowed, so a number is one object for the whole sequence.
    /// </summary>
    private readonly record struct ShellModel(
        int Held,
        bool HeldOwned,
        int Bound,
        bool Presenting,
        int NextTarget,
        bool EverAttached,
        bool DisposeSeen,
        int CreatesSinceAssign
    )
    {
        public static ShellModel Empty => new(0, false, 0, false, 1, false, false, 0);
    }

    // Does the transition's work on the model, step by step in SurfaceWork's order, checking each
    // step, then checks the state both sides arrive at. Returns the model after the work.
    private static ShellModel Check(
        SurfaceLifecycle initial,
        SurfaceLifecycle before,
        ShellModel shell,
        SurfaceTransition t,
        List<Event> path
    )
    {
        var e = path[^1];
        var work = t.Work;
        var next = t.Next;
        var heldBefore = shell.Held;
        var heldOwnedBefore = shell.HeldOwned;
        var boundBefore = shell.Bound;

        // Built only on a failure.
        void Ensure(bool ok, string what)
        {
            if (!ok)
            {
                Assert.Fail(
                    $"{what}. From {initial}, sequence {string.Join(",", path)}: {e} from {before} "
                        + $"gave {work}{(t.Refused ? " (refused)" : "")}, now {next}, shell {shell}."
                );
            }
        }

        // Refusals, and nothing after a dispose.
        var refusable = e is Event.Initialize or Event.Assign or Event.EnsureOwned;
        Ensure(t.Refused == (before.IsDisposed && refusable), "Refuses exactly a new target or an initialise after a dispose");
        if (before.IsDisposed)
        {
            Ensure(work == SurfaceWork.None, "Disposed does nothing more");
            Ensure(next == before, "Disposed is final and keeps its binding number");
        }

        // The work, in order.
        Ensure(
            !(work.HasFlag(SurfaceWork.StopPresenting) && work.HasFlag(SurfaceWork.StartPresenting)),
            "Never stops and starts presenting at once"
        );
        if (work.HasFlag(SurfaceWork.Unbind))
        {
            Ensure(shell.Bound != 0, "Unbinds only what is bound");
            shell = shell with { Bound = 0 };
        }
        if (work.HasFlag(SurfaceWork.StopPresenting))
        {
            Ensure(shell.Presenting, "Stops only what started");
            shell = shell with { Presenting = false };
        }
        if (work.HasFlag(SurfaceWork.DisposeTarget))
        {
            Ensure(shell.Held != 0 && shell.HeldOwned, "Disposes only a target it owns");
            Ensure(shell.Bound != shell.Held, "Never disposes a bound target");
            Ensure(
                e is Event.Assign or Event.AssignNone or Event.Dispose,
                "Disposes an owned target only when it is replaced or the control is disposed"
            );
            shell = shell with { Held = 0 };
        }
        if (work.HasFlag(SurfaceWork.ForgetTarget))
        {
            Ensure(shell.Held != 0 && !shell.HeldOwned, "Forgets only a borrowed target");
            Ensure(shell.Bound != shell.Held, "Never forgets a bound target");
            Ensure(
                e is Event.Assign or Event.AssignNone or Event.Dispose,
                "Forgets a borrowed target only when it is replaced or the control is disposed"
            );
            shell = shell with { Held = 0 };
        }
        if (work.HasFlag(SurfaceWork.Create))
        {
            Ensure(shell.Held == 0, "Creates only when it holds nothing");
            Ensure(e == Event.EnsureOwned, "Creates only for EnsureOwned");
            shell = shell with
            {
                Held = shell.NextTarget,
                HeldOwned = true,
                NextTarget = shell.NextTarget + 1,
                CreatesSinceAssign = shell.CreatesSinceAssign + 1,
            };
        }
        if (work.HasFlag(SurfaceWork.Borrow))
        {
            Ensure(shell.Held == 0, "Borrows only when it holds nothing");
            Ensure(e == Event.Assign, "Borrows only what a caller assigned");
            shell = shell with { Held = shell.NextTarget, HeldOwned = false, NextTarget = shell.NextTarget + 1 };
        }
        if (work.HasFlag(SurfaceWork.Bind))
        {
            Ensure(shell.Held != 0, "Binds only a target it holds");
            Ensure(shell.Bound == 0, "Binds a target at most once without an unbind between");
            shell = shell with { Bound = shell.Held };
        }
        if (work.HasFlag(SurfaceWork.StartPresenting))
        {
            Ensure(!shell.Presenting, "Never starts over a start");
            Ensure(shell.Bound != 0, "Starts presenting only a bound target");
            shell = shell with { Presenting = true };
        }

        if (!before.IsDisposed && e is (Event.Assign or Event.AssignNone))
            shell = shell with { CreatesSinceAssign = 0 };
        if (next.InTree)
            shell = shell with { EverAttached = true };
        if (e == Event.Dispose)
            shell = shell with { DisposeSeen = true };

        // Ownership: an owned target is created once per ownership and disposed exactly once.
        if (!before.IsDisposed && e is (Event.Assign or Event.AssignNone or Event.Dispose) && heldBefore != 0)
        {
            Ensure(
                work.HasFlag(heldOwnedBefore ? SurfaceWork.DisposeTarget : SurfaceWork.ForgetTarget),
                "A replacement or a dispose lets go of the held target, disposing it only if owned"
            );
        }
        Ensure(shell.CreatesSinceAssign <= 1, "Creates at most one target until an assignment replaces it");
        if (e == Event.EnsureOwned && before.Target != SurfaceTarget.None)
            Ensure(work == SurfaceWork.None, "EnsureOwned keeps the target it has");

        // The state and the shell agree.
        var held = shell.Held == 0
            ? SurfaceTarget.None
            : shell.HeldOwned
                ? SurfaceTarget.Owned
                : SurfaceTarget.Borrowed;
        Ensure(next.Target == held, "The state names the target the shell holds");
        Ensure(next.IsBound == (shell.Bound != 0), "The state says bound exactly while the shell has a target bound");
        Ensure(shell.Bound == 0 || shell.Bound == shell.Held, "Only the held target is bound");
        Ensure(shell.Presenting == next.Presents, "The state says presenting exactly while the shell presents");

        // The next two state the rules rather than derive them: they are the spec for two choices
        // ADR-0085 leaves to this core. A target is bound from the bind, before the first attach
        // as well (a chrome control follows its transport from there), and never out of the tree
        // after a detach. And presenting needs a bound target, so assigning none stops it.
        Ensure(
            next.IsBound == (next.Target != SurfaceTarget.None && next.Phase is SurfacePhase.New or SurfacePhase.Live),
            "Bound exactly while holding a target and neither detached nor disposed"
        );
        Ensure(
            next.Presents == (next.InTree && next.Initialized && shell.Bound != 0),
            "Presents exactly while in the tree, initialised and bound"
        );
        Ensure(shell.EverAttached || !shell.Presenting, "Nothing presents before the first attach");

        // The target follows the control.
        if (e is Event.Initialize or Event.Attach or Event.Detach)
            Ensure(shell.Held == heldBefore, "The target lives as long as the control, not as long as an attachment");
        if (e == Event.Attach && before.Phase == SurfacePhase.Detached && heldBefore != 0)
            Ensure(work.HasFlag(SurfaceWork.Bind) && shell.Bound == heldBefore, "A reattach binds the same target again");
        if (e == Event.Detach && before is { InTree: true, IsBound: true })
            Ensure(work.HasFlag(SurfaceWork.Unbind) && shell.Held == heldBefore, "A detach unbinds and keeps the target");

        // The binding number. Judged on what the shell has bound before and after, not on the
        // work flags, so a needless unbind and rebind of the same target cannot pass as a binding
        // that started.
        if (work.HasFlag(SurfaceWork.Unbind) && work.HasFlag(SurfaceWork.Bind))
            Ensure(shell.Bound != boundBefore, "Never unbinds and rebinds the same target in one transition");
        Ensure(
            next.Binding == before.Binding + (shell.Bound != boundBefore ? 1 : 0),
            "The binding number moves by one exactly when a binding starts or ends"
        );

        // Dispose.
        Ensure(next.IsDisposed == shell.DisposeSeen, "Only a dispose disposes");
        if (next.IsDisposed)
            Ensure(shell is { Held: 0, Bound: 0, Presenting: false }, "A disposed control holds, binds and presents nothing");

        // However the control got here, short of a dispose, it presents once it is in the tree,
        // initialised and holding a target: nothing but a dispose makes it single-use.
        if (!next.IsDisposed)
        {
            var ready = next.Attach().Next.Initialize().Next.EnsureOwned().Next;
            Ensure(ready.Presents, "Presents again once in the tree, initialised and holding a target");
        }

        return shell;
    }
}
