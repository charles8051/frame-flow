using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// A backward step in the primary's time within one run, such as a timestamp wrap or a restarted
/// camera (#92). Pure and synchronous: the window takes no clock and no pump.
/// </summary>
public sealed class SyncJoinBackwardStepTests
{
    private static readonly TimeSpan Window = Ms(100);
    private static readonly TimeSpan Lead = Ms(50);

    [Fact]
    public void AfterAStepBackPastTheWindow_SecondariesOnTheNewTimelineMatch()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var old = RefBox.Of(1000);
        Assert.Null(Admit(window, old));
        Assert.Equal(1000, Match(window, 1010));

        // The source restarts at zero.
        Assert.Null(Match(window, 0));
        Assert.Equal(0, old.RefCount);

        var fresh = RefBox.Of(5);
        Assert.Null(Admit(window, fresh));
        Assert.Equal(5, Match(window, 10));

        window.Clear();
        Assert.Equal(0, fresh.RefCount);
    }

    [Fact]
    public void AfterAStepBack_ASecondaryFromTheOldTimelineIsReleasedRatherThanHeldOnTheLead()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        Assert.Null(Admit(window, RefBox.Of(1000), Lead));
        Assert.Equal(1000, Match(window, 1000));

        // Leads the primary by more than the lead, so the reader holds it.
        var held = RefBox.Of(1100);
        var room = Admit(window, held, Lead);
        Assert.NotNull(room);

        Assert.Null(Match(window, 0));

        // The step releases the reader. Tried again, the held secondary is from the old timeline.
        Assert.True(room.IsCompleted);
        Assert.Null(Admit(window, held, Lead));
        Assert.Equal(0, held.RefCount);

        // So is one still on the edge from before the step.
        var inFlight = RefBox.Of(1020);
        Assert.Null(Admit(window, inFlight, Lead));
        Assert.Equal(0, inFlight.RefCount);

        var fresh = RefBox.Of(5);
        Assert.Null(Admit(window, fresh, Lead));
        Assert.Equal(5, Match(window, 10));

        window.Clear();
        Assert.Equal(0, fresh.RefCount);
    }

    [Fact]
    public void AStepBackWithinTheWindow_KeepsTheWindow()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var entry = RefBox.Of(900);
        Assert.Null(Admit(window, entry));
        Assert.Equal(900, Match(window, 1000));

        Assert.Equal(900, Match(window, 950));
        Assert.Equal(1, entry.RefCount);

        window.Clear();
    }

    [Fact]
    public void OnceThePrimaryClimbsBackToTheOldWindow_ItsTimesAreAdmittedAgain()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        Assert.Null(Admit(window, RefBox.Of(1000)));
        Assert.Equal(1000, Match(window, 1000));
        Assert.Null(Match(window, 0));

        // Ahead of the primary and inside the old timeline's window, which began at 900.
        var early = RefBox.Of(950);
        Assert.Null(Admit(window, early));
        Assert.Equal(0, early.RefCount);

        Assert.Null(Match(window, 900));

        var later = RefBox.Of(950);
        Assert.Null(Admit(window, later));
        Assert.Equal(1, later.RefCount);
        Assert.Equal(950, Match(window, 960));

        window.Clear();
        Assert.Equal(0, later.RefCount);
    }

    [Fact]
    public void AfterAStepBack_AnIntervalThatCoversThePrimaryIsKept()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        Assert.Null(Admit(window, RefBox.Of(1000)));
        Assert.Equal(1000, Match(window, 1000));
        Assert.Null(Match(window, 0));
        Assert.Null(Match(window, 850));

        // Ends inside the old timeline's window, but starts at or before the primary.
        var caption = RefBox.Of(840);
        Assert.Null(
            window.TryAdmit(caption, Ms(840), Ms(950), Window, maxLead: null, maxRetained: null, SyncMatch.Within, TimeSpan.MaxValue)
        );
        using var match = window.AdvanceAndMatch(Ms(860), SyncMatch.Within, Window, TimeSpan.MaxValue);
        Assert.Same(caption, match);

        window.Clear();
    }

    [Fact]
    public void ASecondaryMoreThanTheWindowLate_IsReleasedOnArrival_SoItCannotMatchAfterAStepBack()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        Assert.Null(Admit(window, RefBox.Of(1000)));
        Assert.Equal(1000, Match(window, 1000));

        // The window begins at 900, so the next primary would evict this before it could match.
        var late = RefBox.Of(850);
        Assert.Null(Admit(window, late));
        Assert.Equal(0, late.RefCount);

        // Retained, it would pair with a primary on the new timeline.
        Assert.Null(Match(window, 800));
        Assert.Null(Match(window, 850));
    }

    [Fact]
    public void ASecondStepBack_KeepsTheFirstTimelinesBoundaryUntilThePrimaryReachesIt()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        Assert.Null(Admit(window, RefBox.Of(1000)));
        Assert.Equal(1000, Match(window, 1000));

        // Two steps: the first timeline's window began at 900, the second's at 400.
        Assert.Null(Match(window, 0));
        Assert.Null(Match(window, 500));
        Assert.Null(Match(window, 300));
        Assert.Null(Match(window, 400));

        // Past the second boundary, a secondary from the first timeline is still released.
        var first = RefBox.Of(950);
        Assert.Null(Admit(window, first));
        Assert.Equal(0, first.RefCount);

        Assert.Null(Match(window, 900));
        var later = RefBox.Of(950);
        Assert.Null(Admit(window, later));
        Assert.Equal(1, later.RefCount);

        window.Clear();
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static Task? Admit(SecondaryWindow<RefBox<int>> window, RefBox<int> point, TimeSpan? maxLead = null) =>
        window.TryAdmit(
            point,
            Ms(point.Value),
            Ms(point.Value),
            Window,
            maxLead,
            maxRetained: null,
            SyncMatch.MostRecentAtOrBefore,
            TimeSpan.MaxValue
        );

    private static int? Match(SecondaryWindow<RefBox<int>> window, int ms)
    {
        using var match = window.AdvanceAndMatch(
            Ms(ms),
            SyncMatch.MostRecentAtOrBefore,
            Window,
            TimeSpan.MaxValue
        );
        return match?.Value;
    }
}
