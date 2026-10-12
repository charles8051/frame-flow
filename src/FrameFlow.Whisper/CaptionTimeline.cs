// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Whisper.Core;

namespace FrameFlow.Whisper;

/// <summary>
/// Holds captions stamped with the presentation time of the frame on screen when each arrived,
/// and answers which to show at a frame. Call <see cref="Add"/> for each caption that has
/// arrived and then <see cref="GetActive"/>, both with the PTS of the frame just presented.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why arrival, not <see cref="Caption.From"/>.</b> Whisper emits a caption only once it has
/// transcribed a whole window, by which time much of the caption's <c>[From, To]</c> has
/// usually played. Shown from the frame it arrived with for the display duration, it stays up
/// long enough to read.
/// </para>
/// <para>
/// <b>One clock.</b> <see cref="Add"/> and <see cref="GetActive"/> share a clock: the latest PTS
/// either has been given. Within a run of playback it only rises. A PTS below it is a seek
/// back, a loop or the next item, and starts a new timeline with nothing on it: no caption from
/// before the jump shows again, and the time before it no longer evicts the captions after it.
/// A jump the PTS does not show, such as a seek forward by less than the display duration,
/// needs <see cref="Reset"/>.
/// </para>
/// <para>
/// <b>Which PTS.</b> Give both calls the PTS of the frame just presented. Not a caption's own
/// <see cref="Caption.From"/>, which sits behind that frame, and not a position polled on a
/// timer or read from an audio clock, which can step back slightly: any step back, however
/// small, starts the timeline again.
/// </para>
/// <para>
/// <b>Captions in flight at a jump.</b> A caption for audio from before a jump that arrives
/// after it lands on the new timeline, since the timeline cannot tell it apart. A caller that
/// can, by the graph or position its audio came from, drops it before <see cref="Add"/>.
/// </para>
/// <para>
/// <b>Eviction.</b> A caption more than the display duration behind the clock is dropped as
/// the clock advances, so the timeline holds only what can still be shown.
/// </para>
/// <para>
/// <b>Concurrency.</b> Each call is atomic. The calls still need one caller's PTS order: two
/// callers at different positions would each start the timeline again.
/// </para>
/// </remarks>
/// <param name="displayDuration">How long a caption shows after the frame it arrived with.</param>
/// <param name="maxStackedLines">
/// Above one, the most captions shown at once. One or less shows the latest batch: every
/// caption that arrived with the same frame as the last.
/// </param>
public sealed class CaptionTimeline(TimeSpan displayDuration, int maxStackedLines = 1)
{
    private readonly object _gate = new();
    private CaptionTimelineState _state = CaptionTimelineState.Empty(displayDuration, maxStackedLines);

    /// <summary>
    /// Adds <paramref name="caption"/> as arriving with the frame at
    /// <paramref name="enqueuedAtPts"/>, the frame just presented. A PTS below the clock starts a
    /// new timeline first.
    /// </summary>
    public void Add(Caption caption, TimeSpan enqueuedAtPts)
    {
        ArgumentNullException.ThrowIfNull(caption);
        lock (_gate)
        {
            _state = _state.Add(caption, enqueuedAtPts);
        }
    }

    /// <summary>
    /// Returns the captions to show at <paramref name="framePts"/>, oldest first: the latest
    /// batch, or with <c>maxStackedLines</c> above one, up to that many of the latest captions.
    /// Empty when none has arrived within the display duration. A PTS below the clock starts a
    /// new timeline first.
    /// </summary>
    /// <remarks>
    /// Whisper emits the segments of one window in one burst, so they arrive with the same frame
    /// and show together, each on its own line.
    /// </remarks>
    public IReadOnlyList<Caption> GetActive(TimeSpan framePts)
    {
        lock (_gate)
        {
            _state = _state.MoveTo(framePts);
            return _state.OnScreen();
        }
    }

    /// <summary>
    /// Drops every caption and the clock, as if the timeline were new. For a jump the PTS does
    /// not show; a PTS below the clock starts a new timeline without it.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state = _state.Reset();
        }
    }
}
