// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Whisper;

/// <summary>
/// Thread-safe timeline that holds <see cref="Caption"/> entries
/// stamped with the video PTS at which they were enqueued (i.e. the
/// moment they became visible to the renderer). Used internally by
/// <c>CaptionOverlayPipelineExtensions.OverlayOnto</c> to answer
/// per-frame queries of "which captions should be shown at PTS
/// <c>t</c>?" — see the implementation file for why arrival-stamped
/// display is the right model for live captioning instead of
/// <c>Caption.[From, To]</c> matching.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> A single video-side pump calls both
/// <see cref="Add"/> (draining the caption queue when a new frame
/// arrives) and <see cref="GetActive"/> (querying the timeline for
/// that frame). The lock is there as defence in depth; current callers
/// don't actually race. Both operations are O(n) over the entries
/// currently retained, which is bounded by
/// <see cref="_displayDuration"/> via the eviction in
/// <see cref="GetActive"/>.
/// </para>
/// <para>
/// <b>Eviction.</b> Entries whose <see cref="TimelineEntry.EnqueuedAtPts"/>
/// is more than <see cref="_displayDuration"/> behind the most-recent
/// queried PTS are dropped on the next <see cref="GetActive"/> call.
/// Keeps the backing list O(captions-per-display-window) — typically
/// 1–2 — without a separate sweep task.
/// </para>
/// </remarks>
public sealed class CaptionTimeline(TimeSpan displayDuration, int maxStackedLines = 1)
{
    private readonly object _gate = new();
    private readonly List<TimelineEntry> _entries = [];
    private readonly TimeSpan _displayDuration = displayDuration;
    private readonly int _maxStackedLines = maxStackedLines;
    private TimeSpan _highWaterMark = TimeSpan.MinValue;

    /// <summary>
    /// Adds <paramref name="caption"/> to the timeline stamped with
    /// <paramref name="enqueuedAtPts"/> — typically the video PTS of
    /// the frame currently being processed when the caption was
    /// pulled off the pump's queue. Entries are appended; the
    /// timeline relies on monotonically-increasing PTS (which is
    /// guaranteed because video frames flow in PTS order and the
    /// drain happens per-frame).
    /// </summary>
    public void Add(Caption caption, TimeSpan enqueuedAtPts)
    {
        ArgumentNullException.ThrowIfNull(caption);
        lock (_gate)
        {
            _entries.Add(new TimelineEntry(caption, enqueuedAtPts));
        }
    }

    /// <summary>
    /// Returns the captions to display at <paramref name="framePts"/>.
    /// The returned set is the most-recent batch — captions sharing
    /// the highest <see cref="TimelineEntry.EnqueuedAtPts"/> that is
    /// still within the display window. Older entries are evicted as
    /// a side effect. Returns an empty list when no caption has
    /// arrived yet, or the most recent one has aged out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Most-recent-batch semantics.</b> Whisper emits 1..N segments
    /// per inference window; they all arrive in the pump's burst, get
    /// stamped with the same frame PTS, and end up adjacent in
    /// <see cref="_entries"/>. Returning the latest batch keeps a
    /// multi-segment chunk visible together (each segment on its own
    /// caption line) instead of showing only the last one.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Caption> GetActive(TimeSpan framePts)
    {
        lock (_gate)
        {
            if (framePts > _highWaterMark)
                _highWaterMark = framePts;

            // Evict entries older than the display window. Cheap because
            // entries are append-only in PTS order.
            var cutoff = _highWaterMark - _displayDuration;
            if (cutoff > TimeSpan.Zero)
            {
                int drop = 0;
                while (drop < _entries.Count && _entries[drop].EnqueuedAtPts < cutoff)
                    drop++;
                if (drop > 0)
                    _entries.RemoveRange(0, drop);
            }

            if (_entries.Count == 0)
                return Array.Empty<Caption>();

            // Find the latest qualifying entry (EnqueuedAtPts <=
            // framePts and within the display window).
            int latest = -1;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.EnqueuedAtPts > framePts)
                    continue;
                if (framePts - e.EnqueuedAtPts > _displayDuration)
                    break;
                latest = i;
                break;
            }
            if (latest < 0)
                return Array.Empty<Caption>();

            var latestBatchPts = _entries[latest].EnqueuedAtPts;

            if (_maxStackedLines <= 1)
            {
                // Single-line mode: return the latest *batch* (every
                // entry sharing latestBatchPts — the burst from one
                // Whisper inference call shown together on the same
                // visual line).
                int batchStart = latest;
                while (batchStart > 0 && _entries[batchStart - 1].EnqueuedAtPts == latestBatchPts)
                    batchStart--;

                int count = latest - batchStart + 1;
                if (count == 1)
                    return new[] { _entries[latest].Caption };

                var hits = new Caption[count];
                for (int i = 0; i < count; i++)
                    hits[i] = _entries[batchStart + i].Caption;
                return hits;
            }

            // Multi-line stack mode: walk backwards from latest,
            // collecting up to _maxStackedLines DISTINCT entries that
            // are still within the display window. Order oldest →
            // newest so callers can render top-to-bottom in
            // chronological reading order.
            var stack = new List<Caption>(_maxStackedLines);
            for (int i = latest; i >= 0 && stack.Count < _maxStackedLines; i--)
            {
                var e = _entries[i];
                if (framePts - e.EnqueuedAtPts > _displayDuration)
                    break;
                stack.Add(e.Caption);
            }
            stack.Reverse();
            return stack;
        }
    }

    private readonly record struct TimelineEntry(Caption Caption, TimeSpan EnqueuedAtPts);
}
