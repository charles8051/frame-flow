// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;

namespace FrameFlow.Media;

/// <summary>
/// What a source contains, read from its container when the source opens. A player reports it
/// for the current item.
/// </summary>
/// <param name="ContainerName">
/// The name FFmpeg's demuxer gives the container format, as ffprobe prints it. A demuxer that
/// reads several related formats gives them all, comma-separated: <c>mov,mp4,m4a,3gp,3g2,mj2</c>
/// or <c>matroska,webm</c>. <c>"unknown"</c> when the demuxer reports no name.
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
)
{
    /// <summary>
    /// The shape of the stream's pixels, from the container or, failing that, the codec. Square
    /// unless the source says otherwise. A frame can carry a different one: see
    /// <see cref="IVideoFrame.SampleAspectRatio"/>.
    /// </summary>
    public SampleAspectRatio SampleAspectRatio { get; init; } = SampleAspectRatio.Square;

    /// <summary>How far the stream's frames turn clockwise to display upright, from its display matrix.</summary>
    public VideoRotation Rotation { get; init; }

    /// <summary>The width to show the stream at: <see cref="Width"/> scaled by the pixel shape, then rotated.</summary>
    public int DisplayWidth => DisplaySize.Of(Width, Height, SampleAspectRatio, Rotation).Width;

    /// <summary>The height to show the stream at, after rotation.</summary>
    public int DisplayHeight => DisplaySize.Of(Width, Height, SampleAspectRatio, Rotation).Height;
}

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
