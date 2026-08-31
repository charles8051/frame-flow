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
);
