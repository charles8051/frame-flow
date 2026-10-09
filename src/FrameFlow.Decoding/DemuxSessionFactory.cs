// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Core;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding;

/// <summary>
/// Production implementation of <see cref="IDemuxSessionFactory"/> that opens media
/// sources using FFmpeg's <c>avformat_open_input</c> and <c>avformat_find_stream_info</c>.
/// </summary>
/// <remarks>
/// <para>
/// This factory is the only place that calls <c>avformat_open_input</c>. On success
/// it transfers ownership of the resulting <c>AVFormatContext*</c> to a new
/// <see cref="DemuxSession"/> wrapped in a <see cref="FormatContextHandle"/>; the
/// factory itself does not retain any native resources after <see cref="OpenAsync"/>
/// returns (ADR-0005).
/// </para>
/// <para>
/// The factory is stateless and may be registered as a singleton in a DI container.
/// </para>
/// </remarks>
public sealed class DemuxSessionFactory : IDemuxSessionFactory
{
    private readonly ILogger<DemuxSessionFactory> _logger;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Initializes a new <see cref="DemuxSessionFactory"/> with a single
    /// logger. DemuxSession instances produced by <see cref="OpenAsync"/>
    /// will receive <see cref="NullLogger.Instance"/> because no factory
    /// is available to mint their per-type logger — prefer the
    /// <see cref="DemuxSessionFactory(ILoggerFactory)"/> overload so
    /// demuxer-side diagnostics aren't silently swallowed.
    /// </summary>
    /// <param name="logger">
    /// Optional logger. When <see langword="null"/>, logging is disabled via
    /// <see cref="NullLogger{T}"/>.
    /// </param>
    public DemuxSessionFactory(ILogger<DemuxSessionFactory>? logger = null)
    {
        _logger = logger ?? NullLogger<DemuxSessionFactory>.Instance;
        _loggerFactory = null;
    }

    /// <summary>
    /// Initializes a new <see cref="DemuxSessionFactory"/> that threads
    /// an <see cref="ILoggerFactory"/> through to each
    /// <see cref="DemuxSession"/> it produces. This is the preferred
    /// constructor — without it, <c>DemuxSession</c>'s own diagnostics
    /// (per-packet read errors, seek failures) fall through to
    /// <see cref="NullLogger.Instance"/> and disappear silently. Same
    /// asymmetry the <c>DecoderFactories.CreateAudio(ILoggerFactory)</c>
    /// overload (commit <c>d03e4b0</c>) closed for <c>AudioDecoder</c>.
    /// </summary>
    public DemuxSessionFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DemuxSessionFactory>();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The open sequence is:
    /// <list type="number">
    ///   <item><c>avformat_open_input</c> — opens the container and reads the header.</item>
    ///   <item><c>avformat_find_stream_info</c> — probes stream metadata.</item>
    ///   <item>Packet buffer allocation via <c>av_packet_alloc</c>.</item>
    ///   <item><see cref="DemuxSession.BuildMediaInfo"/> — reads stream metadata into managed types.</item>
    /// </list>
    /// If any step fails, all allocated native resources are freed before the exception propagates.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="source"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when FFmpeg cannot open the source (e.g. file not found, unsupported format)
    /// or when stream info probing fails.
    /// </exception>
    public ValueTask<IDemuxSession> OpenAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return OpenCore(source, forcedVideoCodec: null);
        }
        catch (ReopenWithCodecException reopen)
        {
            return OpenCore(source, reopen.CodecId);
        }
    }

    /// <param name="source">The source to open.</param>
    /// <param name="forcedVideoCodec">
    /// An <c>AVCodecID</c> to set as the format context's <c>video_codec_id</c> before the open,
    /// which <c>image2</c> uses as the decoder where it cannot name one from the extension (#575).
    /// <see langword="null"/> for none.
    /// </param>
    private ValueTask<IDemuxSession> OpenCore(IMediaSource source, int? forcedVideoCodec)
    {
        // Resolve the URL string FFmpeg will open.
        string url = ResolveUrl(source);

        _logger.LogInformation(
            "Opening media source {DisplayName} (url={Url}, format={InputFormat})",
            source.DisplayName,
            url,
            source.InputFormat ?? "probe"
        );

        // Forcing the demuxer rather than probing for one. A wrong name here is the caller's
        // typo, and failing on it beats quietly probing instead: the options below are often
        // specific to the demuxer this names, and would then all come back unrecognised.
        nint inputFormat = ResolveInputFormat(source);

        // avformat_open_input returns a context on success or fails and leaves ctx at Zero.
        nint ctx = nint.Zero;
        nint options = BuildDemuxerOptions(source);
        int openResult;
        try
        {
            if (forcedVideoCodec is { } codecId)
            {
                // avformat_open_input takes this context, and frees it if the open fails.
                ctx = FFAvFormat.avformat_alloc_context();
                if (ctx == nint.Zero)
                    throw new OutOfMemoryException("FFmpeg avformat_alloc_context returned null.");
                unsafe
                {
                    Unsafe.AsRef<AVFormatContext>((void*)ctx).video_codec_id = (AVCodecID)codecId;
                }
            }

            openResult = FFAvFormat.avformat_open_input(ref ctx, url, inputFormat, ref options);

            if (openResult >= 0 && ctx != nint.Zero)
            {
                // What comes back is what the demuxer did not consume. Reported rather than
                // dropped: an option that named nothing did not do what the caller asked, and
                // the open otherwise succeeds and hides it.
                var unrecognised = ReadKeys(options);
                if (unrecognised.Count > 0)
                {
                    FFAvFormat.avformat_close_input(ref ctx);
                    throw new InvalidOperationException(
                        $"The demuxer opening media source {source.DisplayName} did not "
                            + $"recognise these options: {string.Join(", ", unrecognised)}. "
                            + "Demuxer options belong to the demuxer that opens the source, "
                            + "so set IMediaSource.InputFormat to choose it explicitly."
                    );
                }
            }
        }
        finally
        {
            // The dictionary is ours on every path, including the failure ones, and holds
            // whatever avformat_open_input left in it.
            FFAvUtil.av_dict_free(ref options);
        }

        if (openResult < 0 || ctx == nint.Zero)
        {
            _logger.LogError(
                "Failed to open media source {DisplayName} (url={Url}, error code {ErrorCode})",
                source.DisplayName,
                url,
                openResult
            );

            // Ensure no partial context leaks (avformat_open_input frees on failure).
            throw new InvalidOperationException(
                $"FFmpeg could not open media source '{source.DisplayName}' "
                    + $"(url='{url}', error code {openResult})."
            );
        }

        // Wrap the context immediately so it is freed on any subsequent failure.
        var formatCtx = new FormatContextHandle(ctx);

        int findResult;
        try
        {
            findResult = FFAvFormat.avformat_find_stream_info(
                formatCtx.DangerousGetHandle(),
                nint.Zero
            );
        }
        catch
        {
            formatCtx.Dispose();
            throw;
        }

        if (findResult < 0)
        {
            _logger.LogError(
                "Failed to find stream info for {DisplayName} (error code {ErrorCode})",
                source.DisplayName,
                findResult
            );

            formatCtx.Dispose();
            throw new InvalidOperationException(
                $"FFmpeg could not find stream info for '{source.DisplayName}' "
                    + $"(error code {findResult})."
            );
        }

        // Allocate the reusable packet buffer.
        // av_packet_alloc is in libavcodec, not libavutil.
        nint packet = FFAvCodec.av_packet_alloc();
        if (packet == nint.Zero)
        {
            formatCtx.Dispose();
            throw new OutOfMemoryException("FFmpeg av_packet_alloc returned null.");
        }

        // Build managed metadata from the open context, decide whether the container is
        // worth a session, and say so. Everything up to the ownership transfer below runs
        // inside this handler: until then the packet and the format context are still the
        // factory's to free, and that includes an exception raised by a logger.
        MediaInfo mediaInfo;
        try
        {
            mediaInfo = DemuxSession.BuildMediaInfo(formatCtx.DangerousGetHandle());

            // avformat_find_stream_info returns success even when it could not resolve codec
            // parameters, so an unplayable container arrives here looking fine and would go
            // on to decode nothing and end at zero duration (#340). Refuse it while the
            // failure still has a name.
            var viability = SourceViability.Classify(mediaInfo);
            if (viability != SourceViabilityKind.Playable)
            {
                string detail = viability switch
                {
                    SourceViabilityKind.NoStreams => "it declares no audio or video stream",
                    SourceViabilityKind.NoResolvedStreams =>
                        "no stream has resolved codec parameters "
                        + $"({SourceViability.DescribeStreams(mediaInfo)})",
                    _ => "it offers no playable stream",
                };

                // image2 names a bare image's decoder from its extension and found none (#575).
                // The file's own first bytes can name it, and the open is tried once more with it.
                if (forcedVideoCodec is null && StillCodecFor(source) is { } sniffed)
                {
                    _logger.LogInformation(
                        "Media source {DisplayName} opened on image2 without a decoder for its "
                            + "format; reopening with codec {CodecId} named",
                        source.DisplayName,
                        sniffed
                    );
                    throw new ReopenWithCodecException(sniffed);
                }

                _logger.LogError(
                    "Opened media source {DisplayName} but resolved no playable stream: {Detail}",
                    source.DisplayName,
                    detail
                );

                // image2 reads one bare image. A container image format has no decoder it can name.
                string hint =
                    source.InputFormat == "image2"
                        ? " image2 reads one bare image and cannot open a container image format "
                            + "such as AVIF or ICO; open those with MediaSource.FromFile."
                        : "";

                throw new InvalidOperationException(
                    $"FFmpeg opened media source '{source.DisplayName}' but resolved no playable "
                        + $"stream: {detail}.{hint}"
                );
            }

            // Playable on another stream, but a declared track is still unusable. The load
            // stands; the track that will not appear is said out loud.
            if (SourceViability.DescribeUnusableStreams(mediaInfo) is { Length: > 0 } unusable)
            {
                _logger.LogWarning(
                    "Media source {DisplayName} declares a stream with unresolved codec "
                        + "parameters; it will not be decoded ({Streams})",
                    source.DisplayName,
                    unusable
                );
            }

            _logger.LogInformation(
                "Opened media source {DisplayName} with {VideoStreamCount} video and {AudioStreamCount} audio streams",
                source.DisplayName,
                mediaInfo.VideoStreams.Count,
                mediaInfo.AudioStreams.Count
            );
        }
        catch
        {
            var tempPkt = packet;
            FFAvCodec.av_packet_free(ref tempPkt);
            formatCtx.Dispose();
            throw;
        }

        // Ownership of formatCtx and packet transfers to the new DemuxSession.
        // The session is returned to the caller who is then responsible for disposal.
        // CA2000 is a false positive here because the object's lifetime extends beyond this method.
#pragma warning disable CA2000
        IDemuxSession session = new DemuxSession(
            formatCtx,
            packet,
            mediaInfo,
            _loggerFactory?.CreateLogger<DemuxSession>()
        );
#pragma warning restore CA2000
        return ValueTask.FromResult(session);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// The decoder the first bytes of an <c>image2</c> source's file name, or null when the
    /// source is not one, is not a file, cannot be read, or is a format this does not know.
    /// </summary>
    private static int? StillCodecFor(IMediaSource source)
    {
        if (source.InputFormat != "image2" || source.FilePath is not { } path)
            return null;

        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[StillCodec.HeaderLength];
            int read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return StillCodec.ForHeader(header[..read]);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Carries the decoder to open again with, out of <see cref="OpenCore"/>'s cleanup.</summary>
    private sealed class ReopenWithCodecException(int codecId) : Exception
    {
        public int CodecId { get; } = codecId;
    }

    /// <summary>
    /// Looks up the demuxer named by <see cref="IMediaSource.InputFormat"/>, or
    /// <see cref="nint.Zero"/> to let <c>avformat_open_input</c> probe for one. The returned
    /// pointer is libavformat's own static storage and is not owned.
    /// </summary>
    private static nint ResolveInputFormat(IMediaSource source)
    {
        if (string.IsNullOrEmpty(source.InputFormat))
            return nint.Zero;

        nint format = FFAvFormat.av_find_input_format(source.InputFormat);
        if (format == nint.Zero)
        {
            throw new ArgumentException(
                $"No demuxer is named {source.InputFormat} "
                    + $"(media source {source.DisplayName}).",
                nameof(source)
            );
        }

        return format;
    }

    /// <summary>
    /// Builds the <c>AVDictionary*</c> carrying the source's demuxer options, or
    /// <see cref="nint.Zero"/> when it has none. The caller owns the result and frees it on
    /// every path.
    /// </summary>
    private static nint BuildDemuxerOptions(IMediaSource source)
    {
        var requested = source.DemuxerOptions;
        if (requested is null || requested.Count == 0)
            return nint.Zero;

        nint options = nint.Zero;
        try
        {
            foreach (var (key, value) in requested)
            {
                if (string.IsNullOrEmpty(key))
                {
                    throw new ArgumentException(
                        $"Media source {source.DisplayName} has a demuxer option with an "
                            + "empty key.",
                        nameof(source)
                    );
                }

                int rc = FFAvUtil.av_dict_set(ref options, key, value ?? string.Empty, 0);
                if (rc < 0)
                {
                    throw new InvalidOperationException(
                        $"Could not set demuxer option {key} for media source "
                            + $"{source.DisplayName} (error code {rc})."
                    );
                }
            }
        }
        catch
        {
            FFAvUtil.av_dict_free(ref options);
            throw;
        }

        return options;
    }

    /// <summary>
    /// Copies every key out of the dictionary. Reads only, and copies the strings into
    /// managed memory, so the entries stay FFmpeg's to free.
    /// </summary>
    private static List<string> ReadKeys(nint dictionary)
    {
        var keys = new List<string>();
        if (dictionary == nint.Zero)
            return keys;

        nint entry = nint.Zero;
        while (true)
        {
            entry = FFAvUtil.av_dict_get(
                dictionary,
                string.Empty,
                entry,
                FFAvUtil.AvDictIgnoreSuffix
            );
            if (entry == nint.Zero)
                break;

            string? key;
            unsafe
            {
                ref AVDictionaryEntry e = ref Unsafe.AsRef<AVDictionaryEntry>((void*)entry);
                key = Marshal.PtrToStringUTF8((nint)e.key);
            }

            if (!string.IsNullOrEmpty(key))
                keys.Add(key);
        }

        return keys;
    }

    /// <summary>
    /// Resolves the URL string to pass to <c>avformat_open_input</c>.
    /// Prefers <see cref="IMediaSource.FilePath"/> for local files so FFmpeg
    /// uses the local file protocol rather than parsing a file:// URI.
    /// </summary>
    private static string ResolveUrl(IMediaSource source)
    {
        if (!string.IsNullOrEmpty(source.FilePath))
            return source.FilePath;

        if (source.Uri is not null)
            return source.Uri.ToString();

        throw new ArgumentException(
            $"Media source '{source.DisplayName}' has neither a file path nor a URI. "
                + "Cannot determine the URL to open.",
            nameof(source)
        );
    }
}
