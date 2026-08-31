// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Internal playback state machine states. The controller drives its Stateless
/// machine against this fine-grained enum so it can distinguish loading substages
/// for dispatch and diagnostics. Consumers observe the collapsed public
/// <see cref="PlaybackState"/> via <see cref="InternalPlaybackStateExtensions.ToPublicState"/>.
/// </summary>
internal enum InternalPlaybackState
{
    Idle,
    Initializing,
    Preparing,
    InitialBuffering,
    Paused,
    Playing,
    Rebuffering,
    Ended,
    Unloaded,
    Error,
}

internal static class InternalPlaybackStateExtensions
{
    /// <summary>
    /// Maps an <see cref="InternalPlaybackState"/> to its public
    /// <see cref="PlaybackState"/> projection. The transient loading substages
    /// <c>Initializing</c>, <c>Preparing</c>, and <c>InitialBuffering</c>
    /// collapse to <see cref="PlaybackState.Loading"/>.
    /// </summary>
    public static PlaybackState ToPublicState(this InternalPlaybackState state) =>
        state switch
        {
            InternalPlaybackState.Idle => PlaybackState.Idle,
            InternalPlaybackState.Initializing => PlaybackState.Loading,
            InternalPlaybackState.Preparing => PlaybackState.Loading,
            InternalPlaybackState.InitialBuffering => PlaybackState.Loading,
            InternalPlaybackState.Paused => PlaybackState.Paused,
            InternalPlaybackState.Playing => PlaybackState.Playing,
            InternalPlaybackState.Rebuffering => PlaybackState.Rebuffering,
            InternalPlaybackState.Ended => PlaybackState.Ended,
            InternalPlaybackState.Unloaded => PlaybackState.Unloaded,
            InternalPlaybackState.Error => PlaybackState.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
}
