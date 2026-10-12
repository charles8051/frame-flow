// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A source, the position to start playing it from, and optionally where to stop: a moment inside
/// a file, such as a search hit, a chapter or a bookmark.
/// </summary>
/// <remarks>
/// <para>
/// A player's queue methods take a clip where they take a source, and the item they create
/// opens at <see cref="Start"/>. A source passed on its own is a clip that starts at zero.
/// </para>
/// <para>
/// <see cref="Start"/> and <see cref="End"/> are in the source's own time, the time <c>Position</c>
/// and a seek use. They are not rebased: an item that starts at 83.2 s reports a position of
/// 83.2 s when it starts.
/// </para>
/// <para>
/// Clips of one file should share one source object. A player reuses the open file when the next
/// item has the same source as the current one, and it compares sources by reference.
/// </para>
/// </remarks>
public readonly record struct MediaClip
{
    private readonly TimeSpan _start;
    private readonly TimeSpan? _end;

    /// <summary>
    /// Creates a clip of <paramref name="source"/> that starts at <paramref name="start"/> and,
    /// when <paramref name="end"/> is given, stops there.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> is negative, or <paramref name="end"/> is not after it.
    /// </exception>
    public MediaClip(IMediaSource source, TimeSpan start = default, TimeSpan? end = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        Start = start;
        End = end;
    }

    /// <summary>The source the clip plays.</summary>
    /// <remarks>Null only on <c>default(MediaClip)</c>, which a player refuses.</remarks>
    public IMediaSource Source { get; }

    /// <summary>Where the clip starts, in the source's time. Zero is the start of the source.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative, or not before <see cref="End"/>.
    /// </exception>
    public TimeSpan Start
    {
        get => _start;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(Start));
            if (_end is { } end)
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, end, nameof(Start));
            _start = value;
        }
    }

    /// <summary>
    /// Where the clip stops, in the source's time, exclusive, or null to play to the end of the
    /// source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The item ends when playback reaches it, as it would at the end of the source: the player
    /// advances, loops or ends by its repeat mode. No frame presented at or after it is shown. An
    /// audio buffer is played whole when it starts before it.
    /// </para>
    /// <para>
    /// A seek to or past it lets that run play on to the end of the source. A loop or replay of
    /// the item returns to <see cref="Start"/> and stops here again.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not after <see cref="Start"/>.</exception>
    public TimeSpan? End
    {
        get => _end;
        init
        {
            if (value is { } end)
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(end, _start, nameof(End));
            _end = value;
        }
    }
}
