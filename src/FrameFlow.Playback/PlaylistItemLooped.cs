// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// The current playlist item is back at its start after playing to its end. This says which item
/// looped.
/// </summary>
/// <param name="Item">The item that looped.</param>
/// <param name="Loop">
/// The same <see cref="LoopRestarted"/> instance the player raises on its <c>LoopRestarted</c> for
/// this loop, by reference, carrying the count and the item duration. A consumer subscribed to both
/// discards the second sighting by reference equality.
/// </param>
/// <remarks>
/// Raised on <c>IMediaPlayer.ItemLooped</c>, which is the channel to prefer over
/// <c>LoopRestarted</c> when the item matters. Both report the same loop, <c>ItemLooped</c> first.
/// Nothing is raised while the session is disposing, or from a session the controller has replaced.
/// </remarks>
public sealed record PlaylistItemLooped(PlaylistItem Item, LoopRestarted Loop);
