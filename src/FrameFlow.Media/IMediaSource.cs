// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

public interface IMediaSource
{
    string DisplayName { get; }

    /// <summary>
    /// The URI identifying the media resource, or <see langword="null"/> for sources
    /// that are not URI-addressable (e.g. in-memory streams).
    /// </summary>
    Uri? Uri { get; }

    /// <summary>
    /// The local file path when the source is a file, or <see langword="null"/> for non-file sources.
    /// </summary>
    string? FilePath { get; }

    bool IsSeekable { get; }

    /// <summary>
    /// Options handed to the demuxer when the source is opened, or <see langword="null"/>
    /// to open it with none. Keys and values are FFmpeg's own, as they would be written on
    /// an <c>ffmpeg</c> command line before the input: <c>framerate</c>, <c>loop</c>,
    /// <c>rtsp_transport</c>, and so on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set is not filtered. Which options exist depends on the demuxer that opens the
    /// source, so <see cref="InputFormat"/> is usually set alongside this: an option the
    /// demuxer does not recognise is an error rather than a silent no-op, and which demuxer
    /// is chosen decides what "recognised" means.
    /// </para>
    /// <para>
    /// Comparison is FFmpeg's, not the dictionary's, so a dictionary with two keys differing
    /// only in case is ambiguous. Build it with
    /// <see cref="StringComparer.Ordinal"/>.
    /// </para>
    /// </remarks>
    IReadOnlyDictionary<string, string>? DemuxerOptions => null;

    /// <summary>
    /// The short name of the demuxer to open the source with (<c>image2</c>, <c>mp4</c>,
    /// <c>concat</c>, ...), or <see langword="null"/> to let FFmpeg probe for one. The
    /// equivalent of <c>-f</c> before an <c>ffmpeg</c> input.
    /// </summary>
    /// <remarks>
    /// Probing is right for ordinary media and wrong when the probe's answer and the
    /// caller's intent differ. A single still image is the case that motivated this: it
    /// probes to a <c>*_pipe</c> demuxer, which reports no duration whatever options it is
    /// given, while <c>image2</c> takes a <c>framerate</c> and reports the still as a clip
    /// of a known length.
    /// </remarks>
    string? InputFormat => null;
}
