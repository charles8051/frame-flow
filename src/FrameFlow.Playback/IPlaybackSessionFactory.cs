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
}
