// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using FrameFlow.Graph;
using FrameFlow.Graph.Core;

namespace FrameFlow.Inference.Core;

/// <summary>What a presented frame changed in a <see cref="PresentedResultsState{TResult}"/>.</summary>
internal enum PresentedChange
{
    /// <summary>The result on screen is the one it was.</summary>
    None,

    /// <summary>A different result is on screen: <see cref="PresentedResultsState{TResult}.Current"/>.</summary>
    Presented,

    /// <summary>The result that was on screen no longer applies, and the frame has none of its own.</summary>
    Cleared,
}

/// <summary>A result waiting for its frame.</summary>
/// <param name="Result">The result.</param>
/// <param name="Restart">
/// How many times a result earlier than the last of its timeline had started that timeline over
/// when this one was posted. The presentation-time rules use it.
/// </param>
internal readonly record struct PendingResult<TResult>(InferenceResult<TResult> Result, int Restart);

/// <summary>
/// The results a <see cref="PresentedResults{TResult}"/> holds, the timeline on screen, and the
/// result for the frame on screen. Pure: each operation returns a new state and leaves this one as
/// it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>Timelines.</b> A result carries the timeline of its frame and a presented frame names the
/// timeline on screen (ADR-0084). Results can arrive before their frame is shown, so order comes
/// from presentation, through a <see cref="PresentedTimeline"/>. A result of a timeline the screen
/// has left is dropped when it is posted. When a new timeline is presented, the results of the one
/// it left are dropped, including the one on screen. A result of a timeline not yet presented waits,
/// and matches no frame until its timeline is on screen.
/// </para>
/// <para>
/// <b>Time.</b> Among the results of the timeline on screen, a frame shows the latest at or before
/// its timestamp. A result on <see cref="TimelineId.None"/>, and every result while no frame has
/// named a timeline, belongs to the timeline on screen. Those keep the rules that came before
/// timelines: a result earlier than the last of its timeline means the timeline went back, and the
/// results waiting from before are dropped; a frame earlier than the result showing, with no result
/// of its own, means the picture went back, and the result is cleared. Until a frame names a
/// timeline, every result counts as one timeline for these rules, whatever it carries, because the
/// screen cannot tell them apart. A timeline that names itself does not go back in time, so once the
/// screen follows timelines these rules do not arise for it.
/// </para>
/// </remarks>
internal sealed record PresentedResultsState<TResult>
{
    private PresentedResultsState(int capacity, PresentedTimeline screen)
    {
        Capacity = capacity;
        Screen = screen;
    }

    /// <summary>Nothing posted and nothing presented.</summary>
    /// <param name="capacity">The most results held while waiting; the oldest go first.</param>
    /// <param name="history">How many left timelines the screen remembers.</param>
    public static PresentedResultsState<TResult> Empty(
        int capacity,
        int history = PresentedTimeline.DefaultHistory
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        return new(capacity, new PresentedTimeline(history));
    }

    /// <summary>The most results held while waiting.</summary>
    public int Capacity { get; }

    /// <summary>The timeline on screen and the ones it has left.</summary>
    public PresentedTimeline Screen { get; private init; }

    /// <summary>The results waiting, in the order they were posted.</summary>
    public ImmutableArray<PendingResult<TResult>> Pending { get; private init; } = [];

    /// <summary>The result for the frame on screen, or <see langword="null"/>.</summary>
    public InferenceResult<TResult>? Current { get; private init; }

    // The Restart of the result on screen, and the restarts so far.
    private int CurrentRestart { get; init; }

    private int Restarts { get; init; }

    /// <summary>Holds <paramref name="result"/> until its frame is presented.</summary>
    public PresentedResultsState<TResult> Post(InferenceResult<TResult> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Its frames have been left and will not be shown again.
        if (Screen.Classify(result.Timeline) == TimelineRelation.Past)
            return this;

        var pending = Pending;
        int restarts = Restarts;
        var screen = Screen;

        // Within one timeline results come in frame order, so one earlier than the last of its
        // timeline means that timeline went back: a seek or a loop that the results do not name.
        int last = LastTogetherWith(pending, screen, result.Timeline);
        if (last >= 0 && result.Timestamp < pending[last].Result.Timestamp)
        {
            pending = pending.RemoveAll(p => Together(screen, p.Result.Timeline, result.Timeline));
            restarts++;
        }

        pending = pending.Add(new PendingResult<TResult>(result, restarts));
        if (pending.Length > Capacity)
            pending = pending.RemoveAt(0);

        return this with { Pending = pending, Restarts = restarts };
    }

    /// <summary>The state once a frame at <paramref name="pts"/> on <paramref name="timeline"/> is shown.</summary>
    public (PresentedResultsState<TResult> State, PresentedChange Change) Present(TimeSpan pts, TimelineId timeline)
    {
        var screen = Screen.Present(timeline);
        var pending = Pending;
        var current = Current;
        bool cleared = false;

        if (screen.Current != Screen.Current)
        {
            // A new timeline is on screen. Nothing of the one it left will be shown again.
            pending = pending.RemoveAll(p => screen.Classify(p.Result.Timeline) == TimelineRelation.Past);
            if (current is not null && screen.Classify(current.Timeline) == TimelineRelation.Past)
            {
                current = null;
                cleared = true;
            }
        }

        int match = LatestAtOrBefore(pending, screen, pts);
        if (match < 0)
        {
            // Nothing at or before this frame. If what is showing is from later in its timeline,
            // the picture went back past it.
            if (current is not null && pts < current.Timestamp)
            {
                // What waits from the cleared result's pass would show it again when the picture
                // gets back there; what was posted since a restart is for the new position.
                var shown = current;
                int restart = CurrentRestart;
                pending = pending.RemoveAll(p =>
                    p.Restart == restart && Together(screen, p.Result.Timeline, shown.Timeline)
                );
                current = null;
                cleared = true;
            }

            if (!cleared && ReferenceEquals(screen, Screen))
                return (this, PresentedChange.None);

            return (
                this with { Screen = screen, Pending = pending, Current = current },
                cleared ? PresentedChange.Cleared : PresentedChange.None
            );
        }

        var matched = pending[match];

        // Keep the matched result. What came before it on the timeline on screen has been shown
        // or passed over; a result still to come stays.
        pending = WithoutEarlier(pending, screen, match);

        if (ReferenceEquals(matched.Result, current))
        {
            if (ReferenceEquals(screen, Screen) && pending == Pending)
                return (this, PresentedChange.None);
            return (this with { Screen = screen, Pending = pending }, PresentedChange.None);
        }

        return (
            this with
            {
                Screen = screen,
                Pending = pending,
                Current = matched.Result,
                CurrentRestart = matched.Restart,
            },
            PresentedChange.Presented
        );
    }

    // Whether the time rules treat results on `a` and on `b` as one timeline. A screen that has
    // named no timeline cannot tell any two apart, so for it they are all one, as before timelines.
    private static bool Together(PresentedTimeline screen, TimelineId a, TimelineId b) =>
        screen.Current == TimelineId.None || a == b;

    // The index of the last result the time rules treat as on `timeline`, or -1.
    private static int LastTogetherWith(
        ImmutableArray<PendingResult<TResult>> pending,
        PresentedTimeline screen,
        TimelineId timeline
    )
    {
        for (int i = pending.Length - 1; i >= 0; i--)
        {
            if (Together(screen, pending[i].Result.Timeline, timeline))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The index of the latest result at or before <paramref name="pts"/> among those
    /// <paramref name="screen"/> calls current, or -1. Of two at the same time, the later posted.
    /// A branch drops frames while a run is in progress, so most frames have no result of their
    /// own and show the one before.
    /// </summary>
    internal static int LatestAtOrBefore(
        ImmutableArray<PendingResult<TResult>> pending,
        PresentedTimeline screen,
        TimeSpan pts
    )
    {
        int found = -1;
        for (int i = 0; i < pending.Length; i++)
        {
            var result = pending[i].Result;
            if (result.Timestamp > pts || screen.Classify(result.Timeline) != TimelineRelation.Current)
                continue;
            if (found < 0 || result.Timestamp >= pending[found].Result.Timestamp)
                found = i;
        }

        return found;
    }

    // `pending` without the other results on the timeline on screen at or before `match`'s time.
    // Judged by time, not by posting order: on a screen that follows timelines, a result on None
    // and one on the timeline on screen are both current but posted in no common order, so one
    // posted earlier can be for a later frame. Where every current result is in one group, which
    // the time rules keep in time order, the two are the same.
    private static ImmutableArray<PendingResult<TResult>> WithoutEarlier(
        ImmutableArray<PendingResult<TResult>> pending,
        PresentedTimeline screen,
        int match
    )
    {
        var at = pending[match].Result.Timestamp;
        int earlier = 0;
        for (int i = 0; i < pending.Length; i++)
        {
            if (IsEarlier(pending, screen, match, at, i))
                earlier++;
        }

        if (earlier == 0)
            return pending;

        var kept = ImmutableArray.CreateBuilder<PendingResult<TResult>>(pending.Length - earlier);
        for (int i = 0; i < pending.Length; i++)
        {
            if (!IsEarlier(pending, screen, match, at, i))
                kept.Add(pending[i]);
        }

        return kept.MoveToImmutable();
    }

    // Whether the result at `i` has been shown or passed over once the result at `match`, at
    // `at`, is on screen.
    private static bool IsEarlier(
        ImmutableArray<PendingResult<TResult>> pending,
        PresentedTimeline screen,
        int match,
        TimeSpan at,
        int i
    ) =>
        i != match
        && pending[i].Result.Timestamp <= at
        && screen.Classify(pending[i].Result.Timeline) == TimelineRelation.Current;
}
