// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// What a source contains, read from its container when the source opens. A player reports it
/// for the current item.
/// </summary>
/// <param name="ContainerName">
/// The container format's name. A demuxed source reports <c>"unknown"</c> for now, because the
/// demuxer does not read the format name yet (#465).
/// </param>
/// <param name="Duration">
/// The container's duration, or <see cref="TimeSpan.Zero"/> when it reports none, as a live
/// stream does.
/// </param>
/// <param name="VideoStreams">The video streams, in container order.</param>
/// <param name="AudioStreams">
/// The audio streams, in container order. Other stream types, such as subtitles, are not listed.
/// </param>
public sealed record MediaInfo(
    string ContainerName,
    TimeSpan Duration,
    IReadOnlyList<VideoStreamInfo> VideoStreams,
    IReadOnlyList<AudioStreamInfo> AudioStreams
);

/// <summary>One video stream in a source's container.</summary>
/// <param name="StreamIndex">The stream's index in the container, as FFmpeg numbers them.</param>
/// <param name="CodecName">FFmpeg's name for the stream's codec, such as <c>h264</c> or <c>hevc</c>.</param>
/// <param name="Width">
/// The coded width in pixels, before any sample aspect ratio or rotation is applied.
/// </param>
/// <param name="Height">
/// The coded height in pixels, before any sample aspect ratio or rotation is applied.
/// </param>
/// <param name="FrameRate">
/// The container's average frame rate in frames per second, or 0 when it reports none.
/// </param>
public sealed record VideoStreamInfo(
    int StreamIndex,
    string CodecName,
    int Width,
    int Height,
    double FrameRate
);

/// <summary>One audio stream in a source's container.</summary>
/// <param name="StreamIndex">The stream's index in the container, as FFmpeg numbers them.</param>
/// <param name="CodecName">FFmpeg's name for the stream's codec, such as <c>aac</c> or <c>opus</c>.</param>
/// <param name="SampleRate">The sample rate in hertz.</param>
/// <param name="Channels">The number of channels in the stream's channel layout.</param>
public sealed record AudioStreamInfo(
    int StreamIndex,
    string CodecName,
    int SampleRate,
    int Channels
);
