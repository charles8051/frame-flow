// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// One place in a playlist player's queue: a source with an identity of its own.
/// </summary>
/// <remarks>
/// The player creates items, and they compare by reference. The same source added twice is two
/// items, so a caller can jump to or remove one of them without naming it by position.
/// </remarks>
public sealed class PlaylistItem
{
    internal PlaylistItem(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }

    /// <summary>The source this item plays.</summary>
    public IMediaSource Source { get; }

    /// <inheritdoc/>
    public override string ToString() => Source.DisplayName;
}
