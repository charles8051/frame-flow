// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Notification raised when a playlist-capable player hands off presentation
/// from one source to the next. The hand-off keeps the video + audio sinks and
/// their GPU resources warm across the boundary — only the per-item decode
/// runtime is swapped — so a consumer observing this stream should update its
/// own model (and optionally enqueue the following item), not rebuild any
/// presenter.
/// </summary>
/// <param name="Source">The source that is now presenting.</param>
/// <param name="MediaInfo">Metadata for <paramref name="Source"/>.</param>
/// <param name="Index">
/// A running count of hand-offs since the playlist started (the first item is
/// <c>0</c>). It increments on every transition including loop wraps, so it is a
/// monotonic counter, not an index into the queue.
/// </param>
/// <param name="Wrapped">
/// <see langword="true"/> when this transition wrapped past the end of the queue
/// back to the start under <see cref="RepeatMode.All"/>.
/// </param>
public sealed record PlaylistTransition(
    IMediaSource Source,
    MediaInfo MediaInfo,
    int Index,
    bool Wrapped
)
{
    /// <summary>
    /// The item that became current. Every transition the player raises sets it, so a
    /// subscriber can tell two items of one source apart. It is <see langword="null"/> only on
    /// a transition built with the four-argument constructor.
    /// </summary>
    public PlaylistItem? Item { get; init; }

    /// <summary>
    /// The item this transition left, or <see langword="null"/> when nothing preceded it. Under
    /// <see cref="PlaylistTransitionReason.Loop"/> it is the same item as <see cref="Item"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Reason"/> describes how <i>this</i> item ended, while every other member of the
    /// record describes the item that started. Attribute a reason to this one, not to
    /// <see cref="Item"/>: on a queue of <c>[A, B]</c> where A fails, the transition that follows
    /// names B in <see cref="Item"/> and A here.
    /// </remarks>
    public PlaylistItem? Previous { get; init; }

    /// <summary>
    /// Why the hand-off happened, in terms of how <see cref="Previous"/> ended. Defaults to
    /// <see cref="PlaylistTransitionReason.FirstItem"/> on a transition built without one.
    /// </summary>
    public PlaylistTransitionReason Reason { get; init; }
}
