// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Event data emitted when the current item is back at its start after it played to its end. It is
/// not emitted for a skip, a jump, or a rebuild after a failure.
/// </summary>
/// <param name="LoopCount">
/// The 1-based loop count. A playlist counts consecutive loops of the current item, and counts from 1
/// again after any other start. A single-source player counts every loop it has run.
/// </param>
/// <param name="ItemDuration">The duration of the item that looped.</param>
public sealed record LoopRestarted(int LoopCount, TimeSpan ItemDuration);
