// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// How a playlist item failed. The session decides this when it skips the item; before
/// <c>PlaylistItemFailed</c> existed it reached a caller only as prose inside the error message.
/// </summary>
/// <remarks>
/// The distinction is worth acting on: an item that could not be started may still be openable
/// later — a file not yet copied into place, a stream not yet live — while one that faulted
/// part-way through played far enough to prove it can be opened.
/// </remarks>
public enum PlaylistItemFailure
{
    /// <summary>It could not be opened, warmed or started.</summary>
    CouldNotStart,

    /// <summary>It faulted while it played.</summary>
    FaultedDuringPlayback,
}
