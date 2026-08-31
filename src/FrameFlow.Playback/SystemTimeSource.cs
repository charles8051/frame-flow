// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Production implementation of <see cref="ITimeSource"/> that delegates to
/// <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
internal sealed class SystemTimeSource : ITimeSource
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
