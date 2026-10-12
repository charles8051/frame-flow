// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A source and the position to start playing it from: a moment inside a file, such as a search
/// hit, a chapter or a bookmark.
/// </summary>
/// <remarks>
/// <para>
/// A player's queue methods take a clip where they take a source, and the item they create
/// opens at <see cref="Start"/>. A source passed on its own is a clip that starts at zero.
/// </para>
/// <para>
/// <see cref="Start"/> is in the source's own time, the time <c>Position</c> and a seek use. It is
/// not rebased: an item that starts at 83.2 s reports a position of 83.2 s when it starts.
/// </para>
/// <para>
/// Clips of one file should share one source object. A player reuses the open file when the next
/// item has the same source as the current one, and it compares sources by reference.
/// </para>
/// </remarks>
public readonly record struct MediaClip
{
    private readonly TimeSpan _start;

    /// <summary>Creates a clip of <paramref name="source"/> that starts at <paramref name="start"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is negative.</exception>
    public MediaClip(IMediaSource source, TimeSpan start = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        Start = start;
    }

    /// <summary>The source the clip plays.</summary>
    /// <remarks>Null only on <c>default(MediaClip)</c>, which a player refuses.</remarks>
    public IMediaSource Source { get; }

    /// <summary>Where the clip starts, in the source's time. Zero is the start of the source.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan Start
    {
        get => _start;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(Start));
            _start = value;
        }
    }
}
