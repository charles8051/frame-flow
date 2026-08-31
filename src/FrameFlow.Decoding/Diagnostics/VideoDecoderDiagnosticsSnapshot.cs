// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding.Diagnostics;

/// <summary>
/// Immutable snapshot of an <see cref="IVideoDecoder"/>'s observable state at
/// a single point in time (ADR-0034).
/// </summary>
/// <param name="FramesDecoded">
/// Cumulative count of decoded frames yielded by the decoder. For
/// hwaccel-backed decoders this counts frames after the GPU→CPU transfer
/// (ADR-0033), so the value matches the number of <c>CpuVideoFrame</c>
/// instances handed downstream.
/// </param>
/// <param name="DecodeErrors">
/// Number of non-fatal decode errors (corrupt packets, transient
/// <c>av_hwframe_transfer_data</c> failures, etc.) that resulted in a
/// dropped frame but did not terminate the decode loop.
/// </param>
/// <param name="HardwareBackend">
/// The hardware-decode backend currently bound to this decoder, or
/// <see langword="null"/> when running software-only (ADR-0033). Set once
/// when the decoder opens and immutable thereafter — safe to read without
/// synchronization.
/// </param>
/// <param name="PacketsDroppedForBackpressure">
/// Cumulative count of raw video packets <c>SendPacketAsync</c> shed
/// (drop-newest) because the decoder's bounded queue was full and
/// blocking the demux pump would have wedged the audio chain. Healthy
/// pipelines stay at zero; non-zero usually correlates with a visible
/// "video pauses on the last good frame for a beat" artifact. Audio
/// is unaffected. See <c>VideoDecoder.SendPacketAsync</c>'s xmldoc.
/// </param>
public sealed record VideoDecoderDiagnosticsSnapshot(
    long FramesDecoded,
    long DecodeErrors,
    HardwareDecodeBackendKind? HardwareBackend,
    long PacketsDroppedForBackpressure = 0
)
{
    /// <summary>
    /// Zero-valued snapshot used as the seed value for rollups when no
    /// video decoder is active.
    /// </summary>
    public static VideoDecoderDiagnosticsSnapshot Empty { get; } =
        new(
            FramesDecoded: 0,
            DecodeErrors: 0,
            HardwareBackend: null,
            PacketsDroppedForBackpressure: 0
        );
}
