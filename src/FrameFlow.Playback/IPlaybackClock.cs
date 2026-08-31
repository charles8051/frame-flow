// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Provides the current playback position and state-transition operations
/// (<see cref="Start"/>, <see cref="Pause"/>, <see cref="Resume"/>,
/// <see cref="Seek"/>, <see cref="Stop"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ownership (ADR-0028 §1)</strong> — the clock is created by
/// <c>PlaybackController</c> and passed to <c>PlaybackSession</c>. Only
/// <c>PlaybackSession</c> is permitted to call the mutation methods on this
/// interface. Every other layer should take a dependency on
/// <see cref="IReadOnlyPlaybackClock"/> and invoke session lifecycle methods
/// (<c>PlayAsync</c>, <c>PauseAsync</c>, <c>SeekAsync</c>, <c>DisposeAsync</c>)
/// when clock state needs to change.
/// </para>
/// <para>
/// This ownership rule is documented convention rather than a compile-time
/// constraint. A reviewer catching a direct <c>_clock.Start()</c> call from
/// anywhere other than <c>PlaybackSession</c> should reject the change.
/// </para>
/// </remarks>
public interface IPlaybackClock : IReadOnlyPlaybackClock
{
    /// <summary>Starts the clock at the given position.</summary>
    void Start(TimeSpan startPosition);

    /// <summary>Pauses the clock, freezing the current position.</summary>
    void Pause();

    /// <summary>Resumes the clock from the paused position.</summary>
    void Resume();

    /// <summary>Seeks the clock to the given position.</summary>
    void Seek(TimeSpan position);

    /// <summary>Stops the clock and resets the position to zero.</summary>
    void Stop();
}
