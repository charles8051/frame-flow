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
    /// <summary>At least one stream carries resolved codec parameters.</summary>
    Playable,

    /// <summary>The container declared neither an audio nor a video stream.</summary>
    NoStreams,

    /// <summary>
    /// Streams were declared and none of them resolved. FFmpeg opened the container but
    /// never worked out what is inside it.
    /// </summary>
    NoResolvedStreams,
}

/// <summary>
/// Pure classification of an opened container's stream metadata.
/// </summary>
/// <remarks>
/// <para>
/// <c>avformat_find_stream_info</c> reports "Could not find codec parameters" at warning
/// level and still returns a non-negative value, so a container FFmpeg opened but could not
/// resolve reaches <see cref="DemuxSessionFactory"/> looking successful, carrying streams
/// whose parameters are still zero. Left alone it becomes a session that decodes nothing and
/// ends at zero duration, which a caller cannot tell apart from a valid empty clip. An
/// animated WebP is the reproducible case (#340, #341).
/// </para>
/// <para>
/// Resolution is the only question asked here. Whether FrameFlow can build a decoder for a
/// codec it did resolve is a separate one, answered later and loudly by
/// <c>VideoDecoder.Open</c> / <c>AudioDecoder.Open</c>.
/// </para>
/// <para>
/// These are total functions over <see cref="MediaInfo"/>. They read no FFmpeg state, perform
/// no IO, and hold nothing across calls; the shell that owns the format context decides what
/// to do with the verdict.
/// </para>
/// </remarks>
internal static class SourceViability
{
    /// <summary>
    /// Classify what <paramref name="mediaInfo"/> offers. A video stream counts only with
    /// positive dimensions and an audio stream only with a positive sample rate and channel
    /// count, because unresolved codec parameters leave all four at zero.
    /// </summary>
    public static SourceViabilityKind Classify(MediaInfo mediaInfo)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);

        if (mediaInfo.VideoStreams.Count == 0 && mediaInfo.AudioStreams.Count == 0)
            return SourceViabilityKind.NoStreams;

        return mediaInfo.VideoStreams.Any(IsResolved) || mediaInfo.AudioStreams.Any(IsResolved)
            ? SourceViabilityKind.Playable
            : SourceViabilityKind.NoResolvedStreams;
    }

    /// <summary>
    /// Render every declared stream, as <c>"video webp 0x0"</c> or
    /// <c>"video h264 1920x1080, audio aac 44100Hz 2ch"</c>. Empty when nothing was declared.
    /// </summary>
    public static string DescribeStreams(MediaInfo mediaInfo) => Describe(mediaInfo, _ => true);

    /// <summary>
    /// Render only the declared streams that did not resolve. Empty when they all did.
    /// </summary>
    /// <remarks>
    /// An unsupported track inside an otherwise fine container is a real shape, and failing
    /// the whole load over it would be a regression. The rest plays; the caller gets a
    /// warning rather than silence about the track that will not.
    /// </remarks>
    public static string DescribeUnusableStreams(MediaInfo mediaInfo) =>
        Describe(mediaInfo, resolved => !resolved);

    private static string Describe(MediaInfo mediaInfo, Func<bool, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);

        var video = mediaInfo
            .VideoStreams.Where(v => keep(IsResolved(v)))
            .Select(v => $"video {v.CodecName} {v.Width}x{v.Height}");

        var audio = mediaInfo
            .AudioStreams.Where(a => keep(IsResolved(a)))
            .Select(a => $"audio {a.CodecName} {a.SampleRate}Hz {a.Channels}ch");

        return string.Join(", ", video.Concat(audio));
    }

    private static bool IsResolved(VideoStreamInfo video) => video.Width > 0 && video.Height > 0;

    private static bool IsResolved(AudioStreamInfo audio) =>
        audio.SampleRate > 0 && audio.Channels > 0;
}
