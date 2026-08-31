// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Triggers for the orthogonal seeking state machine region.
/// </summary>
internal enum SeekTrigger
{
    /// <summary>A seek operation has been requested by the user or application.</summary>
    SeekRequested,

    /// <summary>The pipeline has begun flushing buffers for the seek.</summary>
    FlushStarted,

    /// <summary>The seek is complete and new frames are ready.</summary>
    SeekCompleted,
}
