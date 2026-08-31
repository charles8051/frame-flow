// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Read-only view over an <see cref="IPlaybackClock"/>. Components that need to
/// observe playback time but must not change clock state take a dependency on
/// this interface instead of the mutable <see cref="IPlaybackClock"/>.
/// </summary>
/// <remarks>
/// Per ADR-0028 §1, the <see cref="PlaybackSession"/> is the single owner of
/// clock state transitions. Any layer that needs to <em>read</em> the clock —
/// notably <c>PipelineController</c> for A/V sync — should take
/// <see cref="IReadOnlyPlaybackClock"/> so that the type system prevents
/// accidental mutation from outside the session's lifecycle methods.
/// </remarks>
public interface IReadOnlyPlaybackClock
{
    /// <summary>Gets the current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>Gets a value indicating whether the clock is running (started and not paused).</summary>
    bool IsRunning { get; }

    /// <summary>Gets a value indicating whether the clock is paused.</summary>
    bool IsPaused { get; }
}
