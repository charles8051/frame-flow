// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using System.Runtime.InteropServices;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Encoding.Internal;

/// <summary>
/// libavcodec-backed H.264 video encoder (ADR-0040). Accepts decoded BGRA32
/// <see cref="IVideoFrame"/>s, converts them to YUV420P via <c>sws_scale</c>,
/// and produces <see cref="EncodedPacket"/>s via the
/// <c>avcodec_send_frame</c> / <c>avcodec_receive_packet</c> loop — the encode
/// mirror of <c>FrameFlow.Decoding.VideoDecoder</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Native ownership (ADR-0005).</b> Owns, for its lifetime: the
/// <c>AVCodecContext</c> (<see cref="CodecContextHandle"/>), one reusable
/// YUV420P source <c>AVFrame</c> (<see cref="FrameHandle"/>), one reusable
/// receive <c>AVPacket</c> (<see cref="PacketHandle"/>), and the BGRA→YUV420P
/// <c>SwsContext</c> (<see cref="SwsContextHandle"/>). All are released in
/// <see cref="Dispose"/>. No native pointer escapes this type; encoded payloads
/// cross the boundary as managed <see cref="EncodedPacket"/> byte copies.
/// </para>
/// <para>
/// <b>Lazy open.</b> The codec context opens on the first
/// <see cref="Encode"/>, taking geometry from
/// <see cref="H264EncoderOptions"/> when specified, otherwise from the first
/// frame (rounded down to even dimensions, which H.264 4:2:0 requires).
/// </para>
/// <para>
/// <b>Threading.</b> Not thread-safe; a single caller drives
/// <see cref="Encode"/> / <see cref="Flush"/> in sequence.
/// </para>
/// </remarks>
internal sealed class H264VideoEncoder : IVideoEncoder, INativeVideoEncoder, IEncodeCodec<EncodedPacket>
{
    private readonly H264EncoderOptions _options;
    private readonly ILogger _logger;

    private CodecContextHandle? _codecCtx;
    private FrameHandle? _frame;
    private PacketHandle? _packet;
    private SwsContextHandle? _sws;

    private int _codedWidth;
    private int _codedHeight;
    private int _sourceWidth;
    private int _sourceHeight;
    private long _nextPts;
    private long _nextOutputPts;
    private bool _opened;
    private bool _flushed;
    private bool _disposed;

    // The frame pointer the protocol's SendInput should hand to avcodec_send_frame:
    // the filled source frame for a normal encode, or nint.Zero for the flush. Set
    // immediately before EncodeDriver.Run cranks the (synchronous, single-input) machine.
    private nint _pendingSendFrame;

    internal H264VideoEncoder(H264EncoderOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger =
            loggerFactory?.CreateLogger<H264VideoEncoder>()
            ?? NullLogger<H264VideoEncoder>.Instance;
    }

    /// <inheritdoc/>
    public EncoderInfo Info =>
        new(
            _options.EncoderName,
            _codedWidth,
            _codedHeight,
            _options.FrameRateNumerator,
            _options.FrameRateDenominator
        );

    /// <inheritdoc/>
    public bool IsOpen => _opened;

    /// <inheritdoc/>
    CodecContextHandle INativeVideoEncoder.CodecContext =>
        _codecCtx ?? throw new InvalidOperationException("Encoder is not open.");

    /// <inheritdoc/>
    int INativeVideoEncoder.TimeBaseNumerator => _options.FrameRateDenominator;

    /// <inheritdoc/>
    int INativeVideoEncoder.TimeBaseDenominator => _options.FrameRateNumerator;

    /// <inheritdoc/>
    public IReadOnlyList<EncodedPacket> Encode(IVideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_flushed)
            throw new InvalidOperationException("Cannot encode after Flush().");

        CpuFrameData cpu =
            frame.AsCpu()
            ?? frame.ToCpu();

        if (!_opened)
            Open(cpu.Width, cpu.Height);

        if (cpu.Width != _sourceWidth || cpu.Height != _sourceHeight)
        {
            throw new InvalidOperationException(
                $"Frame geometry changed mid-stream: encoder opened for "
                    + $"{_sourceWidth}x{_sourceHeight} but received {cpu.Width}x{cpu.Height}. "
                    + "Resize frames to a constant size before encoding."
            );
        }

        var output = new List<EncodedPacket>(1);
        FillSourceFrame(cpu);
        _pendingSendFrame = _frame!.DangerousGetHandle();
        EncodeDriver.Run(this, output);
        return output;
    }

    /// <inheritdoc/>
    public IReadOnlyList<EncodedPacket> Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _flushed)
            return [];

        _flushed = true;
        var output = new List<EncodedPacket>();
        // A null frame signals end-of-stream; the machine drains to AVERROR_EOF.
        _pendingSendFrame = nint.Zero;
        EncodeDriver.Run(this, output);
        return output;
    }

    // ─────────────────────────────────────────────────────────────────
    // Open
    // ─────────────────────────────────────────────────────────────────

    private void Open(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new InvalidOperationException(
                $"Cannot open encoder for non-positive geometry {sourceWidth}x{sourceHeight}."
            );

        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;

        // Coded dimensions: explicit option override, else source. H.264 4:2:0
        // requires even dimensions — round down.
        int wanted = _options.Width > 0 ? _options.Width : sourceWidth;
        int wantedH = _options.Height > 0 ? _options.Height : sourceHeight;
        _codedWidth = wanted & ~1;
        _codedHeight = wantedH & ~1;
        if (_codedWidth <= 0 || _codedHeight <= 0)
            throw new InvalidOperationException(
                $"Coded dimensions {_codedWidth}x{_codedHeight} are invalid after even-rounding."
            );

        nint codec = FFAvCodec.avcodec_find_encoder_by_name(_options.EncoderName);
        if (codec == nint.Zero)
            throw new InvalidOperationException(
                $"H.264 encoder '{_options.EncoderName}' is not available in the loaded FFmpeg build."
            );

        nint ctxPtr = FFAvCodec.avcodec_alloc_context3(codec);
        if (ctxPtr == nint.Zero)
            throw new InvalidOperationException("avcodec_alloc_context3 failed (out of memory).");
        _codecCtx = new CodecContextHandle(ctxPtr);

        var writer = new AvCodecContextWriter(ctxPtr);
        writer.Width = _codedWidth;
        writer.Height = _codedHeight;
        writer.PixelFormat = FFSwScale.AvPixFmtYuv420P;
        writer.BitRate = _options.BitRate;
        writer.GopSize = _options.GopSize;
        writer.MaxBFrames = 0; // libopenh264 has no B-frame support.
        writer.ColorRange = 1; // AVCOL_RANGE_MPEG — matches swscale's limited-range YUV default.
        writer.SetTimeBase(_options.FrameRateDenominator, _options.FrameRateNumerator);
        writer.SetFrameRate(_options.FrameRateNumerator, _options.FrameRateDenominator);
        // MP4 requires global headers (SPS/PPS in extradata, not in-band).
        writer.AddFlags(FFAvCodec.AvCodecFlagGlobalHeader);

        int openRc = FFAvCodec.avcodec_open2(ctxPtr, codec, nint.Zero);
        if (openRc < 0)
            throw new InvalidOperationException(
                $"avcodec_open2 failed for '{_options.EncoderName}': {DescribeError(openRc)}"
            );

        // Reusable YUV420P source frame, buffer-allocated to the coded geometry.
        nint framePtr = FFAvUtil.av_frame_alloc();
        if (framePtr == nint.Zero)
            throw new InvalidOperationException("av_frame_alloc failed (out of memory).");
        _frame = new FrameHandle(framePtr);
        var frameWriter = new AvFrameWriter(framePtr);
        frameWriter.Format = FFSwScale.AvPixFmtYuv420P;
        frameWriter.Width = _codedWidth;
        frameWriter.Height = _codedHeight;
        int bufRc = FFAvUtil.av_frame_get_buffer(framePtr, 0);
        if (bufRc < 0)
            throw new InvalidOperationException(
                $"av_frame_get_buffer failed: {DescribeError(bufRc)}"
            );

        // Reusable receive packet.
        nint pktPtr = FFAvCodec.av_packet_alloc();
        if (pktPtr == nint.Zero)
            throw new InvalidOperationException("av_packet_alloc failed (out of memory).");
        _packet = new PacketHandle(pktPtr);

        // BGRA32 → YUV420P conversion context (source geometry → coded geometry).
        nint swsPtr = FFSwScale.sws_getContext(
            _sourceWidth,
            _sourceHeight,
            FFSwScale.AvPixFmtBgra,
            _codedWidth,
            _codedHeight,
            FFSwScale.AvPixFmtYuv420P,
            FFSwScale.SwsBilinear,
            nint.Zero,
            nint.Zero,
            nint.Zero
        );
        if (swsPtr == nint.Zero)
            throw new InvalidOperationException("sws_getContext (BGRA→YUV420P) failed.");
        _sws = new SwsContextHandle(swsPtr);

        _opened = true;
        LogOpened(
            _logger,
            _options.EncoderName,
            _codedWidth,
            _codedHeight,
            _options.FrameRateNumerator,
            _options.FrameRateDenominator
        );
    }

    // ─────────────────────────────────────────────────────────────────
    // Per-frame conversion + encode
    // ─────────────────────────────────────────────────────────────────

    private unsafe void FillSourceFrame(CpuFrameData cpu)
    {
        nint framePtr = _frame!.DangerousGetHandle();

        // The reusable frame may still be referenced by the encoder from the
        // previous send; copy-on-write if so before overwriting its pixels.
        int wrc = FFAvUtil.av_frame_make_writable(framePtr);
        if (wrc < 0)
            throw new InvalidOperationException(
                $"av_frame_make_writable failed: {DescribeError(wrc)}"
            );

        var frameWriter = new AvFrameWriter(framePtr);

        using MemoryHandle srcPin = cpu.PlaneY.Pin();

        byte** srcSlice = stackalloc byte*[4];
        srcSlice[0] = (byte*)srcPin.Pointer;
        srcSlice[1] = null;
        srcSlice[2] = null;
        srcSlice[3] = null;

        int* srcStrides = stackalloc int[4];
        srcStrides[0] = cpu.StrideY;
        srcStrides[1] = 0;
        srcStrides[2] = 0;
        srcStrides[3] = 0;

        // YUV420P destination: 3 planes from the encoder's source frame.
        byte** dstSlice = stackalloc byte*[4];
        dstSlice[0] = frameWriter.GetDataPointer(0);
        dstSlice[1] = frameWriter.GetDataPointer(1);
        dstSlice[2] = frameWriter.GetDataPointer(2);
        dstSlice[3] = null;

        int* dstStrides = stackalloc int[4];
        dstStrides[0] = frameWriter.GetLineSize(0);
        dstStrides[1] = frameWriter.GetLineSize(1);
        dstStrides[2] = frameWriter.GetLineSize(2);
        dstStrides[3] = 0;

        int rows = FFSwScale.sws_scale(
            _sws!.DangerousGetHandle(),
            srcSlice,
            srcStrides,
            0,
            _sourceHeight,
            dstSlice,
            dstStrides
        );
        if (rows <= 0)
            throw new InvalidOperationException("sws_scale (BGRA→YUV420P) produced no rows.");

        // Monotonic PTS in the encoder time base ({1/fps}): 0, 1, 2, …
        frameWriter.Pts = _nextPts++;
    }

    // ─────────────────────────────────────────────────────────────────
    // IEncodeCodec — the narrow effect surface EncodeDriver names (ADR-0055,
    // encode mirror). These are the only FFmpeg send/receive calls in the type;
    // all sequencing decisions come from the pure EncodeProtocol core.
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    CodecReturn IEncodeCodec<EncodedPacket>.TrySendFrame() =>
        EncodeDriver.Classify(
            FFAvCodec.avcodec_send_frame(_codecCtx!.DangerousGetHandle(), _pendingSendFrame)
        );

    /// <inheritdoc/>
    CodecReturn IEncodeCodec<EncodedPacket>.ReceivePacket() =>
        EncodeDriver.Classify(
            FFAvCodec.avcodec_receive_packet(
                _codecCtx!.DangerousGetHandle(),
                _packet!.DangerousGetHandle()
            )
        );

    /// <inheritdoc/>
    EncodedPacket? IEncodeCodec<EncodedPacket>.BuildPacket()
    {
        nint pktPtr = _packet!.DangerousGetHandle();
        EncodedPacket built = BuildPacket(pktPtr);
        FFAvCodec.av_packet_unref(pktPtr);
        return built;
    }

    private EncodedPacket BuildPacket(nint pktPtr)
    {
        var pkt = new AvEncodedPacketAccessor(pktPtr);
        byte[] data = pkt.CopyData();
        bool key = (pkt.Flags & FFmpegConstants.PktFlagKey) != 0;

        // libopenh264 does not reliably propagate the input frame's PTS to its
        // output packets (the muxer otherwise warns "Encoder did not produce
        // proper pts, making some up"). Since this encoder is constant-frame-
        // rate with no B-frames (output order == input order), fall back to a
        // monotonic per-output counter when the codec leaves PTS/DTS unset.
        // A B-frame-capable override encoder keeps its own (correct) values.
        long pts = pkt.Pts != FFAvUtil.AvNoPtsValue ? pkt.Pts : _nextOutputPts;
        long dts = pkt.Dts != FFAvUtil.AvNoPtsValue ? pkt.Dts : pts;
        long duration = pkt.Duration > 0 ? pkt.Duration : 1;
        _nextOutputPts = Math.Max(_nextOutputPts, pts) + 1;

        return new EncodedPacket(
            data,
            pts,
            dts,
            duration,
            _options.FrameRateDenominator,
            _options.FrameRateNumerator,
            key,
            streamIndex: 0
        );
    }

    private static unsafe string DescribeError(int errnum)
    {
        const int bufSize = 256;
        byte* buf = stackalloc byte[bufSize];
        int rc = FFAvUtil.av_strerror(errnum, (nint)buf, bufSize);
        if (rc < 0)
            return $"AVERROR {errnum}";
        return Marshal.PtrToStringUTF8((nint)buf) ?? $"AVERROR {errnum}";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sws?.Dispose();
        _packet?.Dispose();
        _frame?.Dispose();
        _codecCtx?.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────
    // Logging (ADR-0010)
    // ─────────────────────────────────────────────────────────────────

    private static readonly Action<ILogger, string, int, int, int, int, Exception?> _logOpened =
        LoggerMessage.Define<string, int, int, int, int>(
            LogLevel.Debug,
            new EventId(1, nameof(LogOpened)),
            "H.264 encoder opened: codec={Codec} {Width}x{Height} @ {FpsNum}/{FpsDen}"
        );

    private static void LogOpened(
        ILogger logger,
        string codec,
        int width,
        int height,
        int fpsNum,
        int fpsDen
    ) => _logOpened(logger, codec, width, height, fpsNum, fpsDen, null);
}
