// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Orthogonal repeat/loop region state, tracked independently from the primary playback state.
/// </summary>
public enum RepeatMode
{
    /// <summary>No looping — playback ends at the end of the media.</summary>
    Off,

    /// <summary>Loop the current item indefinitely.</summary>
    One,

    /// <summary>
    /// Loop the whole playlist: at the end of the last item, wrap to the first
    /// and continue. For a single-source player (no playlist) this behaves like
    /// <see cref="Off"/> — there is nothing to wrap to, so playback ends at the
    /// end of the media. Only a playlist-capable player
    /// (<c>FrameFlow.Player.IMediaPlaylistPlayer</c>) gives <see cref="All"/> a
    /// distinct meaning.
    /// </summary>
    All,
}
