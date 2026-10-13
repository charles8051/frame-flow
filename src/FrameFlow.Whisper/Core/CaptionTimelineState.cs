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

/// <summary>A caption of a timeline that has not been presented yet, waiting for it.</summary>
/// <param name="Caption">The caption.</param>
/// <param name="Timeline">The timeline it was transcribed on.</param>
/// <param name="ArrivedAt">
/// Presentation time of the frame on screen when it arrived, on the timeline on screen then. It
/// only groups the captions that arrived together.
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
/// whichever way its time moved. A caption of a timeline the screen has left is dropped. A caption
/// of a timeline not yet presented is held, and joins the screen with the first frame of its
/// timeline, as if it arrived with that frame. Held captions are bounded by count, not by age:
/// only what could show then is kept, the latest batch, or with <see cref="MaxStackedLines"/>
/// above one, that many of the latest captions. On one tracked timeline the time is continuous,
/// so a time below <see cref="Now"/> is not a jump.
/// </para>
/// <para>
/// <b>Untracked.</b> Where the frame or the screen names no timeline, frames present in rising
/// PTS order until a seek, a loop or the next item. A time below <see cref="Now"/> can only be one
/// of those, so it starts a new timeline with nothing on it. A caption from before the jump is not
/// kept to show again when playback reaches its stamp, and the time before the jump no longer sets
/// the cutoff that evicts the captions after it. A caption on <see cref="TimelineId.None"/> belongs
/// to the timeline on screen.
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
    /// The captions of the timeline on screen within <see cref="DisplayDuration"/> of
    /// <see cref="Now"/>, in the order they arrived.
    /// </summary>
    public ImmutableArray<TimedCaption> Entries { get; private init; } = [];

    /// <summary>The captions of timelines not presented yet, in the order they arrived.</summary>
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
    /// Moves to the frame at <paramref name="pts"/> on <paramref name="timeline"/>. A timeline
    /// other than the one on screen starts a new timeline there, with the captions held for it. On
    /// the timeline on screen the captions that have aged out are dropped. Where either side names
    /// no timeline, a time below <see cref="Now"/> starts a new, empty timeline there.
    /// </summary>
    public CaptionTimelineState MoveTo(TimeSpan pts, TimelineId timeline)
    {
        var screen = Presented.Present(timeline);
        if (Presented.Current != TimelineId.None && screen.Current != Presented.Current)
            return Start(screen, pts);

        var known = timeline != TimelineId.None && timeline == Presented.Current;
        if (!known && Now is { } now && pts < now)
            return this with { Presented = screen, Entries = [], Now = pts };

        int expired = 0;
        while (expired < Entries.Length && pts - Entries[expired].ArrivedAt > DisplayDuration)
            expired++;

        if (expired == 0 && Now == pts && ReferenceEquals(screen, Presented))
            return this;
        return this with { Presented = screen, Entries = Entries.RemoveRange(0, expired), Now = pts };
    }

    /// <summary>Adds <paramref name="caption"/>, on no known timeline, as arriving at <paramref name="pts"/>.</summary>
    public CaptionTimelineState Add(Caption caption, TimeSpan pts) => Add(caption, pts, TimelineId.None);

    /// <summary>
    /// Moves to <paramref name="pts"/> on the timeline on screen, then takes
    /// <paramref name="caption"/>, transcribed on <paramref name="timeline"/>, as arriving there:
    /// shown if its timeline is on screen, dropped if the screen has left it, and held otherwise.
    /// </summary>
    public CaptionTimelineState Add(Caption caption, TimeSpan pts, TimelineId timeline)
    {
        var moved = MoveTo(pts, Presented.Current);
        return moved.Presented.Classify(timeline) switch
        {
            TimelineRelation.Past => moved,
            TimelineRelation.Upcoming => moved with { Held = moved.Hold(new HeldCaption(caption, timeline, pts)) },
            _ => moved with { Entries = moved.Entries.Add(new TimedCaption(caption, pts)) },
        };
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

    // A new timeline on screen at pts: nothing from the one it replaces, and the captions held
    // for it as if they arrived with this frame. The rest stay held while still upcoming.
    private CaptionTimelineState Start(PresentedTimeline screen, TimeSpan pts)
    {
        var entries = ImmutableArray.CreateBuilder<TimedCaption>();
        var held = ImmutableArray.CreateBuilder<HeldCaption>();
        foreach (var waiting in Held)
        {
            if (waiting.Timeline == screen.Current)
                entries.Add(new TimedCaption(waiting.Caption, pts));
            // Defensive: no held caption is of a timeline left, since presenting a timeline
            // takes every caption held for it and Add never holds a past one.
            else if (screen.Classify(waiting.Timeline) == TimelineRelation.Upcoming)
                held.Add(waiting);
        }

        return this with
        {
            Presented = screen,
            Entries = entries.ToImmutable(),
            Held = held.ToImmutable(),
            Now = pts,
        };
    }

    // Held captions are kept only while they could still show once their timeline presents, as
    // if they all arrived with its first frame: with stacking, the latest MaxStackedLines; without
    // it, the latest batch, every caption that arrived with the same frame as the newest, as
    // OnScreen groups them. A batch can hold captions of two timelines, and each shows when its
    // own timeline presents.
    private ImmutableArray<HeldCaption> Hold(HeldCaption caption)
    {
        var held = Held.Add(caption);
        int first;
        if (MaxStackedLines > 1)
        {
            first = Math.Max(0, held.Length - MaxStackedLines);
        }
        else
        {
            first = held.Length - 1;
            while (first > 0 && held[first - 1].ArrivedAt == caption.ArrivedAt)
                first--;
        }

        return first == 0 ? held : held.RemoveRange(0, first);
    }
}
