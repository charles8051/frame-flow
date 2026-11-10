// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// The failure of a load that replays the queue of a player that has already started an item,
/// when every item the queue offered failed to open (#857). The controller ends at it, in
/// <c>Ended</c>, as a live session does at the same run of failures, rather than entering
/// <c>Error</c>. The last item's failure is the inner exception.
/// </summary>
internal sealed class NoItemCouldStartException(Exception? innerException)
    : InvalidOperationException("No playlist item could be opened.", innerException);
