// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Captures a state transition from <paramref name="Previous"/> to <paramref name="Current"/>.
/// Consumers react to state pairs rather than internal trigger names — diagnostic trigger
/// information is available via structured logging (see ADR-0010).
/// </summary>
/// <typeparam name="T">The enum type of the state (e.g. <see cref="PlaybackState"/>, <see cref="SeekState"/>).</typeparam>
/// <param name="Previous">The state before the transition.</param>
/// <param name="Current">The state after the transition.</param>
public readonly record struct StateTransition<T>(T Previous, T Current)
    where T : struct, Enum;
