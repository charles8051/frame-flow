// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding;

/// <summary>
/// FFmpeg-backed implementation of <see cref="IDemuxSession"/> that opens a media
/// source, inspects its streams, reads packets, and supports seeking.
/// </summary>
/// <remarks>
/// <para>
/// Resource ownership (ADR-0005): this class owns one <c>AVFormatContext*</c> wrapped in a
/// <see cref="FormatContextHandle"/>, and one <c>AVPacket*</c> used as a reusable read
/// buffer. Both are disposed in <see cref="DisposeAsync"/>.
/// </para>
/// <para>
/// Threading (ADR-0009): this class is not thread-safe. Only one async operation should
/// be in flight at a time. In the standard pipeline the demux loop is the sole caller.
/// </para>
/// <para>
/// Timestamp semantics: all timestamps in <see cref="DemuxPacket"/> are normalized to
/// <see cref="TimeSpan"/> using the stream's <c>AVStream.time_base</c>. Packets whose
/// <c>pts</c> or <c>dts</c> equals <c>AV_NOPTS_VALUE</c> are surfaced with
/// <c>HasPts</c>/<c>HasDts</c> = <see langword="false"/> and a zero <see cref="TimeSpan"/>.
/// </para>
/// <para>
/// Cancellation (ADR-0013): <see cref="CancellationToken"/> parameters cancel only the
/// current operation, not the session. The session remains open and readable after
/// cancellation.
/// </para>
/// </remarks>
public sealed class DemuxSession : IDemuxSession
{
    // Owned native resources — both freed deterministically in DisposeAsync.
    private readonly FormatContextHandle _formatCtx;
    private readonly nint _packet; // AVPacket* — reused for every av_read_frame call
    private readonly ILogger _logger;

    private bool _disposed;

    // ADR-0034: diagnostics counters. Single-writer (demux pump per ADR-0009)
    // + multi-reader (snapshot call from any thread). Interlocked is sufficient
    // — no multi-field invariant is needed; each counter stands alone.
    private long _packetsRead;
    private long _bytesRead;
    private long _seeksPerformed;
    private int _endOfStreamReached; // bool encoded as int for Interlocked.Exchange

    /// <summary>
    /// Internal constructor — callers must use <see cref="DemuxSessionFactory.OpenAsync"/>
    /// which validates arguments and performs the FFmpeg open sequence.
    /// </summary>
    /// <param name="formatCtx">
    /// The <see cref="FormatContextHandle"/> wrapping the opened <c>AVFormatContext*</c>.
    /// Ownership transfers to this session.
    /// </param>
    /// <param name="packet">
    /// A pre-allocated <c>AVPacket*</c> used as the reusable read buffer.
    /// Ownership transfers to this session.
    /// </param>
    /// <param name="mediaInfo">Pre-populated metadata for this session.</param>
    internal DemuxSession(
        FormatContextHandle formatCtx,
        nint packet,
        MediaInfo mediaInfo,
        ILogger? logger = null
    )
    {
        _formatCtx = formatCtx ?? throw new ArgumentNullException(nameof(formatCtx));
        _packet = packet;
        MediaInfo = mediaInfo ?? throw new ArgumentNullException(nameof(mediaInfo));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public MediaInfo MediaInfo { get; }

    /// <summary>
    /// Gets the raw <c>AVFormatContext*</c> pointer for use by decoder factories within
    /// the <c>FrameFlow.Decoding</c> layer. External consumers should not use this directly.
    /// </summary>
    public nint FormatContextPtr => _formatCtx.DangerousGetHandle();

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown when the session has been disposed.</exception>
    public ValueTask<DemuxPacket?> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // av_read_frame is a synchronous call and returns quickly for file-backed sources.
        // For streaming sources it may block on I/O; for Phase 02 we target local files
        // and do not run this on a background thread. Future phases may move I/O-bound reads
        // to Task.Run if streaming latency becomes an issue (ADR-0009).
        var packet = ReadNextPacket();
        return ValueTask.FromResult(packet);
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown when the session has been disposed.</exception>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Seek using the global time base (stream_index = -1, timestamp in microseconds).
        long timestamp = (long)(position.TotalSeconds * FFAvFormat.AvTimeBase);
        nint ctxPtr = _formatCtx.DangerousGetHandle();
        int result = FFAvFormat.av_seek_frame(
            ctxPtr,
            streamIndex: -1,
            timestamp,
            flags: FFAvFormat.AvseekFlagBackward
        );

        if (result < 0)
        {
            _logger.LogWarning(
                "Seek failed for position {Position} (error code {ErrorCode})",
                position,
                result
            );
            throw new InvalidOperationException(
                $"FFmpeg seek failed with error code {result} for position {position}."
            );
        }

        // Discard any per-stream packets the demuxer pre-fetched before
        // the seek. Without this, the next av_read_frame can return a
        // stale audio packet whose PTS is well before the seek target —
        // the audio sink then captures that stale PTS as its
        // _baseSourceTime, the master clock starts way behind the post-
        // seek video frames, and PaceUntil hangs waiting for the clock
        // to catch up. Verified repro on a 213 s H.264/AAC clip: seek to 42s,
        // audio's first buffer had PTS ~17.5s (a 24.5 s gap),
        // PaceUntil waited for the clock to advance 24.5 s of realtime
        // before forwarding the first post-seek video frame.
        FFAvFormat.avformat_flush(ctxPtr);

        _logger.LogDebug("Seek completed to position {Position}", position);
        Interlocked.Increment(ref _seeksPerformed);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Marks the stream at <paramref name="streamIndex"/> for full discard at the
    /// demuxer (FFmpeg <c>AVDISCARD_ALL</c>) so <c>av_read_frame</c> skips its
    /// packets (ADR-0059).
    /// </summary>
    /// <param name="streamIndex">Index of the stream within the format context.</param>
    /// <remarks>
    /// <para>
    /// Use this for streams the pipeline has no consumer for. A discarded stream's
    /// packets are dropped by the read loop instead of being copied into managed
    /// memory, counted in <see cref="GetDiagnostics"/>, or routed to a decoder.
    /// This is what keeps an unconsumed audio stream from backpressuring the single
    /// shared demux pump and starving video (ADR-0059): without it, audio packets
    /// pile up in a decoder queue that nothing drains, the pump blocks once that
    /// queue fills, and it stops reading video.
    /// </para>
    /// <para>
    /// Because the flag is set after <c>avformat_find_stream_info</c>, a small
    /// number of packets the probe had already buffered can still be returned on
    /// the first reads; every packet read fresh from the file afterwards is
    /// skipped. Callers that must not feed even those few packets to a decoder
    /// should also skip constructing the stream's decoder (as
    /// <c>SubstrateSession</c> does).
    /// </para>
    /// <para>
    /// The discard flag lives on the <c>AVStream</c> and persists across
    /// <see cref="SeekAsync"/> — once discarded, a stream stays discarded for the
    /// lifetime of the session.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when the session has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="streamIndex"/> is negative or not a valid stream index.
    /// </exception>
    public unsafe void DiscardStream(int streamIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint ctx = _formatCtx.DangerousGetHandle();
        ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)ctx);

        if ((uint)streamIndex >= fmtCtx.nb_streams)
            throw new ArgumentOutOfRangeException(
                nameof(streamIndex),
                streamIndex,
                $"Stream index must be in [0, {fmtCtx.nb_streams})."
            );

        AVStream* stream = fmtCtx.streams[streamIndex];
        if (stream == null)
            return;

        stream->discard = AVDiscard.AVDISCARD_ALL;
        _logger.LogDebug("Stream {StreamIndex} marked AVDISCARD_ALL at the demuxer.", streamIndex);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _logger.LogDebug("DemuxSession disposing.");

        // Free the reusable packet first (data is already unref'd after each read).
        // av_packet_free is in libavcodec in FFmpeg 7.x.
        var pkt = _packet;
        if (pkt != nint.Zero)
            FFAvCodec.av_packet_free(ref pkt);

        // Free the format context. FormatContextHandle calls avformat_close_input.
        _formatCtx.Dispose();

        return ValueTask.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads the next packet from the format context.
    /// Returns <see langword="null"/> at end of stream.
    /// </summary>
    private unsafe DemuxPacket? ReadNextPacket()
    {
        nint ctx = _formatCtx.DangerousGetHandle();

        int result = FFAvFormat.av_read_frame(ctx, _packet);

        if (result == FFAvUtil.AvErrorEof)
        {
            // ADR-0034: latch EOF for diagnostics. We use Exchange rather than
            // a plain write so future readers can tell whether EOF was a
            // fresh observation or a repeat (not used today, but the latch
            // semantic is part of the snapshot contract).
            Interlocked.Exchange(ref _endOfStreamReached, 1);
            return null;
        }

        if (result < 0)
            throw new InvalidOperationException(
                $"FFmpeg av_read_frame failed with error code {result}."
            );

        try
        {
            var packet = BuildManagedPacket(ctx);
            Interlocked.Increment(ref _packetsRead);
            if (packet.Data.Length > 0)
                Interlocked.Add(ref _bytesRead, packet.Data.Length);
            return packet;
        }
        finally
        {
            // Unref the packet data. The AVPacket struct itself is reused next call.
            // av_packet_unref is in libavcodec in FFmpeg 7.x.
            FFAvCodec.av_packet_unref(_packet);
        }
    }

    /// <inheritdoc/>
    public DemuxSessionDiagnosticsSnapshot GetDiagnostics() =>
        new(
            PacketsRead: Interlocked.Read(ref _packetsRead),
            BytesRead: Interlocked.Read(ref _bytesRead),
            SeeksPerformed: Interlocked.Read(ref _seeksPerformed),
            EndOfStreamReached: Volatile.Read(ref _endOfStreamReached) == 1
        );

    /// <summary>
    /// Internal counter hook called by <c>DecodingPipeline.RunDemuxPumpAsync</c>
    /// after each successful direct read from the format context. The pipeline
    /// pump bypasses <see cref="ReadPacketAsync"/> for performance, so the
    /// session-level diagnostics counters need an explicit increment from
    /// that path (ADR-0034).
    /// </summary>
    internal void RecordPacketRead(int packetSizeBytes)
    {
        Interlocked.Increment(ref _packetsRead);
        if (packetSizeBytes > 0)
            Interlocked.Add(ref _bytesRead, packetSizeBytes);
    }

    /// <summary>
    /// Internal counter hook called by <c>DecodingPipeline.RunDemuxPumpAsync</c>
    /// when the pump observes EOF from the format context (ADR-0034).
    /// </summary>
    internal void RecordEndOfStream() => Interlocked.Exchange(ref _endOfStreamReached, 1);

    /// <summary>
    /// Copies the current packet's data and metadata into a managed <see cref="DemuxPacket"/>.
    /// Called while <c>_packet</c> still holds valid reference-counted data.
    /// </summary>
    private unsafe DemuxPacket BuildManagedPacket(nint ctx)
    {
        ref AVPacket pkt = ref Unsafe.AsRef<AVPacket>((void*)_packet);

        long rawPts = pkt.pts;
        long rawDts = pkt.dts;
        int streamIndex = pkt.stream_index;
        int flags = pkt.flags;
        int size = pkt.size;
        byte* dataPtr = pkt.data;
        long rawDuration = pkt.duration;

        bool isKeyFrame = (flags & FFmpegConstants.PktFlagKey) != 0;

        // Read the stream's time base to normalize timestamps.
        GetStreamTimeBase(ctx, streamIndex, out int timeBaseNum, out int timeBaseDen);

        bool hasPts = rawPts != FFAvUtil.AvNoPtsValue;
        bool hasDts = rawDts != FFAvUtil.AvNoPtsValue;

        TimeSpan pts = hasPts ? RescaleToTimeSpan(rawPts, timeBaseNum, timeBaseDen) : TimeSpan.Zero;
        TimeSpan dts = hasDts ? RescaleToTimeSpan(rawDts, timeBaseNum, timeBaseDen) : TimeSpan.Zero;
        TimeSpan duration =
            rawDuration > 0
                ? RescaleToTimeSpan(rawDuration, timeBaseNum, timeBaseDen)
                : TimeSpan.Zero;

        // Copy packet data into a managed byte array.
        // This satisfies ADR-0005: no native pointers escape this layer.
        byte[] data;
        if (size > 0 && dataPtr != null)
        {
            data = new byte[size];
            new ReadOnlySpan<byte>(dataPtr, size).CopyTo(data);
        }
        else
        {
            data = [];
        }

        return new DemuxPacket(
            streamIndex: streamIndex,
            pts: pts,
            hasPts: hasPts,
            dts: dts,
            hasDts: hasDts,
            duration: duration,
            data: data,
            isKeyFrame: isKeyFrame
        );
    }

    /// <summary>
    /// Reads the <c>time_base</c> of the stream at <paramref name="streamIndex"/> from
    /// the format context's stream array.
    /// </summary>
    private static unsafe void GetStreamTimeBase(
        nint ctx,
        int streamIndex,
        out int num,
        out int den
    )
    {
        ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)ctx);

        uint nbStreams = fmtCtx.nb_streams;

        if ((uint)streamIndex >= nbStreams)
        {
            // Defensive fallback: use AV_TIME_BASE (1/1000000) so timestamps are in microseconds.
            num = 1;
            den = FFAvUtil.AvTimeBase;
            return;
        }

        // streams is AVStream** — an array of pointers to AVStream.
        AVStream* stream = fmtCtx.streams[streamIndex];

        num = stream->time_base.num;
        den = stream->time_base.den;

        // Guard against degenerate time bases (denominator must be > 0).
        if (den <= 0)
        {
            num = 1;
            den = FFAvUtil.AvTimeBase;
        }
    }

    /// <summary>
    /// Converts an FFmpeg timestamp in stream time base units to a .NET
    /// <see cref="TimeSpan"/> (1 tick = 100 ns = 10^-7 s).
    /// </summary>
    /// <remarks>
    /// The conversion is:
    ///   ticks = timestamp * timeBaseNum * TimeSpan.TicksPerSecond / timeBaseDen
    ///
    /// Intermediate multiplication uses <see cref="long"/> arithmetic. For typical
    /// time bases (1/90000, 1/44100, etc.) this will not overflow for media files
    /// with durations under ~29 years.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TimeSpan RescaleToTimeSpan(long timestamp, int timeBaseNum, int timeBaseDen)
    {
        // ticks = timestamp * (timeBaseNum / timeBaseDen) * TicksPerSecond
        //       = timestamp * timeBaseNum * TicksPerSecond / timeBaseDen
        // Use checked arithmetic to surface overflows during development.
        long ticks = timestamp * (long)timeBaseNum * TimeSpan.TicksPerSecond / timeBaseDen;
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// Builds a <see cref="MediaInfo"/> from an opened <c>AVFormatContext*</c>.
    /// Called by <see cref="DemuxSessionFactory"/> after a successful open.
    /// </summary>
    /// <param name="ctx">The raw <c>AVFormatContext*</c> pointer.</param>
    /// <returns>A fully populated <see cref="MediaInfo"/> instance.</returns>
    internal static unsafe MediaInfo BuildMediaInfo(nint ctx)
    {
        ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)ctx);

        // Duration is in AV_TIME_BASE (microseconds).
        long rawDuration = fmtCtx.duration;
        TimeSpan duration =
            rawDuration > 0 ? TimeSpan.FromMicroseconds(rawDuration) : TimeSpan.Zero;

        uint nbStreams = fmtCtx.nb_streams;
        AVStream** streamsArr = fmtCtx.streams;

        var videoStreams = new List<VideoStreamInfo>();
        var audioStreams = new List<AudioStreamInfo>();

        for (uint i = 0; i < nbStreams; i++)
        {
            AVStream* stream = streamsArr[i];
            if (stream == null)
                continue;

            int streamIdx = stream->index;

            AVCodecParameters* codecPar = stream->codecpar;
            if (codecPar == null)
                continue;

            int mediaType = (int)codecPar->codec_type;
            int codecId = (int)codecPar->codec_id;

            string codecName = FFAvCodec.avcodec_get_name(codecId);

            if (mediaType == FFAvUtil.AvMediaTypeVideo)
            {
                int width = codecPar->width;
                int height = codecPar->height;

                int fpsNum = stream->avg_frame_rate.num;
                int fpsDen = stream->avg_frame_rate.den;
                double fps = fpsDen > 0 ? (double)fpsNum / fpsDen : 0.0;

                videoStreams.Add(new VideoStreamInfo(streamIdx, codecName, width, height, fps));
            }
            else if (mediaType == FFAvUtil.AvMediaTypeAudio)
            {
                int sampleRate = codecPar->sample_rate;
                int channels = codecPar->ch_layout.nb_channels;

                audioStreams.Add(new AudioStreamInfo(streamIdx, codecName, sampleRate, channels));
            }
            // Other stream types (subtitle, data, etc.) are not surfaced in Phase 02.
        }

        // Container name is not trivially accessible from AVFormatContext struct offsets.
        // Use a placeholder for now; a future phase can extract it via iformat->name.
        const string containerName = "unknown";

        return new MediaInfo(containerName, duration, videoStreams, audioStreams);
    }
}
