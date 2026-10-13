// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Whisper.Core;

namespace FrameFlow.Whisper;

/// <summary>
/// Holds captions stamped with the presentation time of the frame on screen when each arrived,
/// and answers which to show at a frame. For each presented frame, call
/// <see cref="Add(Caption, TimeSpan, TimelineId)"/> for each caption that has arrived and then
/// <see cref="GetActive(TimeSpan, TimelineId)"/>, both with the PTS of that frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why arrival, not <see cref="Caption.From"/>.</b> Whisper emits a caption only once it has
/// transcribed a whole window, by which time much of the caption's <c>[From, To]</c> has
/// usually played. Shown from the frame it arrived with for the display duration, it stays up
/// long enough to read.
/// </para>
/// <para>
/// <b>Timelines (ADR-0084).</b> Give <see cref="GetActive(TimeSpan, TimelineId)"/> the presented
/// frame's timeline, <c>FramePresentedInfo.Timeline</c>, and
/// <see cref="Add(Caption, TimeSpan, TimelineId)"/> each caption's, <see cref="CaptionRef.Timeline"/>.
/// The order comes from what is presented. A frame on a timeline other than the one on screen (a
/// seek, a loop, the next item) starts a new timeline with nothing from the one it left, whichever
/// way its time moved. A caption that names a timeline, and once a frame has named one any caption,
/// is matched against the frame given to the next <see cref="GetActive(TimeSpan, TimelineId)"/>, so
/// one that arrives with the first frame of a new timeline is judged on that timeline. A caption of
/// that frame's timeline, or of none, shows from that frame. A caption of a timeline the screen has
/// left, still in flight when it was left, is dropped. A caption of a timeline not presented yet is
/// held, and shows from the first frame of its timeline; one of a timeline that is never presented
/// never shows, even when it arrives before the first frame.
/// </para>
/// <para>
/// <b>Without timelines.</b> Until a frame names a timeline (frames on <see cref="TimelineId.None"/>,
/// or the overloads without a timeline), <see cref="Add(Caption, TimeSpan)"/> and
/// <see cref="GetActive(TimeSpan)"/> share a clock: the latest PTS either has been given. Within a
/// run of playback it only rises. A PTS below it is a seek back, a loop or the next item, and starts
/// a new timeline with nothing on it. A frame on <see cref="TimelineId.None"/> keeps this rule after
/// a timeline has been named too. A jump the PTS does not show, such as a seek forward by less
/// than the display duration, needs <see cref="Reset"/>. A caption for audio from before a jump
/// that arrives after it lands on the new timeline, since nothing tells it apart; a caption on
/// <see cref="TimelineId.None"/> always belongs to the timeline on screen. A caption that names a
/// timeline joins the next frame that names none, while no frame has named one.
/// </para>
/// <para>
/// <b>Which PTS.</b> Give both calls the PTS of the frame just presented. Not a caption's own
/// <see cref="Caption.From"/>, which sits behind that frame, and not a position polled on a
/// timer or read from an audio clock. Without timelines, any step back, however small, starts the
/// timeline again.
/// </para>
/// <para>
/// <b>Bounds.</b> A caption on screen is dropped once it is more than the display duration behind
/// the clock, or once newer captions leave it no room to show. A held caption has no clock yet, so
/// it is bounded by count: each timeline keeps what could show once it presents, the latest batch,
/// or with <c>maxStackedLines</c> above one, that many of the latest captions. The captions of at
/// most <see cref="FrameFlow.Graph.Core.PresentedTimeline.DefaultHistory"/> timelines wait at once,
/// and the timeline heard from least recently goes first. A caption held for a timeline that is
/// never presented stays until those bounds or <see cref="Reset"/> drop it.
/// </para>
/// <para>
/// <b>Concurrency.</b> Each call is atomic. The calls still need one caller's frame order. Without
/// timelines, two callers at different positions would each start the timeline again; with them,
/// two callers on different timelines would.
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
    /// Adds <paramref name="caption"/>, on no known timeline, as arriving with the frame at
    /// <paramref name="enqueuedAtPts"/>, the frame just presented. Until a frame names a timeline,
    /// a PTS below the clock starts a new timeline first.
    /// </summary>
    public void Add(Caption caption, TimeSpan enqueuedAtPts) =>
        Add(caption, enqueuedAtPts, TimelineId.None);

    /// <summary>
    /// Adds <paramref name="caption"/>, transcribed on <paramref name="timeline"/>, as arriving
    /// with the frame at <paramref name="enqueuedAtPts"/>, the frame just presented. When the
    /// caption names a timeline, or a frame has named one, the caption is matched at the next
    /// <see cref="GetActive(TimeSpan, TimelineId)"/>, against that frame's timeline: shown if it is
    /// of that timeline or of none, dropped if the screen has left its timeline, and held if its
    /// timeline has not been presented.
    /// </summary>
    /// <param name="caption">The caption.</param>
    /// <param name="enqueuedAtPts">The PTS of the frame just presented.</param>
    /// <param name="timeline">
    /// The caption's own timeline, <see cref="CaptionRef.Timeline"/>, or
    /// <see cref="TimelineId.None"/> when it is not known.
    /// </param>
    public void Add(Caption caption, TimeSpan enqueuedAtPts, TimelineId timeline)
    {
        ArgumentNullException.ThrowIfNull(caption);
        lock (_gate)
        {
            _state = _state.Add(caption, enqueuedAtPts, timeline);
        }
    }

    /// <summary>
    /// Returns the captions to show at <paramref name="framePts"/>, on a frame that names no
    /// timeline. A PTS below the clock starts a new timeline first.
    /// </summary>
    public IReadOnlyList<Caption> GetActive(TimeSpan framePts) => GetActive(framePts, TimelineId.None);

    /// <summary>
    /// Returns the captions to show at the frame at <paramref name="framePts"/> on
    /// <paramref name="timeline"/>, oldest first: the latest batch, or with <c>maxStackedLines</c>
    /// above one, up to that many of the latest captions. Empty when none has arrived within the
    /// display duration. A timeline other than the one on screen starts a new timeline first. The
    /// captions added since the last call, and those held for this timeline, are then matched
    /// against it.
    /// </summary>
    /// <param name="framePts">The PTS of the frame just presented.</param>
    /// <param name="timeline">
    /// The presented frame's timeline, <c>FramePresentedInfo.Timeline</c>, or
    /// <see cref="TimelineId.None"/> when it names none.
    /// </param>
    /// <remarks>
    /// Whisper emits the segments of one window in one burst, so they arrive with the same frame
    /// and show together, each on its own line.
    /// </remarks>
    public IReadOnlyList<Caption> GetActive(TimeSpan framePts, TimelineId timeline)
    {
        lock (_gate)
        {
            _state = _state.MoveTo(framePts, timeline);
            return _state.OnScreen();
        }
    }

    /// <summary>
    /// Drops every caption, shown or held, and the clock, as if the timeline were new. For a jump
    /// the PTS does not show on frames that name no timeline; a PTS below the clock starts a new
    /// timeline without it. The timelines already left are kept, so a caption from one of them is
    /// still dropped.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state = _state.Reset();
        }
    }
}
