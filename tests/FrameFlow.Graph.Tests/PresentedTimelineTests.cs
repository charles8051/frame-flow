using FrameFlow.Graph.Core;
using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// <see cref="PresentedTimeline"/> (ADR-0084, decision 5): order comes from what is presented. An
/// item is current when its timeline is on screen, past when the screen has left it, and upcoming
/// otherwise; <see cref="TimelineId.None"/> on either side is current.
/// </summary>
public sealed class PresentedTimelineTests
{
    private static readonly TimelineId A = TimelineId.Next();
    private static readonly TimelineId B = TimelineId.Next();
    private static readonly TimelineId C = TimelineId.Next();
    private static readonly TimelineId D = TimelineId.Next();
    private static readonly TimelineId E = TimelineId.Next();
    private static readonly TimelineId None = TimelineId.None;

    [Fact]
    public void BeforeAnythingIsPresented_EveryItemIsCurrent()
    {
        var screen = new PresentedTimeline();

        Assert.Equal(None, screen.Current);
        Assert.Equal(PresentedTimeline.DefaultHistory, screen.HistoryCapacity);
        Assert.All([None, A, B], id => Assert.Equal(TimelineRelation.Current, screen.Classify(id)));
    }

    [Fact]
    public void AnItemOnNone_IsAlwaysCurrent()
    {
        var screen = new PresentedTimeline().Present(A).Present(B);

        Assert.Equal(TimelineRelation.Current, screen.Classify(None));
    }

    [Fact]
    public void TheTimelineOnScreen_IsCurrent_AndOneNeverPresented_IsUpcoming()
    {
        var screen = new PresentedTimeline().Present(A);

        Assert.Equal(A, screen.Current);
        Assert.Equal(TimelineRelation.Current, screen.Classify(A));
        Assert.Equal(TimelineRelation.Upcoming, screen.Classify(B));
    }

    [Fact]
    public void AnItemOfALeftTimeline_ThatArrivesAfterTheNextTimelinesFirstFrame_IsPast()
    {
        // A result for a frame of A, delayed on a path that reorders, arrives once B is on screen.
        var screen = new PresentedTimeline().Present(A).Present(B);

        Assert.Equal(TimelineRelation.Past, screen.Classify(A));
        Assert.Equal(TimelineRelation.Current, screen.Classify(B));
    }

    [Fact]
    public void AnItemWhoseTimelineFellOutOfTheHistory_IsUpcoming_AndNeverBecomesCurrent()
    {
        // History of two: after A, B, C, D the screen remembers C and B. A reads as upcoming, so a
        // consumer holds it within its own bounds; it is never shown, because A never comes back.
        var screen = new PresentedTimeline(history: 2).Present(A).Present(B).Present(C).Present(D);

        Assert.Equal(TimelineRelation.Upcoming, screen.Classify(A));
        Assert.Equal(TimelineRelation.Past, screen.Classify(B));
        Assert.Equal(TimelineRelation.Past, screen.Classify(C));

        foreach (var next in new[] { E, TimelineId.Next() })
        {
            screen = screen.Present(next);
            Assert.NotEqual(TimelineRelation.Current, screen.Classify(A));
        }
    }

    [Fact]
    public void AnIdMintedEarlier_IsUpcomingUntilItIsPresented()
    {
        // An item prepared ahead mints its id before the playing item loops (#674). Its items wait
        // as upcoming while the loop plays, become current when it is promoted and presented, and
        // the loop's late items are then past.
        var prepared = TimelineId.Next();
        var firstLoop = TimelineId.Next();
        var secondLoop = TimelineId.Next();

        var screen = new PresentedTimeline().Present(firstLoop).Present(secondLoop);
        Assert.Equal(TimelineRelation.Upcoming, screen.Classify(prepared));

        screen = screen.Present(prepared);
        Assert.Equal(TimelineRelation.Current, screen.Classify(prepared));
        Assert.Equal(TimelineRelation.Past, screen.Classify(secondLoop));
        Assert.Equal(TimelineRelation.Past, screen.Classify(firstLoop));
    }

    [Fact]
    public void PresentingNoneOrTheCurrentTimeline_ChangesNothing()
    {
        var screen = new PresentedTimeline().Present(A);

        Assert.Same(screen, screen.Present(A));
        Assert.Same(screen, screen.Present(None));

        var empty = new PresentedTimeline();
        Assert.Same(empty, empty.Present(None));
    }

    [Fact]
    public void ATimelinePresentedAgain_IsCurrent_AndTheOneItReplacedIsPast()
    {
        var screen = new PresentedTimeline().Present(A).Present(B).Present(A);

        Assert.Equal(A, screen.Current);
        Assert.Equal(TimelineRelation.Current, screen.Classify(A));
        Assert.Equal(TimelineRelation.Past, screen.Classify(B));
    }

    [Fact]
    public void PresentingLeavesTheEarlierValueAsItWas()
    {
        var before = new PresentedTimeline().Present(A);
        _ = before.Present(B);

        Assert.Equal(A, before.Current);
        Assert.Equal(TimelineRelation.Upcoming, before.Classify(B));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AHistoryOfLessThanOne_IsRefused(int history) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentedTimeline(history));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EverySequenceOfPresents_ClassifiesEveryIdAsTheModelDoes(int history)
    {
        // Exhaustive over five presents from {none, a, b, c, d}, read against a model built from
        // the whole sequence rather than step by step: the timeline on screen is the last one
        // presented, and the past is the distinct timelines left, most recently left first, up to
        // the history.
        TimelineId[] alphabet = [None, A, B, C, D];
        TimelineId[] probes = [None, A, B, C, D, E];
        foreach (var presents in TimelineFollowerTests.Sequences(alphabet, 5))
        {
            var screen = new PresentedTimeline(history);
            for (int n = 1; n <= presents.Length; n++)
            {
                screen = screen.Present(presents[n - 1]);
                var (current, past) = Model(presents[..n], history);

                Assert.Equal(current, screen.Current);
                foreach (var probe in probes)
                {
                    var expected =
                        probe == None || current == None || probe == current ? TimelineRelation.Current
                        : past.Contains(probe) ? TimelineRelation.Past
                        : TimelineRelation.Upcoming;
                    Assert.Equal(expected, screen.Classify(probe));
                }
            }
        }
    }

    private static (TimelineId Current, IReadOnlyList<TimelineId> Past) Model(TimelineId[] presents, int history)
    {
        // The timelines in the order they took the screen, each change once.
        var shown = new List<TimelineId>();
        foreach (var id in presents)
        {
            if (id != None && (shown.Count == 0 || shown[^1] != id))
                shown.Add(id);
        }

        if (shown.Count == 0)
            return (None, []);

        var current = shown[^1];
        var past = new List<TimelineId>();
        for (int i = shown.Count - 2; i >= 0 && past.Count < history; i--)
        {
            if (shown[i] != current && !past.Contains(shown[i]))
                past.Add(shown[i]);
        }

        return (current, past);
    }
}
