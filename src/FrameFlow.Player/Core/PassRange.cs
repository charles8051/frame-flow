// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Player.Core;

/// <summary>
/// The span of a source a pass delivers (#483), in media time: from <see cref="Start"/>,
/// inclusive, to <see cref="End"/>, exclusive, or to the end of the source when there is none.
/// </summary>
/// <remarks>
/// The pass's shell acts on it in three places: it seeks to <see cref="SeekTo"/> before the run,
/// the demux pump stops reading at <see cref="End"/>, and each decoded frame or audio buffer is
/// delivered only when <see cref="Delivers"/> says so. The seek lands on a keyframe at or before
/// the start, so the frames between it and the start are decoded and not delivered.
/// </remarks>
internal readonly record struct PassRange(TimeSpan Start, TimeSpan? End)
{
    /// <summary>The whole source.</summary>
    public static PassRange Whole => new(TimeSpan.Zero, null);

    /// <summary>Whether this is the whole source, so a pass has nothing to seek, stop or drop.</summary>
    public bool IsWhole => Start == TimeSpan.Zero && End is null;

    /// <summary>Where the source is positioned before the run, or null to read it from its start.</summary>
    public TimeSpan? SeekTo => Start > TimeSpan.Zero ? Start : null;

    /// <summary>
    /// Whether an item presented at <paramref name="timestamp"/> is delivered. A range that
    /// starts at zero starts at the source's start, so an item the source stamps before zero is
    /// delivered, as it is with no range.
    /// </summary>
    public bool Delivers(TimeSpan timestamp) =>
        (Start == TimeSpan.Zero || timestamp >= Start) && (End is not { } end || timestamp < end);
}
