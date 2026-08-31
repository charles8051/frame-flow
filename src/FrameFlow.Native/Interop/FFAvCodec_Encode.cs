// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Encode-direction additions to the <c>libavcodec</c> P/Invoke surface
/// (ADR-0040): encoder discovery, the send-frame / receive-packet encode
/// loop, codec-parameters export, and packet timestamp rescaling.
/// </summary>
/// <remarks>
/// <para>
/// This is a partial class that extends <see cref="FFAvCodec"/> declared in
/// <c>FFAvCodec.cs</c>. The decode direction (<c>avcodec_send_packet</c> /
/// <c>avcodec_receive_frame</c>) lives there; the write direction lives here
/// so the read/write surfaces stay auditable side by side. Targets FFmpeg 7.x
/// (libavcodec-61).
/// </para>
/// <para>
/// All pointer parameters are <see cref="nint"/>; raw pointer values must not
/// escape outside <c>FrameFlow.Native</c> and the encode layer
/// (<c>FrameFlow.Encoding</c>) per ADR-0005.
/// </para>
/// </remarks>
internal static partial class FFAvCodec
{
    /// <summary>
    /// Finds a registered encoder by name (e.g. <c>"libopenh264"</c>,
    /// <c>"h264_nvenc"</c>). Returns the <c>AVCodec*</c> on success, or
    /// <see cref="nint.Zero"/> when no encoder with that name is compiled into
    /// the loaded build. The returned pointer is owned by FFmpeg and must not
    /// be freed.
    /// </summary>
    /// <remarks>
    /// Name-based lookup is deterministic, unlike
    /// <c>avcodec_find_encoder(AV_CODEC_ID_H264)</c> which returns whichever
    /// H.264 encoder happens to be registered first. The LGPL FFmpeg build
    /// FrameFlow ships statically links Cisco OpenH264 (<c>libopenh264</c>),
    /// the software default used by the encoder terminal.
    /// </remarks>
    [LibraryImport("avcodec", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint avcodec_find_encoder_by_name(string name);

    /// <summary>
    /// Supplies a raw uncompressed <c>AVFrame</c> to the encoder. Pass
    /// <see cref="nint.Zero"/> as <paramref name="frame"/> to signal
    /// end-of-stream and begin the flush drain.
    /// </summary>
    /// <returns>
    /// 0 on success; AVERROR(EAGAIN) when the encoder's output must be drained
    /// via <see cref="avcodec_receive_packet"/> before more input is accepted;
    /// AVERROR_EOF if the encoder has already been flushed; other negative
    /// codes on error.
    /// </returns>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avcodec_send_frame(nint ctx, nint frame);

    /// <summary>
    /// Retrieves an encoded <c>AVPacket</c> from the encoder into
    /// <paramref name="pkt"/>.
    /// </summary>
    /// <returns>
    /// 0 on success; AVERROR(EAGAIN) when more input is needed before a packet
    /// is available; AVERROR_EOF once the encoder is fully drained after a
    /// flush; other negative codes on error.
    /// </returns>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avcodec_receive_packet(nint ctx, nint pkt);

    /// <summary>
    /// Copies the parameters of an (opened) codec context into an
    /// <c>AVCodecParameters</c> — the inverse of
    /// <see cref="avcodec_parameters_to_context"/>. Used to populate a muxer
    /// stream's <c>codecpar</c> from the encoder, including the
    /// <c>extradata</c> (SPS/PPS) that the MP4 <c>avcC</c> box requires.
    /// </summary>
    /// <returns>0 on success or a negative AVERROR code.</returns>
    /// <remarks>
    /// Must be called <b>after</b> <see cref="avcodec_open2"/> so the encoder
    /// has populated <c>extradata</c> (the global-header SPS/PPS), and before
    /// <c>avformat_write_header</c>. Despite operating on a format-layer
    /// parameters struct, this function lives in <c>libavcodec</c>.
    /// </remarks>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avcodec_parameters_from_context(nint par, nint ctx);

    /// <summary>
    /// Allocates a fresh reference-counted payload buffer of
    /// <paramref name="size"/> bytes on the packet and resets its fields.
    /// Used by the muxer to build a writable <c>AVPacket</c> from an
    /// <see cref="FrameFlow.Encoding.EncodedPacket"/>'s managed bytes.
    /// <c>av_packet_unref</c> the packet first when reusing it.
    /// </summary>
    /// <returns>0 on success or a negative AVERROR code.</returns>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_new_packet(nint pkt, int size);

    /// <summary>
    /// <c>AV_CODEC_FLAG_GLOBAL_HEADER</c> — instructs the encoder to place the
    /// stream's global headers (SPS/PPS for H.264) in <c>extradata</c> instead
    /// of in every keyframe packet. Required for MP4: without it the container
    /// cannot write a valid <c>avcC</c> decoder-configuration box and the file
    /// is not spec-compliant.
    /// </summary>
    internal const int AvCodecFlagGlobalHeader = 1 << 22; // 0x00400000
}
