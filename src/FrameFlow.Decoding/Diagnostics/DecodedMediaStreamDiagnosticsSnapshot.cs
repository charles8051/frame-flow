// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Diagnostics;

/// <summary>
/// Aggregate diagnostics snapshot for an <see cref="IDecodedMediaStream"/>
/// (ADR-0036). Composes the demux, video-decoder, and audio-decoder
/// snapshots that already exist (ADR-0034), and adds the pull-channel
/// depths that are owned by the stream itself.
/// </summary>
/// <param name="Demux">Demuxer snapshot, or <see cref="DemuxSessionDiagnosticsSnapshot.Empty"/> when no session is loaded.</param>
/// <param name="VideoDecoder">Video decoder snapshot, or <see cref="VideoDecoderDiagnosticsSnapshot.Empty"/> when the load has no video stream.</param>
/// <param name="AudioDecoder">Audio decoder snapshot, or <see cref="AudioDecoderDiagnosticsSnapshot.Empty"/> when the load has no audio stream.</param>
/// <param name="VideoChannelDepth">Number of video frames buffered in the pull-shape video channel.</param>
/// <param name="AudioChannelDepth">Number of audio buffers buffered in the pull-shape audio channel.</param>
public sealed record DecodedMediaStreamDiagnosticsSnapshot(
    DemuxSessionDiagnosticsSnapshot Demux,
    VideoDecoderDiagnosticsSnapshot VideoDecoder,
    AudioDecoderDiagnosticsSnapshot AudioDecoder,
    int VideoChannelDepth,
    int AudioChannelDepth
)
{
    /// <summary>
    /// Empty snapshot used as the rollup seed when the stream has no
    /// loaded session.
    /// </summary>
    public static DecodedMediaStreamDiagnosticsSnapshot Empty { get; } =
        new(
            Demux: DemuxSessionDiagnosticsSnapshot.Empty,
            VideoDecoder: VideoDecoderDiagnosticsSnapshot.Empty,
            AudioDecoder: AudioDecoderDiagnosticsSnapshot.Empty,
            VideoChannelDepth: 0,
            AudioChannelDepth: 0
        );
}
