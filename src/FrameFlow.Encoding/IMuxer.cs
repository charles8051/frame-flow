// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Encoding;

/// <summary>
/// Multiplexes streams of <see cref="EncodedPacket"/>s into a container file
/// (ADR-0040). Separate from the encoder so encoded streams can be branched
/// (e.g. one encode broadcast to both an MP4 file and an HLS segmenter).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Add streams, then <see cref="StartAsync"/> (writes the
/// container header), then <see cref="WriteAsync"/> per packet, then
/// <see cref="CompleteAsync"/> (writes the trailer and closes the file). The
/// file is not a valid, seekable container until <see cref="CompleteAsync"/>
/// returns — for MP4 the trailer carries the <c>moov</c> index.
/// </para>
/// <para>
/// <b>Stream wiring.</b> <see cref="AddVideoStream"/> takes the (open)
/// encoder so the muxer can copy its codec parameters — including the H.264
/// SPS/PPS extradata that the MP4 <c>avcC</c> box requires. This is why the
/// encoder must have produced at least its first packet (opened) before the
/// stream is added. The <see cref="Mp4VideoWriter"/> terminal sequences this
/// for you.
/// </para>
/// <para>
/// <b>Disposal.</b> <see cref="IAsyncDisposable.DisposeAsync"/> releases the
/// native output context and closes the file even if
/// <see cref="CompleteAsync"/> was never reached (e.g. on an aborted clip),
/// preventing a leaked OS file handle. Disposal is idempotent.
/// </para>
/// </remarks>
public interface IMuxer : IAsyncDisposable
{
    /// <summary>
    /// Adds a video stream backed by the given (open) encoder and returns the
    /// new stream's index. Call before <see cref="StartAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The encoder is not open, or the muxer has already started.
    /// </exception>
    int AddVideoStream(IVideoEncoder encoder);

    /// <summary>
    /// Writes the container header. Call once after all streams are added and
    /// before the first <see cref="WriteAsync"/>.
    /// </summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes one encoded packet to the container, rescaling its timestamps
    /// from the encoder time base into the stream time base. The packet is
    /// consumed (its data is copied into the muxer); the caller still owns the
    /// <see cref="EncodedPacket"/> instance and should dispose it.
    /// </summary>
    ValueTask WriteAsync(EncodedPacket packet, CancellationToken ct = default);

    /// <summary>
    /// Writes the container trailer and closes the output file. Idempotent —
    /// a second call is a no-op. After this returns the file is complete.
    /// </summary>
    ValueTask CompleteAsync(CancellationToken ct = default);
}
