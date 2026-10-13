// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using FrameFlow.Graph;
using FrameFlow.Graph.Core;

namespace FrameFlow.Whisper.Core;

/// <summary>A caption and the presentation time of the frame on screen when it arrived.</summary>
/// <param name="Caption">The caption.</param>
/// <param name="ArrivedAt">Presentation time of the frame it arrived with.</param>
internal readonly record struct TimedCaption(Caption Caption, TimeSpan ArrivedAt);

/// <summary>
/// A caption waiting for a frame: one added since the last frame while a timeline is on screen, or
/// one of a timeline not presented yet.
/// </summary>
/// <param name="Caption">The caption.</param>
/// <param name="Timeline">
/// The timeline it was transcribed on, or <see cref="TimelineId.None"/> when it names none.
/// </param>
/// <param name="ArrivedAt">
/// Presentation time given with it, on the timeline on screen then. It only groups the captions
/// that arrived together.
/// </param>
internal readonly record struct HeldCaption(Caption Caption, TimelineId Timeline, TimeSpan ArrivedAt);

/// <summary>
/// The captions a <see cref="CaptionTimeline"/> holds, the timeline on screen and the
/// presentation time it has reached. Pure: each operation returns a new state and leaves this one
/// as it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tracked timelines (ADR-0084).</b> Captions are derived items matched against what is shown,
/// so the order comes from presentation, through a <see cref="PresentedTimeline"/>. A frame on a
/// timeline other than the one on screen starts a new timeline with nothing from the one it left,
/// whichever way its time moved. A caption that names a timeline, and once a timeline is on screen
/// any caption, waits for the next frame and is matched against that frame's timeline, so a
/// caption that arrives with the first frame of a new timeline is judged on it: shown if it is of
/// that timeline or names none, or while no frame has named a timeline; dropped if the screen has
/// left its timeline; and held otherwise. A held caption joins the screen with the first frame of
/// its timeline, as if it arrived with that frame. On one tracked timeline the time is continuous,
/// so a time below <see cref="Now"/> is not a jump.
/// </para>
/// <para>
/// <b>Untracked.</b> Until a frame names a timeline, and for a frame that names none, frames
/// present in rising PTS order until a seek, a loop or the next item. A time below
/// <see cref="Now"/> can only be one of those, so it starts a new timeline with nothing on it. A
/// caption from before the jump is not kept to show again when playback reaches its stamp, and the
/// time before the jump no longer sets the cutoff that evicts the captions after it. A caption on
/// <see cref="TimelineId.None"/> belongs to the timeline on screen. While no frame has named a
/// timeline, a caption that names one waits for the next frame, and joins it if that frame names
/// none too.
/// </para>
/// <para>
/// <b>Bounds.</b> A caption on screen is dropped once it is more than
/// <see cref="DisplayDuration"/> behind <see cref="Now"/>, and once it can no longer show: past
/// the latest batch, or with <see cref="MaxStackedLines"/> above one, past that many of the latest
/// captions. A held caption has no clock to age on, since its timeline has not been presented, so
/// it is bounded by count alone. Each timeline keeps what could show once it presents, and the
/// captions of at most <see cref="PresentedTimeline.HistoryCapacity"/> timelines wait at once; past
/// that, the timeline heard from least recently is dropped.
/// </para>
/// </remarks>
internal sealed record CaptionTimelineState
{
    private CaptionTimelineState(TimeSpan displayDuration, int maxStackedLines)
    {
        DisplayDuration = displayDuration;
        MaxStackedLines = maxStackedLines;
    }

    /// <summary>A timeline with no captions that has not been given a time or a timeline.</summary>
    public static CaptionTimelineState Empty(TimeSpan displayDuration, int maxStackedLines) =>
        new(displayDuration, maxStackedLines);

    /// <summary>How long a caption shows after the frame it arrived with.</summary>
    public TimeSpan DisplayDuration { get; }

    /// <summary>
    /// Above one, the most captions shown at once. One or less shows the latest batch: every
    /// caption that arrived with the same frame as the last.
    /// </summary>
    public int MaxStackedLines { get; }

    /// <summary>
    /// The captions of the timeline on screen that can still show at <see cref="Now"/>, in the
    /// order they arrived.
    /// </summary>
    public ImmutableArray<TimedCaption> Entries { get; private init; } = [];

    /// <summary>The captions waiting for a frame, in the order they arrived.</summary>
    public ImmutableArray<HeldCaption> Held { get; private init; } = [];

    /// <summary>The timeline on screen, and the ones it has left.</summary>
    public PresentedTimeline Presented { get; private init; } = new();

    /// <summary>
    /// The latest presentation time given, or <see langword="null"/> when none has been since
    /// the timeline was created or reset.
    /// </summary>
    public TimeSpan? Now { get; private init; }

    /// <summary>Moves to <paramref name="pts"/> on a frame that names no timeline.</summary>
    public CaptionTimelineState MoveTo(TimeSpan pts) => MoveTo(pts, TimelineId.None);

    /// <summary>
    /// Moves to the frame at <paramref name="pts"/> on <paramref name="timeline"/>, then matches
    /// the held captions against it. A timeline other than the one on screen starts a new timeline
    /// there. On the timeline on screen the captions that have aged out are dropped. Where either
    /// side names no timeline, a time below <see cref="Now"/> starts a new, empty timeline there.
    /// </summary>
    public CaptionTimelineState MoveTo(TimeSpan pts, TimelineId timeline)
    {
        var screen = Presented.Present(timeline);
        var moved =
            Presented.Current != TimelineId.None && screen.Current != Presented.Current
                ? this with { Presented = screen, Entries = [], Now = pts }
                : Advance(screen, pts, known: timeline != TimelineId.None && timeline == Presented.Current);
        return moved.Match(pts);
    }

    /// <summary>Adds <paramref name="caption"/>, on no known timeline, as arriving at <paramref name="pts"/>.</summary>
    public CaptionTimelineState Add(Caption caption, TimeSpan pts) => Add(caption, pts, TimelineId.None);

    /// <summary>
    /// Takes <paramref name="caption"/>, transcribed on <paramref name="timeline"/>, as arriving
    /// with the frame at <paramref name="pts"/>. A caption that names a timeline, and any caption
    /// once a frame has named one, waits for the next frame, which matches it. Otherwise it moves
    /// to <paramref name="pts"/> and shows the caption.
    /// </summary>
    public CaptionTimelineState Add(Caption caption, TimeSpan pts, TimelineId timeline)
    {
        // Before the first frame that names a timeline, a caption that names one is not known to
        // be current: it can be of an item that presented nothing, such as an audio-only file.
        // The first frame judges it, as it judges one added once a timeline is on screen (#687).
        if (Presented.Current != TimelineId.None || timeline != TimelineId.None)
            return this with { Held = BoundHeld(Held.Add(new HeldCaption(caption, timeline, pts))) };

        var moved = Advance(Presented, pts, known: false);
        return moved with { Entries = BoundEntries(moved.Entries.Add(new TimedCaption(caption, pts))) };
    }

    /// <summary>
    /// A timeline with no captions and no time, as if newly created, except that it still knows
    /// which timelines have been presented, so a caption from one already left is still dropped.
    /// </summary>
    public CaptionTimelineState Reset() => this with { Entries = [], Held = [], Now = null };

    /// <summary>
    /// The captions to show at <see cref="Now"/>, oldest first: the latest batch, or with
    /// <see cref="MaxStackedLines"/> above one, up to that many of the latest captions.
    /// </summary>
    public IReadOnlyList<Caption> OnScreen()
    {
        if (Entries.IsEmpty)
            return [];

        int last = Entries.Length - 1;
        int first;
        if (MaxStackedLines <= 1)
        {
            // One Whisper window's segments arrive in one burst and share a frame, so they show
            // together, each on its own line.
            first = last;
            while (first > 0 && Entries[first - 1].ArrivedAt == Entries[last].ArrivedAt)
                first--;
        }
        else
        {
            first = Math.Max(0, Entries.Length - MaxStackedLines);
        }

        var shown = new Caption[last - first + 1];
        for (int i = 0; i < shown.Length; i++)
            shown[i] = Entries[first + i].Caption;
        return shown;
    }

    // On the timeline on screen: a time below Now is a jump only where a side names no timeline;
    // otherwise the captions that aged out are dropped.
    private CaptionTimelineState Advance(PresentedTimeline screen, TimeSpan pts, bool known)
    {
        if (!known && Now is { } now && pts < now)
            return this with { Presented = screen, Entries = [], Now = pts };

        var kept = Expire(pts);
        if (kept.Length == Entries.Length && Now == pts && ReferenceEquals(screen, Presented))
            return this;
        return this with { Presented = screen, Entries = kept, Now = pts };
    }

    // Every caption more than the display duration behind pts. Arrival order is not stamp order:
    // on one tracked timeline a frame can step back, and a caption arrive below an earlier one.
    private ImmutableArray<TimedCaption> Expire(TimeSpan pts)
    {
        int expired = 0;
        foreach (var entry in Entries)
        {
            if (pts - entry.ArrivedAt > DisplayDuration)
                expired++;
        }

        if (expired == 0)
            return Entries;

        var kept = ImmutableArray.CreateBuilder<TimedCaption>(Entries.Length - expired);
        foreach (var entry in Entries)
        {
            if (pts - entry.ArrivedAt <= DisplayDuration)
                kept.Add(entry);
        }

        return kept.MoveToImmutable();
    }

    // The held captions against the frame at pts, now on screen: of its timeline or of none, they
    // join it as if they arrived with it; of a timeline it has left, they are dropped; of one not
    // presented yet, they keep waiting.
    private CaptionTimelineState Match(TimeSpan pts)
    {
        if (Held.IsEmpty)
            return this;

        var joined = ImmutableArray.CreateBuilder<TimedCaption>();
        var waiting = ImmutableArray.CreateBuilder<HeldCaption>();
        foreach (var held in Held)
        {
            switch (Presented.Classify(held.Timeline))
            {
                case TimelineRelation.Current:
                    joined.Add(new TimedCaption(held.Caption, pts));
                    break;
                case TimelineRelation.Upcoming:
                    waiting.Add(held);
                    break;
            }
        }

        if (waiting.Count == Held.Length)
            return this;

        return this with
        {
            Entries = joined.Count == 0 ? Entries : BoundEntries(Entries.AddRange(joined.ToImmutable())),
            Held = waiting.ToImmutable(),
        };
    }

    private ImmutableArray<TimedCaption> BoundEntries(ImmutableArray<TimedCaption> entries) =>
        Bound(entries, groups: 1, static _ => TimelineId.None, static e => e.ArrivedAt);

    private ImmutableArray<HeldCaption> BoundHeld(ImmutableArray<HeldCaption> held) =>
        Bound(held, Presented.HistoryCapacity, static h => h.Timeline, static h => h.ArrivedAt);

    // What could still show, for each of the `groups` timelines heard from most recently: with
    // stacking, its latest MaxStackedLines captions; without it, its latest batch, the captions
    // that arrived one after another with the same frame as its newest, as OnScreen groups them.
    private ImmutableArray<T> Bound<T>(
        ImmutableArray<T> items,
        int groups,
        Func<T, TimelineId> timelineOf,
        Func<T, TimeSpan> arrivedAt
    )
    {
        if (items.Length <= 1)
            return items;

        var keep = new bool[items.Length];
        var seen = new List<Group>(Math.Min(groups, items.Length));
        int kept = 0;
        for (int i = items.Length - 1; i >= 0; i--)
        {
            var timeline = timelineOf(items[i]);
            var at = arrivedAt(items[i]);
            int g = 0;
            while (g < seen.Count && seen[g].Timeline != timeline)
                g++;

            if (g == seen.Count)
            {
                if (seen.Count == groups)
                    continue;
                seen.Add(new Group(timeline, Count: 1, Batch: at, Closed: false));
                keep[i] = true;
                kept++;
                continue;
            }

            var group = seen[g];
            if (group.Closed)
                continue;

            bool takes = MaxStackedLines > 1 ? group.Count < MaxStackedLines : at == group.Batch;
            seen[g] = takes ? group with { Count = group.Count + 1 } : group with { Closed = true };
            if (takes)
            {
                keep[i] = true;
                kept++;
            }
        }

        if (kept == items.Length)
            return items;

        var bounded = ImmutableArray.CreateBuilder<T>(kept);
        for (int i = 0; i < items.Length; i++)
        {
            if (keep[i])
                bounded.Add(items[i]);
        }

        return bounded.MoveToImmutable();
    }

    private readonly record struct Group(TimelineId Timeline, int Count, TimeSpan Batch, bool Closed);
}
