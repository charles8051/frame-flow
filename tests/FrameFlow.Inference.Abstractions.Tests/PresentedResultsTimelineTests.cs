using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using FrameFlow.Media;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// <see cref="PresentedResults{TResult}"/> follows the timeline on screen (ADR-0084, decision 5,
/// #621). The rules are tested on the pure <see cref="PresentedResultsState{TResult}"/>; the last
/// tests drive the class through a presenter that raises inline, for its events and its wiring.
/// </summary>
public sealed class PresentedResultsTimelineTests
{
    // ── The state ───────────────────────────────────────────────────────

    [Fact]
    public void TheResultShowing_IsClearedWhenANewTimelineReachesTheScreen()
    {
        // #621: a replace during the old item's first frames. The old result for 0 ms would match
        // the new item's frame at 0 ms by time.
        var (a, b) = (TimelineId.Next(), TimelineId.Next());
        var state = Empty().Post(Result("old item", 0, a));
        (state, var change) = state.Present(Ms(0), a);
        Assert.Equal(PresentedChange.Presented, change);

        (state, change) = state.Present(Ms(0), b);

        Assert.Equal(PresentedChange.Cleared, change);
        Assert.Null(state.Current);
    }

    [Fact]
    public void ANewTimelineOnScreen_DropsWhatWaitsFromTheOneItLeft()
    {
        // A seek forward: the old timeline's results ran ahead of its picture, and are earlier
        // than the new position, so by time they would match it.
        var (a, b) = (TimelineId.Next(), TimelineId.Next());
        var state = Empty().Post(Result("ahead", 1000, a)).Post(Result("further ahead", 1040, a));
        (state, _) = state.Present(Ms(990), a);

        (state, var change) = state.Present(Ms(5000), b);

        Assert.Equal(PresentedChange.None, change);
        Assert.Null(state.Current);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public void AResultOfATimelineTheScreenHasLeft_IsDroppedWhenPosted()
    {
        // Still in flight from the inference branch when the next timeline reached the screen.
        var (a, b) = (TimelineId.Next(), TimelineId.Next());
        var (state, _) = Empty().Present(Ms(900), a);
        (state, _) = state.Present(Ms(0), b);

        state = state.Post(Result("late", 100, a));
        Assert.Empty(state.Pending);

        (state, var change) = state.Present(Ms(200), b);
        Assert.Equal(PresentedChange.None, change);
        Assert.Null(state.Current);
    }

    [Fact]
    public void AResultOfATimelineNotYetShown_WaitsForIt_AndLeavesTheOneOnScreenAlone()
    {
        // A loop's first results arrive while the last frames of the pass before still present.
        // The next timeline's id was made first, as for an item prepared ahead (#674): ids carry no
        // order, the screen does.
        var next = TimelineId.Next();
        var playing = TimelineId.Next();
        var state = Empty().Post(Result("end of pass", 900, playing));
        (state, _) = state.Present(Ms(900), playing);

        state = state.Post(Result("next pass", 0, next)).Post(Result("next pass, later", 40, next));
        (state, var change) = state.Present(Ms(920), playing);

        Assert.Equal(PresentedChange.None, change);
        Assert.Equal("end of pass", state.Current?.Result);

        (state, change) = state.Present(Ms(0), next);
        Assert.Equal(PresentedChange.Presented, change);
        Assert.Equal("next pass", state.Current?.Result);

        (state, _) = state.Present(Ms(40), next);
        Assert.Equal("next pass, later", state.Current?.Result);
    }

    [Fact]
    public void AShortSeekForwardsFirstResult_DoesNotShowOnTheFramesStillPresentingBeforeIt()
    {
        // By time, the new position's result at 150 ms is the latest at or before the old
        // timeline's frame at 160 ms.
        var (before, after) = (TimelineId.Next(), TimelineId.Next());
        var state = Empty().Post(Result("before the seek", 100, before));
        (state, _) = state.Present(Ms(100), before);

        state = state.Post(Result("after the seek", 150, after));
        (state, var change) = state.Present(Ms(160), before);

        Assert.Equal(PresentedChange.None, change);
        Assert.Equal("before the seek", state.Current?.Result);

        (state, _) = state.Present(Ms(150), after);
        Assert.Equal("after the seek", state.Current?.Result);
    }

    [Fact]
    public void AResultOfATimelineTheScreenNoLongerRemembers_WaitsAndNeverShows()
    {
        var (a, b, c) = (TimelineId.Next(), TimelineId.Next(), TimelineId.Next());
        var (state, _) = PresentedResultsState<string>.Empty(capacity: 8, history: 1).Present(Ms(0), a);
        (state, _) = state.Present(Ms(0), b);
        (state, _) = state.Present(Ms(0), c);

        state = state.Post(Result("forgotten", 0, a));
        Assert.Single(state.Pending);

        (state, var change) = state.Present(Ms(100), c);
        Assert.Equal(PresentedChange.None, change);
        Assert.Null(state.Current);
    }

    [Fact]
    public void AResultOnNone_BelongsToTheTimelineOnScreen()
    {
        var a = TimelineId.Next();
        var (state, _) = Empty().Present(Ms(0), a);

        state = state.Post(Result("untracked", 0, TimelineId.None));
        (state, var change) = state.Present(Ms(10), a);

        Assert.Equal(PresentedChange.Presented, change);
        Assert.Equal("untracked", state.Current?.Result);
    }

    [Fact]
    public void APresenterThatNamesNoTimeline_KeepsTheTimestampRules_ForResultsThatNameOne()
    {
        // The presenter cannot say which timeline is on screen, so a result earlier than the last
        // still means the timeline went back, whatever the results carry.
        var (a, b) = (TimelineId.Next(), TimelineId.Next());
        var state = Empty().Post(Result("first pass", 0, a)).Post(Result("first pass, late", 120, a));

        state = state.Post(Result("second pass", 0, b));

        // Past the first pass's late result, which would match here had it been kept.
        (state, _) = state.Present(Ms(130), TimelineId.None);

        Assert.Equal("second pass", state.Current?.Result);
    }

    [Fact]
    public void OnATimelineTheScreenFollows_AnotherTimelinesEarlierResultStartsNothingOver()
    {
        // The same posts as above with the screen on the first pass: the second pass's result
        // waits, and the first pass keeps its own.
        var (a, b) = (TimelineId.Next(), TimelineId.Next());
        var (state, _) = Empty().Present(Ms(0), a);
        state = state.Post(Result("first pass", 0, a)).Post(Result("first pass, late", 120, a));

        state = state.Post(Result("second pass", 0, b));
        (state, _) = state.Present(Ms(130), a);

        Assert.Equal("first pass, late", state.Current?.Result);
        Assert.Contains(state.Pending, p => p.Result.Result == "second pass");
    }

    [Fact]
    public void OnATimelineTheScreenFollows_AResultOnNonePostedEarlier_StillShowsAtItsOwnTime()
    {
        // A result on None and one on the timeline on screen are both current, but the time rules
        // keep them apart, so they are posted in no common order. The later one in time, posted
        // first, has not been passed over when the earlier one shows.
        var a = TimelineId.Next();
        var (state, _) = Empty().Present(Ms(0), a);
        state = state.Post(Result("untracked, later", 200, TimelineId.None)).Post(Result("tracked", 100, a));

        (state, _) = state.Present(Ms(150), a);
        Assert.Equal("tracked", state.Current?.Result);

        (state, var change) = state.Present(Ms(210), a);
        Assert.Equal(PresentedChange.Presented, change);
        Assert.Equal("untracked, later", state.Current?.Result);
    }

    [Fact]
    public void OnATimelineTheScreenFollows_AResultOnNonePostedLater_ButEarlierInTime_IsPassedOver()
    {
        // The other order: once a frame shows the result at 100 ms, one for 50 ms cannot show
        // again, and is not left to wait until the capacity evicts it.
        var a = TimelineId.Next();
        var (state, _) = Empty().Present(Ms(0), a);
        state = state.Post(Result("tracked", 100, a)).Post(Result("untracked, earlier", 50, TimelineId.None));

        (state, _) = state.Present(Ms(120), a);

        Assert.Equal("tracked", state.Current?.Result);
        Assert.DoesNotContain(state.Pending, p => p.Result.Result == "untracked, earlier");
    }

    [Fact]
    public void AFrameThatChangesNothing_LeavesItsStateAlone()
    {
        // The present path runs for every frame; most change nothing, and allocate nothing.
        var a = TimelineId.Next();
        var state = Empty().Post(Result("a", 0, a));
        (state, _) = state.Present(Ms(0), a);

        var (next, change) = state.Present(Ms(10), a);

        Assert.Equal(PresentedChange.None, change);
        Assert.Same(state, next);
    }

    // ── The class ───────────────────────────────────────────────────────

    [Fact]
    public void AReplaceInTheOldItemsFirstFrames_DoesNotShowItsResultOnTheNewItem()
    {
        // #621, end to end: Cleared, then the new item's own result once it is posted.
        var (a, b) = (TimelineId.Next(), TimelineId.Next());
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        var seen = new List<string>();
        onScreen.Presented += (_, result) => seen.Add(result.Result);
        onScreen.Cleared += (_, _) => seen.Add("cleared");

        onScreen.Post(Result("old item", 0, a));
        presenter.Present(0, a);
        presenter.Present(0, b);
        Assert.Null(onScreen.Current);

        onScreen.Post(Result("new item", 0, b));
        presenter.Present(40, b);

        Assert.Equal(["old item", "cleared", "new item"], seen);
    }

    [Fact]
    public void AResultInFlightFromTheOldItem_IsNotShownOnTheNewOne()
    {
        var (a, b) = (TimelineId.Next(), TimelineId.Next());
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        presenter.Present(500, a);
        presenter.Present(0, b);

        onScreen.Post(Result("old item", 0, a));
        presenter.Present(40, b);

        Assert.Null(onScreen.Current);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static PresentedResultsState<string> Empty() => PresentedResultsState<string>.Empty(capacity: 64);

    private static InferenceResult<string> Result(string value, int ms, TimelineId timeline) =>
        new(value, Ms(ms), 4, 4, InferencePath.Host) { Timeline = timeline };

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private sealed class FakePresenter : IFramePresentedSource
    {
        public event EventHandler<FramePresentedInfo>? FramePresented;

        public void Present(int ms, TimelineId timeline) =>
            FramePresented?.Invoke(
                this,
                new FramePresentedInfo(Ms(ms), PresentedAtUtc: default) { Timeline = timeline }
            );
    }
}
