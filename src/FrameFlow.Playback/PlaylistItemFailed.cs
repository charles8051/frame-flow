// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// One playlist item failed and was skipped. The player carries on; this says which item it was,
/// and how it failed.
/// </summary>
/// <param name="Item">The item that failed.</param>
/// <param name="Error">
/// The same <see cref="PlaybackError"/> instance the player raises on its
/// <c>ErrorOccurred</c> for this failure, by reference. A consumer subscribed to both discards the
/// second sighting by reference equality.
/// </param>
/// <param name="Failure">How it failed.</param>
/// <remarks>
/// <para>
/// Raised on <c>IMediaPlaylistPlayer.ItemFailed</c>, which is the channel to prefer over
/// <c>ErrorOccurred</c> for item failures. Both report the same failure, <c>ItemFailed</c> first;
/// <c>ErrorOccurred</c> stays because it is the only failure signal a caller holding the smaller
/// <c>IMediaPlayer</c> surface has.
/// </para>
/// <para>
/// It does not fire for every failure. The first item of a player is treated as a single source's,
/// so a failure before anything has played is fatal rather than an item failure and this is not
/// raised. Nothing is raised while the session is disposing, or from a session the controller has
/// replaced.
/// </para>
/// </remarks>
public sealed record PlaylistItemFailed(
    PlaylistItem Item,
    PlaybackError Error,
    PlaylistItemFailure Failure
);
