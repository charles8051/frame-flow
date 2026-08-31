// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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

        // Resolve the URL string FFmpeg will open.
        string url = ResolveUrl(source);

        _logger.LogInformation(
            "Opening media source {DisplayName} (url={Url})",
            source.DisplayName,
            url
        );

        // avformat_open_input returns a context on success or fails and leaves ctx at Zero.
        nint ctx = nint.Zero;
        int openResult = FFAvFormat.avformat_open_input(ref ctx, url, nint.Zero, nint.Zero);

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
        // av_packet_alloc is in libavcodec in FFmpeg 7.x (not libavutil).
        nint packet = FFAvCodec.av_packet_alloc();
        if (packet == nint.Zero)
        {
            formatCtx.Dispose();
            throw new OutOfMemoryException("FFmpeg av_packet_alloc returned null.");
        }

        // Build managed metadata from the open context.
        MediaInfo mediaInfo;
        try
        {
            mediaInfo = DemuxSession.BuildMediaInfo(formatCtx.DangerousGetHandle());
        }
        catch
        {
            var tempPkt = packet;
            FFAvCodec.av_packet_free(ref tempPkt);
            formatCtx.Dispose();
            throw;
        }

        _logger.LogInformation(
            "Opened media source {DisplayName} with {VideoStreamCount} video and {AudioStreamCount} audio streams",
            source.DisplayName,
            mediaInfo.VideoStreams.Count,
            mediaInfo.AudioStreams.Count
        );

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
