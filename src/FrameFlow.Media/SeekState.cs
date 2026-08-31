// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Orthogonal seeking region state, tracked independently from the primary playback state.
/// </summary>
public enum SeekState
{
    /// <summary>No seek operation is in progress.</summary>
    NotSeeking,

    /// <summary>A seek has been requested but not yet started by the pipeline.</summary>
    SeekPending,

    /// <summary>The seek is actively being processed (flush, reposition, refill).</summary>
    SeekInProgress,
}
