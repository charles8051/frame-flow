// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Event data emitted when a single-item loop (<c>RepeatMode.One</c>) appears to
/// have stalled: the playback position has run <i>past</i> the item duration
/// without a loop restart — i.e. frame delivery stopped while the clock kept
/// advancing. This is the "video frozen on the last frame while the seeker keeps
/// moving" failure, observable rather than silent.
/// </summary>
/// <param name="LoopCount">The loop counter at detection — stuck, not advancing.</param>
/// <param name="Position">The current position, which has overrun <paramref name="Duration"/>.</param>
/// <param name="Duration">The loaded item duration.</param>
/// <param name="Overrun">How long the position has been past <paramref name="Duration"/> with no restart.</param>
public sealed record LoopStalled(int LoopCount, TimeSpan Position, TimeSpan Duration, TimeSpan Overrun);
