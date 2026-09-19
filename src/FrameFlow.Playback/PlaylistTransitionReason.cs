// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Why a playlist hand-off happened. It describes how the <i>previous</i> item ended, not the item
/// that is now presenting — <c>PlaylistTransition.Previous</c> names the item it is about.
/// </summary>
/// <remarks>
/// Without this, a transition reports a natural end and a failure identically, so a consumer
/// counting completed passes counts failures as passes.
/// </remarks>
public enum PlaylistTransitionReason
{
    /// <summary>The player's first item started. Nothing preceded it.</summary>
    FirstItem,

    /// <summary>The previous item played to its end.</summary>
    EndOfItem,

    /// <summary>The previous item was skipped before its end.</summary>
    Skipped,

    /// <summary>A jump moved to this item.</summary>
    Jumped,

    /// <summary>The previous item failed and was passed over.</summary>
    ItemFailed,

    /// <summary>
    /// The current item played to its end and started again. <c>Previous</c> is the same item as
    /// the one that started.
    /// </summary>
    Loop,
}
