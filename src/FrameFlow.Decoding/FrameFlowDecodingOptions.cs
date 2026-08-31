// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>
/// Configures the decoder and demuxer factories registered by
/// <see cref="FrameFlowDecodingServiceCollectionExtensions.AddFrameFlowDecoding"/>.
/// All properties are optional — unset values fall back to the shipped defaults
/// (<see cref="DemuxSessionFactory"/> and <see cref="DecoderFactories.Video"/>
/// / <see cref="DecoderFactories.Audio"/>).
/// </summary>
/// <remarks>
/// Audio sink registration is no longer surfaced here (per ADR-0044). Register
/// an <c>IAudioSink</c> singleton directly via a backend-specific extension
/// (for example <c>AddFrameFlowOpenAlAudio</c>) or
/// <c>services.AddSingleton&lt;IAudioSink&gt;(yourSink)</c>.
/// </remarks>
public sealed class FrameFlowDecodingOptions
{
    /// <summary>
    /// Factory delegate that produces a video decoder for a given demux session,
    /// or <see langword="null"/> when the source has no video stream. Defaults
    /// to <see cref="DecoderFactories.Video"/>.
    /// </summary>
    public Func<IDemuxSession, IVideoDecoder?>? VideoDecoderFactory { get; set; }

    /// <summary>
    /// Factory delegate that produces an audio decoder for a given demux session,
    /// or <see langword="null"/> when the source has no audio stream. Defaults
    /// to <see cref="DecoderFactories.Audio"/>.
    /// </summary>
    public Func<IDemuxSession, IAudioDecoder?>? AudioDecoderFactory { get; set; }
}
