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
/// <param name="Item">
/// The item that became current. Two items over one source are different items, so this is what
/// tells them apart; <see cref="PlaylistItem.Source"/> reaches the source it plays.
/// </param>
/// <param name="MediaInfo">Metadata for <paramref name="Item"/>'s source.</param>
/// <param name="Index">
/// A running count of hand-offs since the playlist started (the first item is
/// <c>0</c>). It increments on every transition including loop wraps, so it is a
/// monotonic counter, not an index into the queue.
/// </param>
/// <param name="Wrapped">
/// <see langword="true"/> when this transition wrapped past the end of the queue
/// back to the start under <see cref="RepeatMode.All"/>.
/// </param>
/// <param name="Previous">
/// The item this transition left, or <see langword="null"/> when nothing preceded it. Under
/// <see cref="PlaylistTransitionReason.Loop"/> it is the same item as <paramref name="Item"/>.
/// </param>
/// <param name="Reason">
/// Why the hand-off happened, in terms of how <paramref name="Previous"/> ended.
/// </param>
/// <remarks>
/// <see cref="Reason"/> describes how the <i>previous</i> item ended, while every other member
/// describes the item that started. Attribute a reason to <see cref="Previous"/>, not to
/// <see cref="Item"/>: on a queue of <c>[A, B]</c> where A fails, the transition that follows
/// names B in <see cref="Item"/> and A in <see cref="Previous"/>.
/// </remarks>
public sealed record PlaylistTransition(
    PlaylistItem Item,
    MediaInfo MediaInfo,
    int Index,
    bool Wrapped,
    PlaylistItem? Previous,
    PlaylistTransitionReason Reason
);
