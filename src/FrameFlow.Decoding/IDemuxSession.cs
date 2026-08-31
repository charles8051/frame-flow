// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media;

namespace FrameFlow.Decoding;

/// <summary>
/// Represents an open media source from which packets can be read and streams inspected.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IDemuxSession"/> is the primary access point for media container data.
/// It exposes stream metadata via <see cref="MediaInfo"/> and supports sequential
/// packet reading via <see cref="ReadPacketAsync"/> and random-access via
/// <see cref="SeekAsync"/>.
/// </para>
/// <para>
/// Ownership: the session owns its native <c>AVFormatContext</c> and all associated
/// resources. Callers must dispose the session when finished; disposal is the sole
/// correct way to release the underlying format context (ADR-0005, ADR-0013).
/// </para>
/// <para>
/// Threading: demux operations are not thread-safe. Only one caller should read
/// packets or issue seeks at a time. In the standard pipeline the demux loop is
/// the sole reader (ADR-0009).
/// </para>
/// </remarks>
public interface IDemuxSession : IAsyncDisposable
{
    /// <summary>
    /// Metadata describing the opened container and its streams.
    /// Available immediately after the session is opened and does not change
    /// over the lifetime of the session.
    /// </summary>
    MediaInfo MediaInfo { get; }

    /// <summary>
    /// Reads the next packet from the media container.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token to cancel the read. Per ADR-0013, cancellation of the caller's token
    /// aborts this operation only — it does not close or invalidate the session.
    /// </param>
    /// <returns>
    /// The next <see cref="DemuxPacket"/>, or <see langword="null"/> when the end of
    /// the stream has been reached (EOF). Each returned packet is a fresh allocation
    /// owned by the caller.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is cancelled. The session
    /// remains open and readable after cancellation.
    /// </exception>
    ValueTask<DemuxPacket?> ReadPacketAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeks the demux session to the specified position.
    /// After a successful seek the next call to <see cref="ReadPacketAsync"/> will
    /// return packets from a point at or before <paramref name="position"/>.
    /// </summary>
    /// <param name="position">
    /// Target position in media time. The implementation will seek to the nearest
    /// keyframe at or before this position.
    /// </param>
    /// <param name="cancellationToken">
    /// Token to cancel the seek operation. Per ADR-0013, cancellation aborts this
    /// operation only and does not invalidate the session.
    /// </param>
    ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a coherent snapshot of the session's observable state
    /// (ADR-0034). Default implementation returns
    /// <see cref="DemuxSessionDiagnosticsSnapshot.Empty"/>; concrete sessions
    /// override to surface real counters.
    /// </summary>
    DemuxSessionDiagnosticsSnapshot GetDiagnostics() => DemuxSessionDiagnosticsSnapshot.Empty;
}
