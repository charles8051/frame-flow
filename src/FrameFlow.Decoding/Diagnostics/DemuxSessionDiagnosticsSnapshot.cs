// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Diagnostics;

/// <summary>
/// Immutable snapshot of an <see cref="IDemuxSession"/>'s observable state at
/// a single point in time (ADR-0034).
/// </summary>
/// <param name="PacketsRead">
/// Total number of packets successfully returned from
/// <see cref="IDemuxSession.ReadPacketAsync"/> since the session opened.
/// Counts both video and audio packets across all streams in the container;
/// per-stream attribution is not tracked at this layer.
/// </param>
/// <param name="BytesRead">
/// Total bytes of compressed media data observed across all returned packets.
/// </param>
/// <param name="SeeksPerformed">
/// Number of successful <see cref="IDemuxSession.SeekAsync"/> calls.
/// </param>
/// <param name="EndOfStreamReached">
/// <see langword="true"/> after the underlying demuxer has signalled EOF on
/// at least one read attempt. Latches once set — a fresh seek does not clear
/// it. Use the seek count to disambiguate when needed.
/// </param>
public sealed record DemuxSessionDiagnosticsSnapshot(
    long PacketsRead,
    long BytesRead,
    long SeeksPerformed,
    bool EndOfStreamReached
)
{
    /// <summary>
    /// Zero-valued snapshot used as the seed value for rollups when no session
    /// is loaded.
    /// </summary>
    public static DemuxSessionDiagnosticsSnapshot Empty { get; } =
        new(PacketsRead: 0, BytesRead: 0, SeeksPerformed: 0, EndOfStreamReached: false);
}
