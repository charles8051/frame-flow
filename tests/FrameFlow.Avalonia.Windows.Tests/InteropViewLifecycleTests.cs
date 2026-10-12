// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Avalonia.Windows.Core;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The lifecycle of <see cref="CompositionInteropVideoView"/> as a pure state machine (#650): which
/// state <c>Initialize</c>, an attach, a detach and <c>DisposeAsync</c> move the view to, and what
/// each sets up or tears down. The view used to treat its first detach as a dispose, so a view that
/// rejoined the visual tree presented nothing, and a second detach released nothing.
/// </summary>
/// <remarks>
/// The named cases pin the sequences the issue describes. <see cref="EverySequence_KeepsTheLifecycleInvariants"/>
/// then walks every sequence of the four events up to <see cref="MaxSequenceLength"/> long and checks
/// the properties the shell relies on after every step.
/// </remarks>
public sealed class InteropViewLifecycleTests
{
    public enum Event
    {
        Initialize,
        Attach,
        Detach,
        Dispose,
    }

    private const int MaxSequenceLength = 8;

    private static InteropViewTransition Step(InteropViewLifecycle state, Event e) => e switch
    {
        Event.Initialize => state.Initialize(),
        Event.Attach => state.Attach(),
        Event.Detach => state.Detach(),
        Event.Dispose => state.Dispose(),
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    private static List<InteropViewTransition> Run(params Event[] events)
    {
        var state = InteropViewLifecycle.Initial;
        var steps = new List<InteropViewTransition>();
        foreach (var e in events)
        {
            var t = Step(state, e);
            steps.Add(t);
            state = t.Next;
        }
        return steps;
    }

    private const InteropViewWork Release = InteropViewWork.ReleaseSink;
    private const InteropViewWork Forget = InteropViewWork.ForgetCallerSink;
    private const InteropViewWork Stop = InteropViewWork.StopPresenting;
    private const InteropViewWork Start = InteropViewWork.StartPresenting;

    // ── The issue's sequence ────────────────────────────────────────

    [Fact]
    public void AViewThatRejoinsTheTree_PresentsAgain()
    {
        var steps = Run(Event.Initialize, Event.Attach, Event.Detach, Event.Attach);

        Assert.Equal(InteropViewWork.None, steps[0].Work); // not in the tree yet
        Assert.Equal(Start, steps[1].Work);
        Assert.Equal(Release | Stop, steps[2].Work);
        Assert.Equal(InteropViewPhase.Detached, steps[2].Next.Phase);
        Assert.True(steps[2].Next.HoldsFrames);
        Assert.Equal(Start, steps[3].Work);
        Assert.Equal(InteropViewPhase.Presenting, steps[3].Next.Phase);
        Assert.False(steps[3].Next.HoldsFrames);
    }

    [Fact]
    public void ASecondDetach_TearsDownWhatTheSecondAttachmentMade()
    {
        var steps = Run(Event.Initialize, Event.Attach, Event.Detach, Event.Attach, Event.Detach);

        Assert.Equal(Release | Stop, steps[4].Work);
        Assert.Equal(InteropViewPhase.Detached, steps[4].Next.Phase);
    }

    [Fact]
    public void ADetach_KeepsACallersSink()
    {
        var detach = Run(Event.Initialize, Event.Attach, Event.Detach)[^1];

        Assert.False(detach.Work.HasFlag(Forget));
    }

    [Fact]
    public void ADetach_IsNotADispose()
    {
        var detach = Run(Event.Initialize, Event.Attach, Event.Detach)[^1];

        Assert.NotEqual(InteropViewPhase.Disposed, detach.Next.Phase);
        Assert.True(detach.Next.Initialized);
        Assert.False(detach.Next.Attached);
    }

    // ── Either order of Initialize and attach ───────────────────────

    [Fact]
    public void AttachThenInitialize_StartsOnInitialize()
    {
        var steps = Run(Event.Attach, Event.Initialize);

        Assert.Equal(InteropViewWork.None, steps[0].Work);
        Assert.Equal(Start, steps[1].Work);
    }

    [Fact]
    public void InitializeWhileDetached_WaitsForTheAttach()
    {
        var steps = Run(Event.Initialize, Event.Attach, Event.Detach, Event.Initialize, Event.Attach);

        Assert.Equal(InteropViewWork.None, steps[3].Work);
        Assert.Equal(Start, steps[4].Work);
    }

    [Fact]
    public void InitializeWhilePresenting_StartsNothingMore()
    {
        var steps = Run(Event.Initialize, Event.Attach, Event.Initialize);

        Assert.Equal(InteropViewWork.None, steps[2].Work);
        Assert.Equal(InteropViewPhase.Presenting, steps[2].Next.Phase);
    }

    // ── A view that never presented ─────────────────────────────────

    [Fact]
    public void TheFirstDetachOfAViewNeverInitialized_StillRunsTheTeardown()
    {
        // As it always has: the teardown reports how it went whether or not anything was set up.
        var steps = Run(Event.Attach, Event.Detach, Event.Attach, Event.Detach);

        Assert.Equal(Release | Stop, steps[1].Work);
        Assert.Equal(InteropViewWork.None, steps[2].Work); // still not initialized
        Assert.Equal(Release, steps[3].Work); // nothing started since the first teardown
    }

    [Fact]
    public void DisposingAViewNeverAttached_RunsTheTeardown()
    {
        var steps = Run(Event.Initialize, Event.Dispose);

        Assert.Equal(Release | Forget | Stop, steps[1].Work);
        Assert.Equal(InteropViewPhase.Disposed, steps[1].Next.Phase);
    }

    // ── Dispose ─────────────────────────────────────────────────────

    [Fact]
    public void DisposeWhilePresenting_TearsDownAndForgetsTheSink()
    {
        var dispose = Run(Event.Initialize, Event.Attach, Event.Dispose)[^1];

        Assert.Equal(Release | Forget | Stop, dispose.Work);
        Assert.True(dispose.Next.HoldsFrames);
    }

    [Fact]
    public void DisposeAfterADetach_ReleasesTheSinkWithoutASecondTeardown()
    {
        // A sink made since the detach still has to go; the compositor side is already down.
        var dispose = Run(Event.Initialize, Event.Attach, Event.Detach, Event.Dispose)[^1];

        Assert.Equal(Release | Forget, dispose.Work);
    }

    [Theory]
    [InlineData(Event.Initialize)]
    [InlineData(Event.Attach)]
    [InlineData(Event.Detach)]
    [InlineData(Event.Dispose)]
    public void AfterDispose_NothingStartsOrStopsAgain(Event e)
    {
        var disposed = Run(Event.Initialize, Event.Attach, Event.Dispose)[^1].Next;

        var t = Step(disposed, e);

        Assert.Equal(InteropViewWork.None, t.Work);
        Assert.Equal(InteropViewPhase.Disposed, t.Next.Phase);
    }

    [Fact]
    public void ADisposedViewThatLeavesTheTree_DoesNothing()
    {
        var steps = Run(Event.Initialize, Event.Attach, Event.Dispose, Event.Detach, Event.Attach);

        Assert.Equal(InteropViewWork.None, steps[3].Work);
        Assert.Equal(InteropViewWork.None, steps[4].Work);
    }

    // ── Every sequence ──────────────────────────────────────────────

    [Fact]
    public void EverySequence_KeepsTheLifecycleInvariants()
    {
        var sequences = 0;
        foreach (var events in Sequences(MaxSequenceLength))
        {
            sequences++;
            CheckInvariants(events);
        }

        Assert.Equal(Enumerable.Range(0, MaxSequenceLength + 1).Sum(n => (int)Math.Pow(4, n)), sequences);
    }

    private static void CheckInvariants(Event[] events)
    {
        var state = InteropViewLifecycle.Initial;
        var surfaceUp = false; // a start not yet followed by its stop
        var teardownsSinceStart = 0;
        var disposeSeen = false;

        foreach (var e in events)
        {
            var before = state;
            var t = Step(state, e);
            state = t.Next;
            var work = t.Work;

            // Built only on a failure: there are tens of thousands of steps.
            void Ensure(bool ok, string what)
            {
                if (!ok)
                    Assert.Fail($"{what}. Sequence {string.Join(",", events)}: {e} from {before} gave {work}, now {state}.");
            }

            Ensure(
                (state.Initialized && state.Attached && state.Phase != InteropViewPhase.Disposed)
                    == (state.Phase == InteropViewPhase.Presenting),
                "Presents exactly while initialized, in the tree and not disposed");

            Ensure(
                state.HoldsFrames == (state.Phase is InteropViewPhase.Detached or InteropViewPhase.Disposed),
                "Holds frames exactly while the compositor side is torn down");

            if (before.Phase == InteropViewPhase.Disposed)
            {
                Ensure(work == InteropViewWork.None, "Disposed does nothing more");
                Ensure(state.Phase == InteropViewPhase.Disposed, "Disposed is final");
            }

            Ensure(
                work.HasFlag(Start) == (before.Phase != InteropViewPhase.Presenting && state.Phase == InteropViewPhase.Presenting),
                "Starts exactly when the view starts presenting");
            Ensure(!(work.HasFlag(Start) && work.HasFlag(Stop)), "Never starts and stops at once");
            if (work.HasFlag(Start))
            {
                Ensure(!surfaceUp, "Never starts over a surface that is up");
                surfaceUp = true;
                teardownsSinceStart = 0;
            }

            if (before.Phase == InteropViewPhase.Presenting && state.Phase != InteropViewPhase.Presenting)
                Ensure(work.HasFlag(Stop), "Leaving Presenting tears down");
            if (work.HasFlag(Stop))
            {
                surfaceUp = false;
                teardownsSinceStart++;
                Ensure(teardownsSinceStart == 1, "Tears down once per start");
            }

            bool live = before.Phase != InteropViewPhase.Disposed;
            Ensure(
                work.HasFlag(Release) == (live && e is Event.Detach or Event.Dispose),
                "Every detach and dispose of a live view releases the sink");
            Ensure(
                work.HasFlag(Forget) == (live && e == Event.Dispose),
                "Only a dispose forgets a caller's sink");

            if (!state.Attached || state.Phase == InteropViewPhase.Disposed)
                Ensure(!surfaceUp, "Nothing stays up out of the tree or after a dispose");

            disposeSeen |= e == Event.Dispose;
            Ensure((state.Phase == InteropViewPhase.Disposed) == disposeSeen, "Only DisposeAsync disposes");

            // However the view got here, short of a dispose, it presents again once it is
            // initialized and in the tree: nothing but DisposeAsync makes it single-use.
            if (!disposeSeen)
            {
                var ready = state.Initialize().Next.Attach().Next;
                Ensure(ready.Phase == InteropViewPhase.Presenting, "Presents again once initialized and attached");
            }
        }
    }

    private static IEnumerable<Event[]> Sequences(int maxLength)
    {
        var all = Enum.GetValues<Event>();
        IEnumerable<Event[]> level = [[]];
        for (var n = 0; n <= maxLength; n++)
        {
            foreach (var s in level)
                yield return s;
            level = level.SelectMany(s => all.Select(e => (Event[])[.. s, e])).ToList();
        }
    }
}
