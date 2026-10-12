// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

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

    /// <summary>
    /// Opens <paramref name="source"/> positioned at <paramref name="start"/>, in the source's
    /// time, so that the warm-up that follows decodes from there and nothing before it is
    /// presented. When <paramref name="end"/> is given, a run that starts before it ends there.
    /// </summary>
    /// <remarks>
    /// Fails, as an open does, when the source cannot be positioned there: it reports a duration
    /// and <paramref name="start"/> is at or past it, or the demuxer cannot seek it.
    /// </remarks>
    ValueTask InitializeAsync(
        IMediaSource source,
        TimeSpan start,
        TimeSpan? end,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Rewinds the runtime in place to <paramref name="position"/>, the start of the item it now
    /// plays, as <see cref="IPlaybackSession.RewindToStartAsync"/> rewinds it to zero. The runs
    /// that follow stop at <paramref name="end"/>, that item's end, when it is given.
    /// </summary>
    /// <remarks>
    /// Fails, as an open would, when the source reports a duration and
    /// <paramref name="position"/> is at or past it.
    /// </remarks>
    ValueTask RewindToAsync(TimeSpan position, TimeSpan? end, CancellationToken cancellationToken);
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
