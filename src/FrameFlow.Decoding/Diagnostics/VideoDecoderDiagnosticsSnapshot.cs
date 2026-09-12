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
/// The hardware-decode backend that is <b>decoding</b>, or <see langword="null"/>
/// when running software-only (ADR-0033).
/// <para>
/// Not the same as the backend that was bound. <c>avcodec_open2</c> succeeding
/// proves only that the device opened; FFmpeg decides whether the hwaccel can
/// handle a given stream later, in <c>get_format</c> on the first decoded frame,
/// and falls back to software by returning a software pixel format. A Vulkan
/// device without <c>VK_KHR_video_decode_queue</c> opens and cannot decode. So
/// this is set when the decoder binds a backend and cleared on the first frame if
/// that backend did not produce it, which is the only point the answer is known.
/// </para>
/// <para>
/// Tracked against every decoded frame, not fixed on the first. FFmpeg calls
/// <c>get_format</c> again when a stream changes coded format or dimensions and can
/// choose differently, so a value settled once goes stale in both directions. In
/// practice it changes only when the negotiated mode does, since it is derived from
/// the frame's pixel format.
/// </para>
/// <para>
/// Written through a volatile field and read in one load, so it is safe to read from
/// another thread without further synchronization.
/// </para>
/// </param>
/// <param name="PacketsDroppedForBackpressure">
/// Cumulative count of raw video packets <c>SendPacketAsync</c> shed
/// (drop-newest) because the decoder's bounded queue was full and
/// blocking the demux pump would have wedged the audio chain. Healthy
/// pipelines stay at zero; non-zero usually correlates with a visible
/// "video pauses on the last good frame for a beat" artifact. Audio
/// is unaffected. See <c>VideoDecoder.SendPacketAsync</c>'s xmldoc.
/// </param>
/// <param name="PacketsDroppedToGopResync">
/// Cumulative count of packets shed because an earlier drop had already
/// broken the reference chain and no keyframe had arrived since (#134).
/// These are not the video chain falling further behind; they are the
/// picture given up to avoid decoding frames whose references are gone.
/// Read it against <paramref name="PacketsDroppedForBackpressure"/>: that
/// one says how far behind the chain fell, this one says what it cost.
/// See <c>GopShedGate</c>.
/// </param>
public sealed record VideoDecoderDiagnosticsSnapshot(
    long FramesDecoded,
    long DecodeErrors,
    HardwareDecodeBackendKind? HardwareBackend,
    long PacketsDroppedForBackpressure = 0,
    long PacketsDroppedToGopResync = 0
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
            PacketsDroppedForBackpressure: 0,
            PacketsDroppedToGopResync: 0
        );
}
