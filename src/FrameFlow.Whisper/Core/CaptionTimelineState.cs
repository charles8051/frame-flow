// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace FrameFlow.Whisper.Core;

/// <summary>A caption and the presentation time of the frame on screen when it arrived.</summary>
/// <param name="Caption">The caption.</param>
/// <param name="ArrivedAt">Presentation time of the frame it arrived with.</param>
internal readonly record struct TimedCaption(Caption Caption, TimeSpan ArrivedAt);

/// <summary>
/// The captions a <see cref="CaptionTimeline"/> holds and the presentation time it has reached.
/// Pure: each operation returns a new state and leaves this one as it was.
/// </summary>
/// <remarks>
/// <para>
/// Frames present in rising PTS order until a seek, a loop or the next item. A time below
/// <see cref="Now"/> can only be one of those, so it starts a new timeline with nothing on it.
/// A caption from before the jump is not kept to show again when playback reaches its stamp,
/// and the time before the jump no longer sets the cutoff that evicts the captions after it.
/// </para>
/// <para>
/// This agrees with <see cref="WhisperOperators.TranscribeWithWhisper"/>, which starts a new
/// window when its audio jumps: every caption it emits once it has taken in audio from after a
/// jump is for that audio, and arrives with a frame after the jump, on the new timeline. A
/// caption for audio before the jump that is still being transcribed when the video jumps also
/// arrives after it; nothing here tells the two apart, so the caller drops that one.
/// </para>
/// </remarks>
internal sealed record CaptionTimelineState
{
    private CaptionTimelineState(TimeSpan displayDuration, int maxStackedLines)
    {
        DisplayDuration = displayDuration;
        MaxStackedLines = maxStackedLines;
    }

    /// <summary>A timeline with no captions that has not been given a time.</summary>
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
    /// The captions within <see cref="DisplayDuration"/> of <see cref="Now"/>, in the order they
    /// arrived, which is also the order of their times.
    /// </summary>
    public ImmutableArray<TimedCaption> Entries { get; private init; } = [];

    /// <summary>
    /// The latest presentation time given, or <see langword="null"/> when none has been since
    /// the timeline was created or reset.
    /// </summary>
    public TimeSpan? Now { get; private init; }

    /// <summary>
    /// Moves the timeline to <paramref name="pts"/>. A time below <see cref="Now"/> starts a new,
    /// empty timeline there; otherwise the captions that have aged out are dropped.
    /// </summary>
    public CaptionTimelineState MoveTo(TimeSpan pts)
    {
        if (Now is { } now && pts < now)
            return this with { Entries = [], Now = pts };

        int expired = 0;
        while (expired < Entries.Length && pts - Entries[expired].ArrivedAt > DisplayDuration)
            expired++;

        if (expired == 0 && Now == pts)
            return this;
        return this with { Entries = Entries.RemoveRange(0, expired), Now = pts };
    }

    /// <summary>
    /// Moves the timeline to <paramref name="pts"/>, then adds <paramref name="caption"/> as
    /// arriving there.
    /// </summary>
    public CaptionTimelineState Add(Caption caption, TimeSpan pts)
    {
        var moved = MoveTo(pts);
        return moved with { Entries = moved.Entries.Add(new TimedCaption(caption, pts)) };
    }

    /// <summary>A timeline with no captions and no time, as if newly created.</summary>
    public CaptionTimelineState Reset() => this with { Entries = [], Now = null };

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
}
