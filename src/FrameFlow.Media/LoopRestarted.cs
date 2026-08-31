// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Event data emitted when the repeat region restarts playback from the beginning.
/// </summary>
/// <param name="LoopCount">The 1-based iteration count after the restart.</param>
/// <param name="ItemDuration">The duration of the item that just finished.</param>
public sealed record LoopRestarted(int LoopCount, TimeSpan ItemDuration);
