using FrameFlow.Whisper.Core;

namespace FrameFlow.Whisper.Tests;

/// <summary>
/// Which captions show at a presented frame: each from the frame it arrived with for the display
/// duration, and none from before a seek back, a loop or the next item.
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
        var state = Empty().Add(Said("a"), Seconds(3)).Add(Said("b"), Seconds(5)).Add(Said("c"), Seconds(7));

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
