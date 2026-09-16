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
    /// and continue. Every player holds a playlist, and one built over a single
    /// source holds that source alone, so <see cref="All"/> wraps to it and
    /// loops. It differs from <see cref="One"/> only for a player holding more
    /// than one item, which is the distinct behaviour ADR-0027 §3 asked for.
    /// </summary>
    All,
}
