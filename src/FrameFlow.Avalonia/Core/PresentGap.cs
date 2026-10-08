// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Avalonia.Core;

/// <summary>
/// The wait before a frame reached the sink, set against how long the frame before it was meant
/// to show (#576). Pure.
/// </summary>
/// <param name="Interval">How long after the previous frame this one arrived.</param>
/// <param name="Held">
/// The previous frame's <see cref="IVideoFrame.Duration"/>: one frame interval for video, the whole
/// dwell for a still. Zero when the frame did not say.
/// </param>
internal readonly record struct PresentGap(TimeSpan Interval, TimeSpan Held)
{
    /// <summary>
    /// How late a frame may arrive, past the end of the previous frame's display, before the wait
    /// counts as a stall.
    /// </summary>
    public static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long after the previous frame finished showing this one arrived. Zero when it arrived in
    /// time. A negative <see cref="Held"/> counts as zero.
    /// </summary>
    public TimeSpan Overrun
    {
        get
        {
            var held = Held > TimeSpan.Zero ? Held : TimeSpan.Zero;
            return Interval > held ? Interval - held : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// <see langword="true"/> when upstream stopped delivering: the frame arrived more than
    /// <see cref="StallThreshold"/> after the previous frame finished showing.
    /// </summary>
    /// <remarks>
    /// A still held through <see cref="MediaSource.FromStill"/> is one frame whose duration is the
    /// dwell, and the next item's first frame follows it a dwell later by design. Measured from the
    /// still's arrival that is a gap of seconds; measured from the end of its display it is the
    /// transition, which is what a stall is judged on.
    /// </remarks>
    public bool IsStall => Overrun > StallThreshold;
}

/// <summary>
/// Remembers when the last frame arrived and how long it is meant to show, so the wait before the
/// next frame can be judged against it (#576). Pure: the caller supplies each arrival, and the next
/// state is returned rather than stored.
/// </summary>
internal readonly struct PresentGapTracker
{
    // Null until the first frame: there is no gap before it.
    private readonly TimeSpan? _lastArrival;
    private readonly TimeSpan _lastDuration;

    private PresentGapTracker(TimeSpan lastArrival, TimeSpan lastDuration)
    {
        _lastArrival = lastArrival;
        _lastDuration = lastDuration;
    }

    /// <summary>
    /// Folds in a frame that arrived at <paramref name="arrival"/> and is meant to show for
    /// <paramref name="duration"/>.
    /// </summary>
    /// <param name="arrival">When the frame arrived, on any clock that only moves forward.</param>
    /// <param name="duration">The frame's <see cref="IVideoFrame.Duration"/>.</param>
    /// <returns>
    /// The next state, and the gap before this frame, judged on the previous frame's duration.
    /// <see langword="null"/> for the first frame.
    /// </returns>
    public (PresentGapTracker Next, PresentGap? Gap) Observe(TimeSpan arrival, TimeSpan duration) =>
        (
            new PresentGapTracker(arrival, duration),
            _lastArrival is { } last ? new PresentGap(arrival - last, _lastDuration) : null
        );
}
