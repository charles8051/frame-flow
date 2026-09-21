// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding;

/// <summary>
/// What a just-opened container offers a playback session, as classified by
/// <see cref="SourceViability.Classify"/>.
/// </summary>
internal enum SourceViabilityKind
{
    /// <summary>
    /// At least one audio stream, or at least one video stream with resolved
    /// dimensions. The session has something to decode.
    /// </summary>
    Playable,

    /// <summary>
    /// The container declared neither an audio nor a video stream.
    /// </summary>
    NoStreams,

    /// <summary>
    /// Every declared video stream reported non-positive dimensions and there is no
    /// audio to fall back on. FFmpeg opened the container but never resolved codec
    /// parameters for anything in it.
    /// </summary>
    NoResolvedVideo,
}

/// <summary>
/// Pure classification of an opened container's stream metadata.
/// </summary>
/// <remarks>
/// <para>
/// <c>avformat_find_stream_info</c> reports "Could not find codec parameters" at warning
/// level and still returns a non-negative value, so a container FFmpeg opened but could
/// not resolve reaches <see cref="DemuxSessionFactory"/> looking successful, carrying a
/// video stream at <c>0x0</c>. Left alone it becomes a session that decodes nothing and
/// ends at zero duration, which a caller cannot tell apart from a valid empty clip. An
/// animated WebP is the reproducible case (#340, #341).
/// </para>
/// <para>
/// These are total functions over <see cref="MediaInfo"/>. They read no FFmpeg state,
/// perform no IO, and hold nothing across calls; the shell that owns the format context
/// decides what to do with the verdict.
/// </para>
/// </remarks>
internal static class SourceViability
{
    /// <summary>
    /// Classify what <paramref name="mediaInfo"/> offers.
    /// </summary>
    /// <remarks>
    /// Audio alone is enough. A video stream counts only when both dimensions are
    /// positive, because unresolved codec parameters leave them at zero.
    /// </remarks>
    public static SourceViabilityKind Classify(MediaInfo mediaInfo)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);

        if (mediaInfo.AudioStreams.Count > 0)
            return SourceViabilityKind.Playable;

        if (mediaInfo.VideoStreams.Count == 0)
            return SourceViabilityKind.NoStreams;

        return mediaInfo.VideoStreams.Any(IsResolved)
            ? SourceViabilityKind.Playable
            : SourceViabilityKind.NoResolvedVideo;
    }

    /// <summary>
    /// Whether a video stream was declared but left without usable dimensions while the
    /// source is still playable on another stream.
    /// </summary>
    /// <remarks>
    /// An unsupported video track inside an otherwise fine container is a real shape, and
    /// failing the whole load over it would be a regression. The audio plays; the caller
    /// gets a warning rather than silence about the video that will not.
    /// </remarks>
    public static bool HasUnusableVideo(MediaInfo mediaInfo)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);

        return mediaInfo.VideoStreams.Any(v => !IsResolved(v));
    }

    /// <summary>
    /// Render the declared video streams for a diagnostic message, as
    /// <c>"webp 0x0"</c> or <c>"h264 1920x1080, webp 0x0"</c>. Empty string when no
    /// video stream was declared.
    /// </summary>
    public static string DescribeVideoStreams(MediaInfo mediaInfo)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);

        return string.Join(
            ", ",
            mediaInfo.VideoStreams.Select(v => $"{v.CodecName} {v.Width}x{v.Height}")
        );
    }

    private static bool IsResolved(VideoStreamInfo video) => video.Width > 0 && video.Height > 0;
}
