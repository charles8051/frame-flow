// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Triggers for the orthogonal repeat/loop state machine region.
/// </summary>
internal enum RepeatTrigger
{
    /// <summary>Set repeat mode to Off.</summary>
    SelectOff,

    /// <summary>Set repeat mode to One (loop current item).</summary>
    SelectOne,

    /// <summary>Set repeat mode to All (loop the whole playlist).</summary>
    SelectAll,
}
