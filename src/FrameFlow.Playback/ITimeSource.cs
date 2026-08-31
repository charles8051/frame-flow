// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Abstracts the source of wall-clock time for testability.
/// Production code uses <see cref="SystemTimeSource"/>; tests inject a fake implementation.
/// </summary>
public interface ITimeSource
{
    /// <summary>Gets the current UTC date and time.</summary>
    DateTimeOffset UtcNow { get; }
}
