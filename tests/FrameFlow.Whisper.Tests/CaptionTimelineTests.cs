using FrameFlow.Graph.Core;
using FrameFlow.Whisper.Core;

namespace FrameFlow.Whisper.Tests;

/// <summary>
/// Which captions show at a presented frame: each from the frame it arrived with for the display
/// duration, and none from before a seek, a loop or the next item, whether the frames and captions
/// name their timelines or only the time shows the jump.
/// </summary>
public sealed class CaptionTimelineTests
{
    private static readonly TimeSpan Display = TimeSpan.FromSeconds(6);

    private const int Fps = 30;

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    private static TimeSpan Frame(int index) => TimeSpan.FromTicks(index * TimeSpan.TicksPerSecond / Fps);

    private static Caption Said(string text) => new(TimeSpan.Zero, TimeSpan.Zero, text);

    private static string[] Texts(IReadOnlyList<Caption> captions) => captions.Select(c => c.Text).ToArray();

    private static string[] ShownAt(CaptionTimeline timeline, TimeSpan pts) => Texts(timeline.GetActive(pts));

    private static string[] ShownAt(CaptionTimeline timeline, double seconds) => ShownAt(timeline, Seconds(seconds));

    /// <summary>What a presenter's frame callback does: add what has arrived, then ask what to show.</summary>
    private static string[] Present(CaptionTimeline timeline, TimeSpan pts, params string[] arrived)
    {
        foreach (var text in arrived)
            timeline.Add(Said(text), pts);
        return ShownAt(timeline, pts);
    }

    // ─── One run of playback ─────────────────────────────────────────

    [Fact]
    public void NothingAdded_ShowsNothing()
    {
        var timeline = new CaptionTimeline(Display);

        Assert.Empty(timeline.GetActive(Seconds(3)));
    }

    [Fact]
    public void Caption_ShowsFromItsArrival_ForTheDisplayDuration()
    {
        var timeline = new CaptionTimeline(Display);

        Assert.Equal(["hello"], Present(timeline, Seconds(10), "hello"));
        Assert.Equal(["hello"], ShownAt(timeline, 13));
        Assert.Equal(["hello"], ShownAt(timeline, 16));
        Assert.Empty(ShownAt(timeline, Seconds(16) + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Add_RejectsNull()
    {
        var timeline = new CaptionTimeline(Display);

        Assert.Throws<ArgumentNullException>(() => timeline.Add(null!, Seconds(1)));
    }

    [Fact]
    public void OneLine_ShowsTheLatestBatchTogether_UntilTheNextArrives()
    {
        var timeline = new CaptionTimeline(Display);

        Assert.Equal(["one.", "two."], Present(timeline, Seconds(10), "one.", "two."));
        Assert.Equal(["one.", "two."], ShownAt(timeline, 11));
        Assert.Equal(["three."], Present(timeline, Seconds(12), "three."));
    }

    [Fact]
    public void Stack_ShowsTheLatestCaptions_OldestFirst_UntilEachAgesOut()
    {
        var timeline = new CaptionTimeline(Display, maxStackedLines: 2);

        Assert.Equal(["a"], Present(timeline, Seconds(10), "a"));
        Assert.Equal(["a", "b"], Present(timeline, Seconds(11), "b"));
        Assert.Equal(["b", "c"], Present(timeline, Seconds(12), "c"));
        Assert.Equal(["b", "c"], ShownAt(timeline, 17));
        Assert.Equal(["c"], ShownAt(timeline, 17.5));
        Assert.Empty(ShownAt(timeline, 18.5));
    }

    [Fact]
    public void SamePtsAgain_KeepsTheTimeline()
    {
        var timeline = new CaptionTimeline(Display);

        Assert.Equal(["a"], Present(timeline, Seconds(10), "a"));
        Assert.Equal(["a"], ShownAt(timeline, 10));
        Assert.Equal(["a", "b"], Present(timeline, Seconds(10), "b"));
    }

    [Fact]
    public void TimesBeforeZero_ShowLikeAnyOther()
    {
        var timeline = new CaptionTimeline(Display);

        Assert.Equal(["early"], Present(timeline, Seconds(-0.5), "early"));
        Assert.Equal(["early"], ShownAt(timeline, 5.5));
        Assert.Empty(ShownAt(timeline, 5.6));
    }

    // ─── Seek back, loop, next item ──────────────────────────────────

    [Fact]
    public void SeekBack_CaptionArrivingWithTheFirstFrameAfterIt_Shows()
    {
        var timeline = new CaptionTimeline(Display);
        Present(timeline, Seconds(40), "before");
        Assert.Empty(ShownAt(timeline, 60));

        // Back to 5 s: the caption that arrives with that frame is added before the query.
        Assert.Equal(["after"], Present(timeline, Seconds(5), "after"));
        Assert.Equal(["after"], ShownAt(timeline, 8));
    }

    [Fact]
    public void SeekBack_CaptionsFromBeforeIt_DoNotShowAgain()
    {
        var timeline = new CaptionTimeline(Display);
        Assert.Equal(["before"], Present(timeline, Seconds(58), "before"));
        Assert.Equal(["before"], ShownAt(timeline, 60));

        Assert.Empty(ShownAt(timeline, 55));
        Assert.Empty(ShownAt(timeline, 58));
        Assert.Empty(ShownAt(timeline, 60));
    }

    [Fact]
    public void Loop_PlayedFrameByFrame_ShowsOnlyThisPassCaptions()
    {
        // A 10 s clip at 30 fps, played twice. Captions arrive at 2 s and 7 s on the first pass,
        // and at 9 s on the second.
        const int clipFrames = 10 * Fps;
        var firstPass = new Dictionary<int, string> { [2 * Fps] = "one", [7 * Fps] = "two" };
        var secondPass = new Dictionary<int, string> { [9 * Fps] = "three" };
        var timeline = new CaptionTimeline(Display);

        for (int i = 0; i < clipFrames; i++)
        {
            var shown = Present(timeline, Frame(i), firstPass.TryGetValue(i, out var t) ? [t] : []);
            string[] expected = i < 2 * Fps ? [] : i < 7 * Fps ? ["one"] : ["two"];
            Assert.True(expected.SequenceEqual(shown), $"first pass, frame {i}: [{string.Join(", ", shown)}]");
        }

        for (int i = 0; i < clipFrames; i++)
        {
            var shown = Present(timeline, Frame(i), secondPass.TryGetValue(i, out var t) ? [t] : []);
            string[] expected = i < 9 * Fps ? [] : ["three"];
            Assert.True(expected.SequenceEqual(shown), $"second pass, frame {i}: [{string.Join(", ", shown)}]");
        }
    }

    [Fact]
    public void NextItem_StartingAtZero_ShowsItsOwnCaptions()
    {
        var timeline = new CaptionTimeline(Display);
        Present(timeline, Seconds(40), "first item");
        Assert.Empty(ShownAt(timeline, 60));

        Assert.Empty(ShownAt(timeline, 0));
        Assert.Equal(["second item"], Present(timeline, Seconds(1), "second item"));
        Assert.Equal(["second item"], ShownAt(timeline, 7));
        Assert.Empty(ShownAt(timeline, 40));
    }

    [Fact]
    public void NextItem_EndingCaption_DoesNotShowInTheNextItem()
    {
        var timeline = new CaptionTimeline(Display);
        Present(timeline, Seconds(29), "end of the first item");
        Assert.Equal(["end of the first item"], ShownAt(timeline, 30));

        for (int i = 0; i <= 30 * Fps; i++)
            Assert.Empty(ShownAt(timeline, Frame(i)));
    }

    // ─── Timelines (ADR-0084) ────────────────────────────────────────

    /// <summary>
    /// The frame callback on a source that names its timelines: add what has arrived, each caption
    /// on its own timeline, then ask what to show at the frame on its timeline.
    /// </summary>
    private static string[] Present(
        CaptionTimeline timeline,
        TimeSpan pts,
        TimelineId frame,
        params (string Text, TimelineId Timeline)[] arrived
    )
    {
        foreach (var (text, on) in arrived)
            timeline.Add(Said(text), pts, on);
        return Texts(timeline.GetActive(pts, frame));
    }

    [Fact]
    public void OnOneTimeline_ACaptionShowsFromItsArrival_ForTheDisplayDuration()
    {
        var timeline = new CaptionTimeline(Display);
        var playing = TimelineId.Next();

        Assert.Equal(["hello"], Present(timeline, Seconds(10), playing, ("hello", playing)));
        Assert.Equal(["hello"], Present(timeline, Seconds(16), playing));
        Assert.Empty(Present(timeline, Seconds(16) + TimeSpan.FromTicks(1), playing));
    }

    [Fact]
    public void ANewTimeline_StartsEmpty_EvenWhenItsTimeMovesForward()
    {
        // A seek forward by less than the display duration: the time alone cannot show it.
        var timeline = new CaptionTimeline(Display);
        var before = TimelineId.Next();
        var after = TimelineId.Next();
        Assert.Equal(["before"], Present(timeline, Seconds(10), before, ("before", before)));

        Assert.Empty(Present(timeline, Seconds(13), after));
        Assert.Equal(["after"], Present(timeline, Seconds(13.5), after, ("after", after)));
    }

    [Fact]
    public void OnOneTimeline_ATimeBelowTheClock_KeepsTheCaptions()
    {
        var timeline = new CaptionTimeline(Display);
        var playing = TimelineId.Next();
        Assert.Equal(["a"], Present(timeline, Seconds(10), playing, ("a", playing)));

        Assert.Equal(["a"], Present(timeline, Seconds(9.98), playing));
        Assert.Equal(["a"], Present(timeline, Seconds(10.02), playing));
    }

    [Fact]
    public void OnOneTimeline_ACaptionArrivingAtATimeBelowTheClock_KeepsTheOthers()
    {
        var timeline = new CaptionTimeline(Display, maxStackedLines: 2);
        var playing = TimelineId.Next();
        Assert.Equal(["a"], Present(timeline, Seconds(10), playing, ("a", playing)));

        Assert.Equal(["a", "b"], Present(timeline, Seconds(9.98), playing, ("b", playing)));
    }

    [Fact]
    public void ACaptionInFlightFromTheTimelineLeft_IsDropped()
    {
        // A seek back from 38 s to 5 s. Captions of the audio before it are still being
        // transcribed: one arrives with the first frame after the seek, before that frame's
        // timeline is given, and one with the frame after, once it has been.
        var timeline = new CaptionTimeline(Display);
        var before = TimelineId.Next();
        var after = TimelineId.Next();
        Assert.Equal(["before"], Present(timeline, Seconds(38), before, ("before", before)));

        Assert.Empty(Present(timeline, Seconds(5), after, ("late", before)));
        Assert.Empty(Present(timeline, Seconds(5) + Frame(1), after, ("later", before)));
        Assert.Equal(["after"], Present(timeline, Seconds(6), after, ("after", after)));
    }

    [Fact]
    public void ACaptionOfATimelineNotYetPresented_IsHeld_AndShowsFromItsFirstFrame()
    {
        // The next item's audio is transcribed before its first frame reaches the screen.
        var timeline = new CaptionTimeline(Display);
        var playing = TimelineId.Next();
        var next = TimelineId.Next();
        Present(timeline, Seconds(39), playing);

        Assert.Empty(Present(timeline, Seconds(40), playing, ("early", next)));
        Assert.Empty(Present(timeline, Seconds(41), playing));

        Assert.Equal(["early"], Present(timeline, Seconds(0.5), next));
        Assert.Equal(["early"], Present(timeline, Seconds(6.5), next));
        Assert.Empty(Present(timeline, Seconds(6.5) + TimeSpan.FromTicks(1), next));
    }

    [Fact]
    public void BeforeTheFirstFrame_ACaptionOfAnotherTimeline_DoesNotShowOnIt()
    {
        // Captions of an item that presented no frame, such as an audio-only file, arrive with
        // the first frame ever presented, which is the next item's.
        var timeline = new CaptionTimeline(Display);
        var audioOnly = TimelineId.Next();
        var playing = TimelineId.Next();

        Assert.Equal(["mine"], Present(timeline, Seconds(0), playing, ("unseen", audioOnly), ("mine", playing)));
        Assert.Equal(["mine"], Present(timeline, Seconds(1), playing));
    }

    [Fact]
    public void HeldCaptions_KeepOnlyTheLatestBatch()
    {
        var timeline = new CaptionTimeline(Display);
        var playing = TimelineId.Next();
        var next = TimelineId.Next();
        Present(timeline, Seconds(39), playing);
        Present(timeline, Seconds(40), playing, ("a", next));
        Present(timeline, Seconds(40.5), playing, ("b", next), ("c", next));

        Assert.Equal(["b", "c"], Present(timeline, Seconds(0), next));
    }

    [Fact]
    public void HeldCaptionsOfOneTimeline_AreNotDroppedForThoseOfAnother()
    {
        // A short item ahead, and the one after it, both transcribed before either presents.
        var timeline = new CaptionTimeline(Display);
        var playing = TimelineId.Next();
        var next = TimelineId.Next();
        var after = TimelineId.Next();
        Present(timeline, Seconds(39), playing);
        Present(timeline, Seconds(40), playing, ("next", next));
        Present(timeline, Seconds(41), playing, ("after", after));

        Assert.Equal(["next"], Present(timeline, Seconds(0), next));
        Assert.Equal(["after"], Present(timeline, Seconds(0), after));
    }

    [Fact]
    public void HeldCaptionsOfTwoTimelines_ArrivingTogether_EachShowWithTheirOwn()
    {
        // Captions of two timelines not presented yet arrive with one frame: one batch, and each
        // caption shows when its own timeline presents.
        var timeline = new CaptionTimeline(Display);
        var playing = TimelineId.Next();
        var next = TimelineId.Next();
        var after = TimelineId.Next();
        Present(timeline, Seconds(39), playing);

        Assert.Empty(Present(timeline, Seconds(40), playing, ("next", next), ("after", after)));

        Assert.Equal(["next"], Present(timeline, Seconds(0), next));
        Assert.Equal(["after"], Present(timeline, Seconds(0), after));
    }

    [Fact]
    public void ATimelineMadeFirst_StillFollowsTheOneOnScreen()
    {
        // An item prepared ahead takes its timeline before the playing item loops. Presentation,
        // not the order the ids were made in, says which is past.
        var timeline = new CaptionTimeline(Display);
        var prepared = TimelineId.Next();
        var firstPass = TimelineId.Next();
        var loop = TimelineId.Next();

        Assert.Equal(["one"], Present(timeline, Seconds(8), firstPass, ("one", firstPass)));
        Assert.Equal(["two"], Present(timeline, Seconds(1), loop, ("two", loop)));
        Assert.Equal(["three"], Present(timeline, Seconds(2), prepared, ("three", prepared), ("late", loop)));
        Assert.Equal(["three"], Present(timeline, Seconds(3), prepared, ("later", firstPass)));
    }

    [Fact]
    public void OnOneTimeline_ACaptionStampedBelowAnother_AgesOutOnItsOwnStamp()
    {
        // The second caption arrives with a frame 20 ms below the first's, on the same timeline.
        var playing = TimelineId.Next();
        var oneLine = new CaptionTimeline(Display);
        var stacked = new CaptionTimeline(Display, maxStackedLines: 2);
        foreach (var timeline in new[] { oneLine, stacked })
        {
            Present(timeline, Seconds(10), playing, ("a", playing));
            Present(timeline, Seconds(9.98), playing, ("b", playing));
        }

        Assert.Equal(["b"], Present(oneLine, Seconds(15.97), playing));
        Assert.Empty(Present(oneLine, Seconds(15.99), playing));
        Assert.Equal(["a", "b"], Present(stacked, Seconds(15.97), playing));
        Assert.Equal(["a"], Present(stacked, Seconds(15.99), playing));
    }

    [Fact]
    public void Loop_PlayedFrameByFrame_DropsTheCaptionsInFlightFromThePassBefore()
    {
        // A 10 s clip at 30 fps, played twice, each pass on its own timeline. Captions arrive at
        // 2 s and 7 s on the first pass. One more for the end of the first pass is still being
        // transcribed at the loop, and arrives half a second into the second pass, which gets
        // its own at 9 s.
        const int clipFrames = 10 * Fps;
        var first = TimelineId.Next();
        var second = TimelineId.Next();
        var firstPass = new Dictionary<int, (string, TimelineId)> { [2 * Fps] = ("one", first), [7 * Fps] = ("two", first) };
        var secondPass = new Dictionary<int, (string, TimelineId)> { [Fps / 2] = ("end of one", first), [9 * Fps] = ("three", second) };
        var timeline = new CaptionTimeline(Display);

        for (int i = 0; i < clipFrames; i++)
        {
            var shown = Present(timeline, Frame(i), first, firstPass.TryGetValue(i, out var c) ? [c] : []);
            string[] expected = i < 2 * Fps ? [] : i < 7 * Fps ? ["one"] : ["two"];
            Assert.True(expected.SequenceEqual(shown), $"first pass, frame {i}: [{string.Join(", ", shown)}]");
        }

        for (int i = 0; i < clipFrames; i++)
        {
            var shown = Present(timeline, Frame(i), second, secondPass.TryGetValue(i, out var c) ? [c] : []);
            string[] expected = i < 9 * Fps ? [] : ["three"];
            Assert.True(expected.SequenceEqual(shown), $"second pass, frame {i}: [{string.Join(", ", shown)}]");
        }
    }

    [Fact]
    public void FramesWithoutATimeline_KeepTheTimeRule_ForCaptionsThatNameOne()
    {
        var timeline = new CaptionTimeline(Display);
        var audio = TimelineId.Next();
        Assert.Equal(["before"], Present(timeline, Seconds(10), TimelineId.None, ("before", audio)));

        // Forward: nothing tells, so the caption stays. Back: a jump.
        Assert.Equal(["before"], Present(timeline, Seconds(13), TimelineId.None));
        Assert.Empty(Present(timeline, Seconds(2), TimelineId.None));
        Assert.Equal(["after"], Present(timeline, Seconds(3), TimelineId.None, ("after", TimelineId.Next())));
    }

    [Fact]
    public void AFrameWithoutATimeline_OnATrackedScreen_KeepsTheTimeRule()
    {
        var timeline = new CaptionTimeline(Display);
        var playing = TimelineId.Next();
        Assert.Equal(["a"], Present(timeline, Seconds(10), playing, ("a", playing)));

        // Forward: nothing tells, so the caption stays. Back: a jump.
        Assert.Equal(["a"], Present(timeline, Seconds(11), TimelineId.None));
        Assert.Empty(Present(timeline, Seconds(5), TimelineId.None));
        Assert.Equal(["b"], Present(timeline, Seconds(5.5), playing, ("b", playing)));
    }

    [Fact]
    public void CaptionsWithoutATimeline_BelongToTheOneOnScreen()
    {
        var timeline = new CaptionTimeline(Display);
        var before = TimelineId.Next();
        var after = TimelineId.Next();
        Assert.Equal(["a"], Present(timeline, Seconds(10), before, ("a", TimelineId.None)));

        Assert.Empty(Present(timeline, Seconds(12), after));
        Assert.Equal(["b"], Present(timeline, Seconds(12.5), after, ("b", TimelineId.None)));
    }

    [Fact]
    public void ACaptionWithoutATimeline_ArrivingWithTheFirstFrameOfANewOne_Shows()
    {
        // Captions from a source that names no timeline, over frames that do.
        var timeline = new CaptionTimeline(Display);
        var before = TimelineId.Next();
        var after = TimelineId.Next();
        Assert.Equal(["a"], Present(timeline, Seconds(10), before, ("a", TimelineId.None)));

        Assert.Equal(["b"], Present(timeline, Seconds(2), after, ("b", TimelineId.None)));
        Assert.Equal(["b"], Present(timeline, Seconds(8), after));
    }

    [Fact]
    public void Reset_DropsTheHeldCaptions_AndStillDropsThoseOfATimelineLeft()
    {
        var timeline = new CaptionTimeline(Display);
        var left = TimelineId.Next();
        var playing = TimelineId.Next();
        var next = TimelineId.Next();
        Present(timeline, Seconds(10), left);
        Present(timeline, Seconds(1), playing, ("held", next));

        timeline.Reset();

        Assert.Empty(Present(timeline, Seconds(2), playing, ("late", left)));
        Assert.Equal(["now"], Present(timeline, Seconds(3), playing, ("now", playing)));
        Assert.Empty(Present(timeline, Seconds(0), next));
    }

    // ─── Reset ───────────────────────────────────────────────────────

    [Fact]
    public void ForwardJump_IsNotVisibleInThePts_SoTheCaptionStaysUntilItAgesOut()
    {
        var timeline = new CaptionTimeline(Display);
        Present(timeline, Seconds(10), "before");

        Assert.Equal(["before"], ShownAt(timeline, 13));
    }

    [Fact]
    public void Reset_AtAForwardJump_DropsTheCaptionOnScreen()
    {
        var timeline = new CaptionTimeline(Display);
        Present(timeline, Seconds(10), "before");

        timeline.Reset();

        Assert.Empty(ShownAt(timeline, 13));
        Assert.Equal(["after"], Present(timeline, Seconds(13.5), "after"));
    }

    [Fact]
    public void Reset_ThenTheSameTime_ShowsOnlyTheCaptionsAddedSince()
    {
        var timeline = new CaptionTimeline(Display);
        Present(timeline, Seconds(30), "before");

        timeline.Reset();

        Assert.Empty(ShownAt(timeline, 30));
        Assert.Equal(["after"], Present(timeline, Seconds(31), "after"));
        Assert.Equal(["after"], ShownAt(timeline, 33));
    }
}

/// <summary>The pure state behind <see cref="CaptionTimeline"/>.</summary>
public sealed class CaptionTimelineStateTests
{
    private static readonly TimeSpan Display = TimeSpan.FromSeconds(6);

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    private static Caption Said(string text) => new(TimeSpan.Zero, TimeSpan.Zero, text);

    private static CaptionTimelineState Empty(int maxStackedLines = 1) =>
        CaptionTimelineState.Empty(Display, maxStackedLines);

    [Fact]
    public void Empty_HasNoCaptionsAndNoTime()
    {
        var state = Empty();

        Assert.Empty(state.Entries);
        Assert.Null(state.Now);
        Assert.Empty(state.OnScreen());
    }

    [Fact]
    public void MoveTo_BelowNow_StartsAnEmptyTimelineThere()
    {
        var state = Empty().Add(Said("a"), Seconds(50)).Add(Said("b"), Seconds(52));

        var moved = state.MoveTo(Seconds(51));

        Assert.Empty(moved.Entries);
        Assert.Equal(Seconds(51), moved.Now);
    }

    [Fact]
    public void MoveTo_Forward_DropsOnlyTheCaptionsThatAgedOut()
    {
        var state = Empty(maxStackedLines: 3)
            .Add(Said("a"), Seconds(3))
            .Add(Said("b"), Seconds(5))
            .Add(Said("c"), Seconds(7));

        // a is 8 s old, b exactly the display duration, c 4 s.
        var moved = state.MoveTo(Seconds(11));

        Assert.Equal(["b", "c"], moved.Entries.Select(e => e.Caption.Text));
        Assert.Equal(Seconds(11), moved.Now);
    }

    [Fact]
    public void Add_BelowNow_StartsANewTimeline_WithTheCaptionOnIt()
    {
        var state = Empty().Add(Said("before"), Seconds(58)).MoveTo(Seconds(60));

        var added = state.Add(Said("after"), Seconds(5));

        Assert.Equal([new TimedCaption(Said("after"), Seconds(5))], added.Entries.ToArray());
        Assert.Equal(Seconds(5), added.Now);
    }

    [Fact]
    public void Operations_LeaveTheEarlierStateAsItWas()
    {
        var state = Empty().Add(Said("a"), Seconds(10));

        _ = state.MoveTo(Seconds(2));
        _ = state.MoveTo(Seconds(20));
        _ = state.Add(Said("b"), Seconds(11));
        _ = state.Reset();

        Assert.Equal([new TimedCaption(Said("a"), Seconds(10))], state.Entries.ToArray());
        Assert.Equal(Seconds(10), state.Now);
    }

    [Fact]
    public void Reset_DropsCaptionsAndTime_KeepingTheSettings()
    {
        var reset = Empty(maxStackedLines: 3).Add(Said("a"), Seconds(10)).Reset();

        Assert.Empty(reset.Entries);
        Assert.Null(reset.Now);
        Assert.Equal(Display, reset.DisplayDuration);
        Assert.Equal(3, reset.MaxStackedLines);
    }

    // ─── Timelines (ADR-0084) ────────────────────────────────────────

    [Fact]
    public void TheFirstTimelinePresented_IsTakenUp_WithoutEmptying()
    {
        var playing = TimelineId.Next();
        var state = Empty().Add(Said("a"), Seconds(10), playing);

        var moved = state.MoveTo(Seconds(10.1), playing);

        Assert.Equal(playing, moved.Presented.Current);
        Assert.Equal(["a"], moved.Entries.Select(e => e.Caption.Text));
    }

    [Fact]
    public void BeforeAFrameNamesATimeline_ACaptionThatNamesOne_WaitsForTheFirstFrameToJudgeIt()
    {
        var other = TimelineId.Next();
        var first = TimelineId.Next();

        var added = Empty().Add(Said("other"), TimeSpan.Zero, other);
        Assert.Empty(added.Entries);
        Assert.Equal([new HeldCaption(Said("other"), other, TimeSpan.Zero)], added.Held.ToArray());

        var moved = added.MoveTo(TimeSpan.FromMilliseconds(10), first);
        Assert.Empty(moved.OnScreen());
        Assert.Equal([new HeldCaption(Said("other"), other, TimeSpan.Zero)], moved.Held.ToArray());
    }

    [Fact]
    public void Add_ShowsDropsOrHolds_ByWhereTheCaptionsTimelineStands()
    {
        var left = TimelineId.Next();
        var playing = TimelineId.Next();
        var next = TimelineId.Next();
        var state = Empty().MoveTo(Seconds(10), left).MoveTo(Seconds(1), playing);

        // Each waits for the frame it arrived with, which matches it.
        var waiting = state.Add(Said("current"), Seconds(2), playing);
        Assert.Empty(waiting.Entries);
        Assert.Equal([new HeldCaption(Said("current"), playing, Seconds(2))], waiting.Held.ToArray());

        var current = waiting.MoveTo(Seconds(2), playing);
        var past = state.Add(Said("past"), Seconds(2), left).MoveTo(Seconds(2), playing);
        var upcoming = state.Add(Said("upcoming"), Seconds(2), next).MoveTo(Seconds(2), playing);
        var none = state.Add(Said("none"), Seconds(2), TimelineId.None).MoveTo(Seconds(2), playing);

        Assert.Equal([new TimedCaption(Said("current"), Seconds(2))], current.Entries.ToArray());
        Assert.Empty(current.Held);
        Assert.Empty(past.Entries);
        Assert.Empty(past.Held);
        Assert.Empty(upcoming.Entries);
        Assert.Equal([new HeldCaption(Said("upcoming"), next, Seconds(2))], upcoming.Held.ToArray());
        Assert.Equal([new TimedCaption(Said("none"), Seconds(2))], none.Entries.ToArray());
        Assert.Empty(none.Held);
    }

    [Fact]
    public void HeldCaptions_OfAtMostTheHistorysCountOfTimelines_TheLeastRecentlyHeardFromGoesFirst()
    {
        var playing = TimelineId.Next();
        var state = Empty(maxStackedLines: 2).MoveTo(Seconds(1), playing);
        var upcoming = Enumerable.Range(0, state.Presented.HistoryCapacity + 1).Select(_ => TimelineId.Next()).ToArray();
        foreach (var (timeline, i) in upcoming.Select((t, i) => (t, i)))
            state = state.Add(Said($"{i}"), Seconds(1), timeline).MoveTo(Seconds(1), playing);

        Assert.Equal(upcoming.Skip(1), state.Held.Select(h => h.Timeline));

        // Heard from again, the oldest kept is now the newest, and the next oldest goes.
        state = state.Add(Said("again"), Seconds(1), upcoming[1]).Add(Said("new"), Seconds(1), TimelineId.Next());

        Assert.DoesNotContain(state.Held, h => h.Timeline == upcoming[2]);
        Assert.Equal(["1", "again"], state.Held.Where(h => h.Timeline == upcoming[1]).Select(h => h.Caption.Text));
    }

    [Fact]
    public void MoveTo_AnotherTimeline_StartsWithTheCaptionsHeldForIt_AndKeepsTheRestHeld()
    {
        var playing = TimelineId.Next();
        var next = TimelineId.Next();
        var later = TimelineId.Next();
        var state = Empty(maxStackedLines: 3)
            .MoveTo(Seconds(40), playing)
            .Add(Said("on screen"), Seconds(40), playing)
            .Add(Said("next"), Seconds(40), next)
            .Add(Said("later"), Seconds(41), later);

        var moved = state.MoveTo(Seconds(2), next);

        Assert.Equal(next, moved.Presented.Current);
        Assert.Equal(TimelineRelation.Past, moved.Presented.Classify(playing));
        Assert.Equal([new TimedCaption(Said("next"), Seconds(2))], moved.Entries.ToArray());
        Assert.Equal([new HeldCaption(Said("later"), later, Seconds(41))], moved.Held.ToArray());
        Assert.Equal(Seconds(2), moved.Now);
    }

    [Fact]
    public void ATimelineThatIsNeverPresented_HoldsNoMoreThanCouldShow()
    {
        // Ten minutes at 30 fps with captions of a timeline that never reaches the screen every
        // half second: a batch of two without stacking, one at a time with three stacked lines.
        var playing = TimelineId.Next();
        var never = TimelineId.Next();
        var oneLine = Empty().MoveTo(TimeSpan.Zero, playing);
        var stacked = Empty(maxStackedLines: 3).MoveTo(TimeSpan.Zero, playing);
        for (int frame = 0; frame < 10 * 60 * 30; frame++)
        {
            var pts = TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / 30);
            if (frame % 15 == 0)
            {
                oneLine = oneLine.Add(Said($"{frame}a"), pts, never).Add(Said($"{frame}b"), pts, never);
                stacked = stacked.Add(Said($"{frame}"), pts, never);
            }
            oneLine = oneLine.MoveTo(pts, playing);
            stacked = stacked.MoveTo(pts, playing);
            Assert.InRange(oneLine.Held.Length, 0, 2);
            Assert.InRange(stacked.Held.Length, 0, 3);
        }

        Assert.Equal(["17985a", "17985b"], oneLine.Held.Select(h => h.Caption.Text));
        Assert.Equal(["17955", "17970", "17985"], stacked.Held.Select(h => h.Caption.Text));
    }

    [Fact]
    public void Reset_DropsTheHeldCaptions_AndKeepsTheTimelinesPresented()
    {
        var left = TimelineId.Next();
        var playing = TimelineId.Next();
        var next = TimelineId.Next();
        var state = Empty()
            .MoveTo(Seconds(10), left)
            .MoveTo(Seconds(1), playing)
            .Add(Said("held"), Seconds(1), next);

        var reset = state.Reset();

        Assert.Empty(reset.Entries);
        Assert.Empty(reset.Held);
        Assert.Null(reset.Now);
        Assert.Same(state.Presented, reset.Presented);
    }

    [Fact]
    public void LongRun_HoldsOnlyTheCaptionsThatCanStillShow()
    {
        // Ten minutes at 30 fps with a caption every 2.5 s: at most three are within 6 s.
        var state = Empty(maxStackedLines: 5);
        for (int frame = 0; frame < 10 * 60 * 30; frame++)
        {
            var pts = TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / 30);
            state = frame % 75 == 0 ? state.Add(Said($"{frame}"), pts) : state.MoveTo(pts);
            Assert.InRange(state.Entries.Length, 0, 3);
        }
    }
}
