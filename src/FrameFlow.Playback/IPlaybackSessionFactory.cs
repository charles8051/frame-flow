// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

internal interface IPlaybackSessionFactory
{
    /// <summary>
    /// Creates a new playback session bound to the supplied clock and wired to
    /// the controller's <paramref name="callbacks"/>. Per ADR-0028 §4, callbacks
    /// are injected at construction time rather than post-hoc so the session
    /// never observes a partially wired callback channel.
    /// </summary>
    IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks);

    /// <summary>
    /// Called with the controller's repeat mode when the controller is created and each time the
    /// mode changes, on the controller's dispatch loop. A factory whose sessions loop internally
    /// passes it to the queue they share. A single-source factory ignores it.
    /// </summary>
    void RepeatModeChanged(RepeatMode mode) { }

    /// <summary>
    /// The source a session created now would start with, reserved so the load that follows plays
    /// it rather than replacing what the factory holds. <see langword="null"/> when there is
    /// nothing to start, which is the answer for a factory that has no queue of its own.
    /// </summary>
    /// <remarks>
    /// Read by the controller when <c>Play</c> arrives at <c>Idle</c>, which is the state a player
    /// built with an empty queue sits in until something is added to it.
    /// </remarks>
    IMediaSource? ReserveStart() => null;
}
