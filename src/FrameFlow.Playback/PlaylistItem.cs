// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// One place in a playlist player's queue: a source with an identity of its own.
/// </summary>
/// <remarks>
/// <para>
/// The player creates items, and they compare by reference. The same source added twice is two
/// items, so a caller can jump to or remove one of them without naming it by position.
/// </para>
/// <para>
/// The constructor is public so a caller can build one for a test double or a decorator over
/// <c>IMediaPlaylistPlayer</c> (#318). An item built that way belongs to no player, and reference
/// equality is what says so: <c>JumpToAsync</c> and <c>RemoveAsync</c> refuse it with a failed
/// <see cref="Result"/>, exactly as they refuse an item another player owns.
/// </para>
/// </remarks>
public sealed class PlaylistItem
{
    /// <summary>Creates an item over <paramref name="source"/>, belonging to no player.</summary>
    public PlaylistItem(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }

    /// <summary>The source this item plays.</summary>
    public IMediaSource Source { get; }

    /// <inheritdoc/>
    public override string ToString() => Source.DisplayName;
}
