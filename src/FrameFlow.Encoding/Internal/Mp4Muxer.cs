// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Encoding.Internal;

/// <summary>
/// libavformat-backed MP4 muxer (ADR-0040). Writes a single video stream of
/// <see cref="EncodedPacket"/>s into an MP4 (MPEG-4 Part 14) container — the
/// mux mirror of <c>FrameFlow.Decoding.DemuxSession</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Native ownership (ADR-0005).</b> Owns the output
/// <c>AVFormatContext</c> (<see cref="OutputFormatContextHandle"/>, which also
/// closes the AVIO file on release) and one reusable write
/// <c>AVPacket</c>. The video stream's codec parameters — including the H.264
/// SPS/PPS <c>extradata</c> the MP4 <c>avcC</c> box requires — are copied from
/// the encoder's open codec context via <c>avcodec_parameters_from_context</c>.
/// </para>
/// <para>
/// <b>Timestamps.</b> Each packet's PTS/DTS/duration arrive in the encoder's
/// time base and are rescaled into the stream's time base (which the muxer
/// fixes during <c>avformat_write_header</c>) before
/// <c>av_interleaved_write_frame</c>.
/// </para>
/// <para>
/// <b>Threading.</b> Not thread-safe; a single caller sequences
/// <see cref="StartAsync"/> → <see cref="WriteAsync"/> → <see cref="CompleteAsync"/>.
/// </para>
/// </remarks>
internal sealed class Mp4Muxer : IMuxer
{
    private readonly string _path;
    private readonly ILogger _logger;

    private OutputFormatContextHandle? _fmtCtx;
    private PacketHandle? _writePacket;
    private nint _streamPtr;
    private int _streamIndex = -1;
    private int _codecTbNum;
    private int _codecTbDen;
    private int _streamTbNum;
    private int _streamTbDen;

    private bool _started;
    private bool _completed;
    private bool _disposed;

    internal Mp4Muxer(string path, ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = loggerFactory?.CreateLogger<Mp4Muxer>() ?? NullLogger<Mp4Muxer>.Instance;

        int rc = FFAvFormat.avformat_alloc_output_context2(
            out nint ctxPtr,
            nint.Zero,
            "mp4",
            _path
        );
        if (rc < 0 || ctxPtr == nint.Zero)
            throw new InvalidOperationException(
                $"avformat_alloc_output_context2 failed for '{_path}' (mp4): AVERROR {rc}."
            );
        _fmtCtx = new OutputFormatContextHandle(ctxPtr);
    }

    /// <inheritdoc/>
    public int AddVideoStream(IVideoEncoder encoder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(encoder);
        if (_started)
            throw new InvalidOperationException("Cannot add a stream after the muxer has started.");
        if (!encoder.IsOpen || encoder is not INativeVideoEncoder native)
            throw new InvalidOperationException(
                "The encoder must be open (have encoded at least one frame) before it is added "
                    + "to the muxer, so its codec parameters and extradata are available."
            );

        nint ctxPtr = _fmtCtx!.DangerousGetHandle();
        _streamPtr = FFAvFormat.avformat_new_stream(ctxPtr, nint.Zero);
        if (_streamPtr == nint.Zero)
            throw new InvalidOperationException("avformat_new_stream failed.");

        var stream = new AvStreamWriter(_streamPtr);
        int rc = FFAvCodec.avcodec_parameters_from_context(
            stream.CodecPar,
            native.CodecContext.DangerousGetHandle()
        );
        if (rc < 0)
            throw new InvalidOperationException(
                $"avcodec_parameters_from_context failed: AVERROR {rc}."
            );

        _codecTbNum = native.TimeBaseNumerator;
        _codecTbDen = native.TimeBaseDenominator;
        // Hint the stream time base to the encoder's; the muxer may override
        // it during avformat_write_header (we re-read it afterwards).
        stream.SetTimeBase(_codecTbNum, _codecTbDen);

        _streamIndex = stream.Index;
        return _streamIndex;
    }

    /// <inheritdoc/>
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException("Muxer already started.");
        if (_streamIndex < 0)
            throw new InvalidOperationException("Add a stream before starting the muxer.");
        ct.ThrowIfCancellationRequested();

        nint ctxPtr = _fmtCtx!.DangerousGetHandle();
        var fmt = new AvOutputFormatContextAccessor(ctxPtr);

        // Open the output file unless the muxer manages its own I/O (mp4 does not).
        if ((fmt.OutputFormatFlags & FFAvFormat.AvfmtNoFile) == 0)
        {
            nint pb = nint.Zero;
            int openRc = FFAvFormat.avio_open(ref pb, _path, FFAvFormat.AvioFlagWrite);
            if (openRc < 0)
                throw new InvalidOperationException(
                    $"avio_open failed for '{_path}': AVERROR {openRc}."
                );
            fmt.Pb = pb;
        }

        int headerRc = FFAvFormat.avformat_write_header(ctxPtr, nint.Zero);
        if (headerRc < 0)
            throw new InvalidOperationException(
                $"avformat_write_header failed: AVERROR {headerRc}."
            );

        // The muxer may have rewritten the stream time base; re-read it as the
        // authoritative target for packet rescaling.
        var stream = new AvStreamWriter(_streamPtr);
        _streamTbNum = stream.TimeBaseNum;
        _streamTbDen = stream.TimeBaseDen;

        nint pktPtr = FFAvCodec.av_packet_alloc();
        if (pktPtr == nint.Zero)
            throw new InvalidOperationException("av_packet_alloc failed for the muxer write packet.");
        _writePacket = new PacketHandle(pktPtr);

        _started = true;
        LogStarted(_logger, _path, _streamTbNum, _streamTbDen);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(EncodedPacket packet, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(packet);
        if (!_started)
            throw new InvalidOperationException("Call StartAsync before writing packets.");
        if (_completed)
            throw new InvalidOperationException("Cannot write after CompleteAsync.");
        ct.ThrowIfCancellationRequested();

        WritePacketCore(packet);
        return ValueTask.CompletedTask;
    }

    private void WritePacketCore(EncodedPacket packet)
    {
        nint ctxPtr = _fmtCtx!.DangerousGetHandle();
        nint pktPtr = _writePacket!.DangerousGetHandle();
        ReadOnlySpan<byte> data = packet.Data.Span;

        FFAvCodec.av_packet_unref(pktPtr);
        int allocRc = FFAvCodec.av_new_packet(pktPtr, data.Length);
        if (allocRc < 0)
            throw new InvalidOperationException($"av_new_packet failed: AVERROR {allocRc}.");

        var pkt = new AvEncodedPacketAccessor(pktPtr);
        pkt.WriteData(data);
        pkt.StreamIndex = _streamIndex;
        pkt.Pts = Rescale(packet.Pts);
        pkt.Dts = Rescale(packet.Dts);
        pkt.Duration = packet.Duration > 0 ? Rescale(packet.Duration) : 0;
        if (packet.IsKeyFrame)
            pkt.Flags |= FFmpegConstants.PktFlagKey;

        int writeRc = FFAvFormat.av_interleaved_write_frame(ctxPtr, pktPtr);
        // av_interleaved_write_frame consumes the packet's buffer ref; reset
        // the struct for reuse regardless of outcome.
        FFAvCodec.av_packet_unref(pktPtr);
        if (writeRc < 0)
            throw new InvalidOperationException(
                $"av_interleaved_write_frame failed: AVERROR {writeRc}."
            );
    }

    /// <summary>
    /// Rescales a timestamp from the encoder time base to the stream time base
    /// using managed integer arithmetic with round-to-nearest:
    /// <c>ts · codecTb / streamTb = ts · codecNum · streamDen / (codecDen · streamNum)</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> use <c>FFAvUtil.av_rescale_q</c>: that
    /// binding flattens FFmpeg's two <c>AVRational</c> struct parameters into
    /// four <c>int</c>s, which is ABI-incorrect on x64 (the struct args land in
    /// the wrong registers, so the rescale reads a garbage denominator and
    /// returns <c>AV_NOPTS_VALUE</c>). Timestamp magnitudes here are small, so
    /// 64-bit arithmetic does not overflow.
    /// </remarks>
    private long Rescale(long ts)
    {
        if (ts == FFAvUtil.AvNoPtsValue)
            return ts;
        if (_codecTbDen <= 0 || _streamTbNum <= 0 || _streamTbDen <= 0)
            return ts;

        long num = ts * _codecTbNum * _streamTbDen;
        long den = (long)_codecTbDen * _streamTbNum;
        return den == 0 ? ts : (num + (den / 2)) / den;
    }

    /// <inheritdoc/>
    public ValueTask CompleteAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started || _completed)
            return ValueTask.CompletedTask;
        ct.ThrowIfCancellationRequested();

        Finalize(throwOnError: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Writes the trailer and closes the AVIO file. Shared by
    /// <see cref="CompleteAsync"/> (which surfaces errors) and
    /// <see cref="DisposeAsync"/> (best-effort finalization of an aborted clip).
    /// </summary>
    private void Finalize(bool throwOnError)
    {
        _completed = true;
        nint ctxPtr = _fmtCtx!.DangerousGetHandle();

        int trailerRc = FFAvFormat.av_write_trailer(ctxPtr);
        if (trailerRc < 0 && throwOnError)
            throw new InvalidOperationException($"av_write_trailer failed: AVERROR {trailerRc}.");

        // Close the AVIO file now and null pb so the handle's release path
        // doesn't double-close.
        var fmt = new AvOutputFormatContextAccessor(ctxPtr);
        if ((fmt.OutputFormatFlags & FFAvFormat.AvfmtNoFile) == 0)
        {
            nint pb = fmt.Pb;
            if (pb != nint.Zero)
            {
                FFAvFormat.avio_closep(ref pb);
                fmt.Pb = pb; // now nint.Zero
            }
        }

        LogCompleted(_logger, _path);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        // Finalize an aborted clip best-effort so the file is at least a valid
        // (if short) MP4 rather than a headerless fragment.
        if (_started && !_completed)
        {
            try
            {
                Finalize(throwOnError: false);
            }
            catch
            {
                // Best-effort; teardown must not throw.
            }
        }

        _writePacket?.Dispose();
        _fmtCtx?.Dispose(); // closes AVIO (if still open) and frees the context
        return ValueTask.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────
    // Logging (ADR-0010)
    // ─────────────────────────────────────────────────────────────────

    private static readonly Action<ILogger, string, int, int, Exception?> _logStarted =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Debug,
            new EventId(1, nameof(LogStarted)),
            "MP4 muxer started: path={Path} streamTimeBase={TbNum}/{TbDen}"
        );

    private static readonly Action<ILogger, string, Exception?> _logCompleted =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(2, nameof(LogCompleted)),
            "MP4 muxer completed: path={Path}"
        );

    private static void LogStarted(ILogger logger, string path, int tbNum, int tbDen) =>
        _logStarted(logger, path, tbNum, tbDen, null);

    private static void LogCompleted(ILogger logger, string path) =>
        _logCompleted(logger, path, null);
}
