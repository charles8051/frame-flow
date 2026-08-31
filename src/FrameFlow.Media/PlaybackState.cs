// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Primary playback region states exposed to consumers of
/// <see cref="FrameFlow.Playback.IPlaybackController"/>.
/// Seeking and repeat are tracked as orthogonal regions via
/// <see cref="SeekState"/> and <see cref="RepeatMode"/>.
/// </summary>
/// <remarks>
/// Transient loading substates used by the controller's internal state machine
/// are collapsed into the single <see cref="Loading"/> value on the public surface.
/// Consumers observe a single <c>Idle → Loading → Paused</c> sequence per load.
/// </remarks>
public enum PlaybackState
{
    /// <summary>No media loaded. Initial quiescent state.</summary>
    Idle,

    /// <summary>
    /// Media source is being opened, probed, and prepared for playback.
    /// Collapses the controller's internal <c>Initializing → Preparing → InitialBuffering</c>
    /// substates into a single observable value.
    /// </summary>
    Loading,

    /// <summary>Playback is suspended at the current position.</summary>
    Paused,

    /// <summary>Media is actively playing.</summary>
    Playing,

    /// <summary>Playback stalled waiting for data (network rebuffer).</summary>
    Rebuffering,

    /// <summary>Reached the natural end of the media stream.</summary>
    Ended,

    /// <summary>The media source has been unloaded and its pipeline torn down.</summary>
    Unloaded,

    /// <summary>An unrecoverable error occurred during playback.</summary>
    Error,
}
