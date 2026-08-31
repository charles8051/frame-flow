// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Decoding;

/// <summary>
/// Provides standard factory delegates for creating video and audio decoders
/// from a <see cref="DemuxSession"/>. Returns <see langword="null"/> when the
/// media source does not contain the requested stream type, allowing the
/// playback session to gracefully skip video-only or audio-only media.
/// </summary>
public static class DecoderFactories
{
    /// <summary>
    /// Software-only video decoder factory. Equivalent to calling
    /// <see cref="CreateVideo"/> with
    /// <see cref="HardwareDecodeMode.Disabled"/>; preserved for legacy
    /// call-sites and tests.
    /// </summary>
    public static Func<IDemuxSession, IVideoDecoder?> Video =>
        session =>
        {
            if (session is not DemuxSession demux)
                throw new InvalidOperationException(
                    $"VideoDecoder requires a {nameof(DemuxSession)} instance. "
                        + $"Got {session.GetType().Name} instead."
                );

            var videoStream = demux.MediaInfo.VideoStreams.FirstOrDefault();
            if (videoStream is null)
                return null;

            return VideoDecoder.Open(demux.FormatContextPtr, videoStream.StreamIndex);
        };

    /// <summary>
    /// Creates a video-decoder factory delegate that applies the supplied
    /// hardware-decode policy (ADR-0033). Used by the DI registration to
    /// thread <see cref="HardwareDecodeOptions"/> and
    /// <see cref="HardwareDecodeCapabilities"/> into per-load decoder
    /// construction without exposing them on
    /// <see cref="Func{IDemuxSession, IVideoDecoder}"/> itself.
    /// </summary>
    /// <param name="options">Policy controlling whether/which hwaccel to use.</param>
    /// <param name="capabilities">
    /// The capability set computed at bootstrap. Pass
    /// <see cref="HardwareDecodeCapabilities.Empty"/> to force software fallback.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory. When provided, the decoder logs hwaccel
    /// selection / fallback events at <c>Information</c> and <c>Warning</c>
    /// levels.
    /// </param>
    /// <param name="videoOptions">
    /// Optional decoder configuration (e.g.
    /// <see cref="VideoDecoderOptions.PacketQueueCapacity"/>). When null the defaults
    /// from <see cref="VideoDecoderOptions"/> are used.
    /// </param>
    public static Func<IDemuxSession, IVideoDecoder?> CreateVideo(
        HardwareDecodeOptions options,
        HardwareDecodeCapabilities capabilities,
        ILoggerFactory? loggerFactory,
        VideoDecoderOptions? videoOptions = null
    ) =>
        session =>
        {
            if (session is not DemuxSession demux)
                throw new InvalidOperationException(
                    $"VideoDecoder requires a {nameof(DemuxSession)} instance. "
                        + $"Got {session.GetType().Name} instead."
                );

            var videoStream = demux.MediaInfo.VideoStreams.FirstOrDefault();
            if (videoStream is null)
                return null;

            return VideoDecoder.Open(
                demux.FormatContextPtr,
                videoStream.StreamIndex,
                options,
                capabilities,
                loggerFactory,
                videoOptions
            );
        };

    /// <summary>
    /// Creates a factory delegate that opens an <see cref="AudioDecoder"/> for the first
    /// audio stream found in the demux session, or returns <see langword="null"/> if no
    /// audio stream is present.
    /// </summary>
    public static Func<IDemuxSession, IAudioDecoder?> Audio =>
        session =>
        {
            if (session is not DemuxSession demux)
                throw new InvalidOperationException(
                    $"AudioDecoder requires a {nameof(DemuxSession)} instance. "
                        + $"Got {session.GetType().Name} instead."
                );

            var audioStream = demux.MediaInfo.AudioStreams.FirstOrDefault();
            if (audioStream is null)
                return null;

            return new AudioDecoder(demux.FormatContextPtr, audioStream.StreamIndex);
        };

    /// <summary>
    /// Creates an audio-decoder factory delegate that threads an
    /// <see cref="ILoggerFactory"/> into <see cref="AudioDecoder"/>
    /// construction. Use this overload when the call site has a logger
    /// factory available; falls back to <see cref="Audio"/>'s
    /// no-logger behaviour otherwise.
    /// </summary>
    public static Func<IDemuxSession, IAudioDecoder?> CreateAudio(ILoggerFactory? loggerFactory) =>
        session =>
        {
            if (session is not DemuxSession demux)
                throw new InvalidOperationException(
                    $"AudioDecoder requires a {nameof(DemuxSession)} instance. "
                        + $"Got {session.GetType().Name} instead."
                );

            var audioStream = demux.MediaInfo.AudioStreams.FirstOrDefault();
            if (audioStream is null)
                return null;

            var logger = loggerFactory?.CreateLogger<AudioDecoder>();
            return new AudioDecoder(
                demux.FormatContextPtr,
                audioStream.StreamIndex,
                options: null,
                logger: logger
            );
        };
}
