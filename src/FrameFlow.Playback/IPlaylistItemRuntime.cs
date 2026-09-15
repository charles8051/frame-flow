// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// The runtime a <see cref="PlaylistSession"/> plays one item on. <see cref="SubstrateSession"/>
/// implements it, and tests supply fakes, so the session's decisions run without media.
/// </summary>
internal interface IPlaylistItemRuntime : IPlaybackSession
{
    /// <summary>
    /// The number of the item's current run. It advances each time a seek or a rewind stops the
    /// run in progress, before the next run launches. See <see cref="SubstrateSession.RunNumber"/>.
    /// </summary>
    int RunNumber { get; }
}

/// <summary>Creates the runtime for each item a <see cref="PlaylistSession"/> opens.</summary>
internal interface IPlaylistItemRuntimeFactory
{
    /// <summary>
    /// Creates an item runtime that presents on the playlist's shared sinks and reports to
    /// <paramref name="callbacks"/>.
    /// </summary>
    IPlaylistItemRuntime CreateItem(IPlaybackClock clock, SessionCallbacks callbacks);
}
