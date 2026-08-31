// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Decoding.Internal;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding;

/// <summary>
/// Software video decoder that accepts raw compressed packets, decodes them with FFmpeg,
/// converts the decoded frames to BGRA32, and yields managed <see cref="CpuVideoFrame"/>
/// instances with normalised presentation timestamps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership (ADR-0005, ADR-0012):</b>
/// This class owns the following native resources for its lifetime:
/// <list type="bullet">
///   <item><c>AVCodecContext*</c> (wrapped in <see cref="CodecContextHandle"/>)</item>
///   <item><c>AVFrame*</c> — one reusable decode frame (wrapped in <see cref="FrameHandle"/>)</item>
///   <item><c>AVPacket*</c> — one reusable packet (wrapped in <see cref="PacketHandle"/>)</item>
///   <item><c>SwsContext*</c> — pixel-format conversion context (wrapped in <see cref="SwsContextHandle"/>)</item>
/// </list>
/// After pixel data has been copied into the pooled managed buffer, the native frame is
/// unreffed and recycled. The managed <see cref="CpuVideoFrame"/> that crosses the queue
/// boundary carries only managed memory — no native pointers escape.
/// </para>
/// <para>
/// <b>Timestamps:</b>
/// Output PTS values are expressed as <see cref="TimeSpan"/> relative to the start of the
/// stream, normalised from the stream's time base using integer arithmetic.
/// Frames whose PTS is <c>AV_NOPTS_VALUE</c> are assigned <see cref="TimeSpan.Zero"/>.
/// Discontinuities are not corrected — the playback layer owns that policy.
/// </para>
/// <para>
/// <b>Threading (ADR-0009):</b>
/// Decode work runs on the thread that calls <see cref="DecodeAsync"/>. The caller is
/// responsible for running this on an appropriate worker task via <c>Task.Run</c>.
/// This class is not thread-safe; only one concurrent caller is supported.
/// </para>
/// <para>
/// <b>Pixel format:</b>
/// All output frames are converted to <c>BGRA32</c> via <c>sws_scale</c>.
/// The output stride is always <c>width * 4</c> (no padding).
/// </para>
/// </remarks>
public sealed partial class VideoDecoder : IVideoDecoder, IDecodeCodec<IVideoFrame>
{
    private readonly CodecContextHandle _codecCtx;
    private readonly FrameHandle _frame;
    private readonly PacketHandle _packet;
    private SwsContextHandle? _swsCtx;
    private readonly IFrameBufferPool _pool;
    private readonly ILogger _logger;

    // Hardware-decode state (ADR-0033). Null/Zero when running software-only.
    // _hwDeviceCtxRef is an AVBufferRef* owned by this decoder, released on dispose.
    // _swFrame holds the CPU-side AVFrame target for av_hwframe_transfer_data.
    // _hwPixelFormat is the AVPixelFormat int we use to detect "this frame is on the GPU."
    private nint _hwDeviceCtxRef;
    private FrameHandle? _swFrame;
    private int _hwPixelFormat = -1;

    // ADR-0038: when true, hwaccel-active decoders yield GpuVideoFrame
    // (cloned AVFrame*) instead of doing the internal readback path.
    // Stored as int + Volatile so the decode worker reads a consistent
    // value if a producer toggles it from another thread.
    private int _yieldHardwareFrames;

    /// <summary>
    /// When <see langword="true"/> and this decoder is bound to a hardware
    /// accelerator (ADR-0033), <see cref="DecodeAsync"/> yields
    /// <see cref="GpuVideoFrame"/> instances that wrap the GPU-resident
    /// <c>AVFrame</c>; the consumer is responsible for either reading them
    /// back to CPU via the <c>FrameFlow.Video</c> <c>ToCpu()</c> operator
    /// or routing them to a GPU-aware sink (ADR-0038 Phase B). When
    /// <see langword="false"/> (the default), the decoder performs an
    /// internal <c>av_hwframe_transfer_data + sws_scale</c> readback and
    /// yields <see cref="CpuVideoFrame"/> as before — the pre-ADR-0038
    /// behavior. Software decoders ignore this flag and always yield CPU
    /// frames.
    /// </summary>
    /// <remarks>
    /// Safe to toggle at any time; the next call to
    /// <see cref="DecodeAsync"/> observes the new value. Existing in-flight
    /// frames are not retroactively converted.
    /// </remarks>
    public bool YieldHardwareFrames
    {
        get => Volatile.Read(ref _yieldHardwareFrames) != 0;
        set => Volatile.Write(ref _yieldHardwareFrames, value ? 1 : 0);
    }

    /// <summary>
    /// Full-queue send policy (ADR-0060). When <see langword="false"/> (the
    /// default) a packet send that finds the bounded queue full <b>blocks</b>,
    /// pacing the single demux pump to the video consumer's rate — the correct
    /// backpressure when video is the sole consumed stream. When
    /// <see langword="true"/> the send instead <b>drops the packet</b>
    /// (drop-newest); that policy is only safe when an audio stream shares the
    /// pump, where blocking on a slow video chain would wedge the pump and
    /// starve audio. Defaulting to drop-newest let an <i>unthrottled</i> pump
    /// (e.g. after the audio stream was discarded for having no consumer,
    /// ADR-0059) read the whole file at IO speed and shed most of the video,
    /// starving playback after roughly one queue's worth of frames.
    /// </summary>
    public bool DropNewestWhenQueueFull
    {
        get => Volatile.Read(ref _dropNewestWhenQueueFull) != 0;
        set => Volatile.Write(ref _dropNewestWhenQueueFull, value ? 1 : 0);
    }

    private int _dropNewestWhenQueueFull;

    // ADR-0034: diagnostics counters. Single-writer (decode worker) so
    // Interlocked is sufficient for cross-thread snapshot reads.
    private long _framesDecoded;
    private long _decodeErrors;

    // Fires when the decode worker emits its first frame. Used by the
    // playback session's InitialBuffering warmup so hardware-decoder
    // cold-start latency is absorbed while the user still sees Loading,
    // not after audio has already begun playing. RunContinuationsAsynchronously
    // prevents the decode worker from inheriting the awaiter's continuation
    // on the hot path. Faulted in DisposeAsync so awaiters don't hang if
    // the decoder is torn down before producing a frame.
    private readonly TaskCompletionSource _firstFrameDecodedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Packets that SendPacketAsync dropped (drop-newest) because the
    // bounded queue was full and blocking would have wedged the
    // shared demux pump. Healthy pipelines stay at zero; non-zero
    // means the video chain fell behind by more than the queue's
    // headroom (~17 s at 30 fps with cap=512).
    private long _packetsDroppedForBackpressure;

    // Rate reporting for the drop path (#143). Guarded by _shedGate: SendPacketAsync is called
    // only from the single demux pump today, but the lock is taken solely on a drop, so it
    // costs nothing on a healthy pipeline and does not rely on that staying true.
    private readonly object _shedGate = new();
    private ShedWindow _shedWindow = ShedWindow.None;

    /// <summary>Stream time base numerator, used for PTS normalisation.</summary>
    private readonly int _timeBaseNum;

    /// <summary>Stream time base denominator, used for PTS normalisation.</summary>
    private readonly int _timeBaseDen;

    /// <summary>Width reported by the stream codec parameters (used for sws context sizing).</summary>
    private readonly int _width;

    /// <summary>Height reported by the stream codec parameters (used for sws context sizing).</summary>
    private readonly int _height;

    // Tracks the source geometry/format that the current swscale context was created for.
    // Some streams can change pixel format or dimensions mid-stream; reusing a stale
    // SwsContext across incompatible frames is unsafe.
    private int _swsSrcWidth = -1;
    private int _swsSrcHeight = -1;
    private int _swsSrcFormat = -1;

    // Depth of the bounded packet queue, configured at construction via
    // VideoDecoderOptions.PacketQueueCapacity (default 512). For video one demuxed
    // packet is one coded frame, so this is also a read-ahead bound in *frames*:
    // 512 ≈ 512 frames ≈ ~20 s at 25 fps. The audio/drop-newest path keeps the
    // larger value (a small queue trips drop-newest mid-GOP on seek — see
    // SendPacketAsync + ResetPacketQueue); the no-audio/block path can run a smaller
    // queue safely because a full-queue send blocks rather than dropping.
    private readonly int _packetQueueCapacity;

    // Packet queue for the session-driven decode path (fed by DecodingPipeline).
    // Created in the constructor and recreated by ResetPacketQueue via
    // CreatePacketQueue so the depth always reflects the configured capacity
    // (mirrors AudioDecoder).
    private Channel<(nint packetPtr, bool isFlush)> _packetQueue;

    // ADR-0055: the input packet currently held by the decode driver. Set when an input is
    // pulled, freed once the codec accepts it (SendCurrentInput), and retained across a
    // send-EAGAIN so the driver re-sends it. A packet still held when the worker is
    // cancelled stays here and is re-fed on the next DecodeAsync — pause must not drop it.
    private nint _pendingRetryPacketPtr;

    // ADR-0055: set true once the input queue is exhausted or a flush sentinel is read, so
    // SendCurrentInput sends a null flush packet. Cleared at the start of each DecodeAsync.
    private bool _inFlushMode;

    // ADR-0055: the managed frame built under _codecSync by ReceiveFrame and handed to the
    // immediately-following BuildFrame. Touched only by the single decode worker.
    private IVideoFrame? _builtFrame;

    private readonly object _codecSync = new();
    private bool _disposed;

    /// <summary>
    /// Initialises a <see cref="VideoDecoder"/> for the given open codec context.
    /// </summary>
    /// <param name="codecCtx">
    /// An open <see cref="CodecContextHandle"/>. Ownership transfers to this decoder.
    /// </param>
    /// <param name="frame">
    /// A pre-allocated <see cref="FrameHandle"/> used as the decode target.
    /// Ownership transfers to this decoder.
    /// </param>
    /// <param name="packet">
    /// A pre-allocated <see cref="PacketHandle"/> used for packet submission.
    /// Ownership transfers to this decoder.
    /// </param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="timeBaseNum">Numerator of the stream time base.</param>
    /// <param name="timeBaseDen">Denominator of the stream time base.</param>
    /// <param name="pool">Buffer pool for managed pixel data. Not owned by the decoder.</param>
    /// <param name="packetQueueCapacity">
    /// Depth of the bounded packet queue (see
    /// <see cref="VideoDecoderOptions.PacketQueueCapacity"/>). Defaults to 512. Must be at least 1.
    /// </param>
    /// <param name="logger">Optional logger for diagnostics (ADR-0010).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="packetQueueCapacity"/> is less than 1.
    /// </exception>
    internal VideoDecoder(
        CodecContextHandle codecCtx,
        FrameHandle frame,
        PacketHandle packet,
        int width,
        int height,
        int timeBaseNum,
        int timeBaseDen,
        IFrameBufferPool pool,
        int packetQueueCapacity = 512,
        ILogger? logger = null
    )
    {
        if (packetQueueCapacity < 1)
            throw new ArgumentOutOfRangeException(
                nameof(packetQueueCapacity),
                packetQueueCapacity,
                $"{nameof(VideoDecoderOptions.PacketQueueCapacity)} must be at least 1."
            );

        _codecCtx = codecCtx;
        _frame = frame;
        _packet = packet;
        _width = width;
        _height = height;
        _timeBaseNum = timeBaseNum;
        _timeBaseDen = timeBaseDen;
        _pool = pool;
        _logger = logger ?? NullLogger.Instance;

        _packetQueueCapacity = packetQueueCapacity;
        _packetQueue = CreatePacketQueue();
    }

    /// <summary>
    /// Creates and opens a <see cref="VideoDecoder"/> for the video stream at
    /// <paramref name="streamIndex"/> inside <paramref name="formatContextPtr"/>.
    /// </summary>
    /// <param name="formatContextPtr">
    /// A live <c>AVFormatContext*</c> pointer (not transferred; the caller retains ownership).
    /// </param>
    /// <param name="streamIndex">
    /// Index of the video stream to decode. Must be a valid stream index.
    /// </param>
    /// <param name="videoOptions">
    /// Optional decoder configuration (e.g.
    /// <see cref="VideoDecoderOptions.PacketQueueCapacity"/>). When null the defaults
    /// from <see cref="VideoDecoderOptions"/> are used.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the codec cannot be found, the context cannot be allocated, or
    /// <c>avcodec_open2</c> fails.
    /// </exception>
    public static VideoDecoder Open(
        nint formatContextPtr,
        int streamIndex,
        VideoDecoderOptions? videoOptions = null,
        ILogger? logger = null
    ) =>
        Open(
            formatContextPtr,
            streamIndex,
            options: null,
            capabilities: null,
            pool: new SharedMemoryFramePool(),
            videoOptions: videoOptions,
            logger: logger
        );

    /// <summary>
    /// Creates and opens a <see cref="VideoDecoder"/> with an injectable frame
    /// buffer pool. Defaults to software-only decode; for hardware-decode
    /// selection use the overload that accepts
    /// <see cref="HardwareDecodeOptions"/> (ADR-0033). Intended for tests and
    /// internal factory code.
    /// </summary>
    internal static VideoDecoder Open(
        nint formatContextPtr,
        int streamIndex,
        IFrameBufferPool pool,
        VideoDecoderOptions? videoOptions = null,
        ILogger? logger = null
    ) =>
        Open(
            formatContextPtr,
            streamIndex,
            options: null,
            capabilities: null,
            pool,
            videoOptions,
            logger
        );

    /// <inheritdoc/>
    /// <remarks>
    /// ADR-0055: the send/receive sequencing now lives in the shared, FFmpeg-free
    /// <see cref="DecodeProtocol"/> Mealy core, cranked by <see cref="DecodeDriver"/>. This
    /// decoder supplies the effects (queue read, locked send/receive, hardware-or-CPU frame
    /// build) through <see cref="IDecodeCodec{T}"/>. Cancellation is propagated cleanly per
    /// ADR-0013, and a packet held un-accepted when the worker is cancelled stays in
    /// <see cref="_pendingRetryPacketPtr"/> and is re-fed on the next call — pause must not
    /// drop a packet, the same guarantee the previous hand-rolled loop gave.
    /// </remarks>
    public async IAsyncEnumerable<IVideoFrame> DecodeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();

        // Clear the flush latch so a re-enumeration (pause/resume) starts a fresh session.
        _inFlushMode = false;

        await foreach (
            var frame in DecodeDriver
                .RunAsync<IVideoFrame>(this, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return frame;
        }
    }

    // ── IDecodeCodec<IVideoFrame> — the effect surface DecodeDriver cranks (ADR-0055) ──

    /// <summary>
    /// Reads the next input from the packet queue. A packet retained from a cancelled
    /// session (in <see cref="_pendingRetryPacketPtr"/>) is re-fed first. Returns
    /// <see langword="false"/> on a flush sentinel or once the queue completes, putting the
    /// decoder into flush mode so <see cref="SendCurrentInput"/> sends a null packet.
    /// </summary>
    async ValueTask<bool> IDecodeCodec<IVideoFrame>.TryBeginNextInputAsync(
        CancellationToken cancellationToken
    )
    {
        lock (_codecSync)
        {
            if (_disposed)
                return false;
            // A packet held un-accepted from a prior (cancelled) session is the next input.
            if (_pendingRetryPacketPtr != nint.Zero)
                return true;
        }

        while (await _packetQueue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_packetQueue.Reader.TryRead(out var item))
            {
                if (item.isFlush)
                {
                    _inFlushMode = true;
                    return false;
                }

                lock (_codecSync)
                {
                    _pendingRetryPacketPtr = item.packetPtr;
                }
                return true;
            }
        }

        // Queue completed without an explicit flush sentinel — flush and drain to EOF.
        _inFlushMode = true;
        return false;
    }

    /// <summary>
    /// Sends the held input (or a null flush packet) under <see cref="_codecSync"/>. The
    /// packet is freed once the decoder accepts it; on <see cref="CodecReturn.Again"/> it is
    /// retained in <see cref="_pendingRetryPacketPtr"/> so the driver re-sends it and a
    /// cancellation preserves it for the next session.
    /// </summary>
    CodecReturn IDecodeCodec<IVideoFrame>.SendCurrentInput()
    {
        lock (_codecSync)
        {
            if (_disposed)
                return CodecReturn.EndOfStream;

            nint ctxPtr = _codecCtx.DangerousGetHandle();
            nint pktArg = _inFlushMode ? nint.Zero : _pendingRetryPacketPtr;
            CodecReturn result = DecodeDriver.Classify(
                FFAvCodec.avcodec_send_packet(ctxPtr, pktArg)
            );

            if (
                !_inFlushMode
                && _pendingRetryPacketPtr != nint.Zero
                && result != CodecReturn.Again
            )
            {
                var ptr = _pendingRetryPacketPtr;
                FFAvCodec.av_packet_free(ref ptr);
                _pendingRetryPacketPtr = nint.Zero;
            }

            return result;
        }
    }

    /// <summary>
    /// Receives one frame and, on success, builds the managed frame and recycles the native
    /// frame — all under <see cref="_codecSync"/> so a concurrent <see cref="Flush"/> cannot
    /// unref the shared <c>AVFrame</c> mid-build. The ADR-0033/0038 GPU and CPU build paths
    /// are preserved; the built frame is stashed for <see cref="BuildFrame"/>.
    /// </summary>
    CodecReturn IDecodeCodec<IVideoFrame>.ReceiveFrame()
    {
        lock (_codecSync)
        {
            if (_disposed)
                return CodecReturn.EndOfStream;

            nint ctxPtr = _codecCtx.DangerousGetHandle();
            nint framePtr = _frame.DangerousGetHandle();
            int receiveRet = FFAvCodec.avcodec_receive_frame(ctxPtr, framePtr);

            if (receiveRet >= 0)
            {
                // ADR-0038: yield a GpuVideoFrame when hardware-active and the frame is in
                // the hardware pixel format; otherwise the CPU readback path.
                if (
                    YieldHardwareFrames
                    && _hwPixelFormat >= 0
                    && new AvFrameAccessor(framePtr).Format == _hwPixelFormat
                )
                {
                    _builtFrame = BuildGpuFrame(framePtr);
                }
                else
                {
                    _builtFrame = BuildManagedFrame(framePtr);
                }

                // Recycle the per-call AVFrame now that the managed wrapper holds its own
                // reference (av_frame_clone for GPU, plane copy for CPU).
                FFAvUtil.av_frame_unref(framePtr);
                return CodecReturn.Ok;
            }

            return DecodeDriver.Classify(receiveRet);
        }
    }

    /// <summary>
    /// Returns the frame stashed by the preceding <see cref="ReceiveFrame"/> (may be
    /// <see langword="null"/> when conversion produced nothing), updating the frames-decoded
    /// counter and first-frame signal. No lock needed: the stash is only touched by the
    /// single decode worker between a receive and this call.
    /// </summary>
    IVideoFrame? IDecodeCodec<IVideoFrame>.BuildFrame()
    {
        var frame = _builtFrame;
        _builtFrame = null;

        if (frame is not null)
        {
            if (Interlocked.Increment(ref _framesDecoded) == 1)
                _firstFrameDecodedTcs.TrySetResult();
            LogVideoFrameDecoded(_logger, frame.Width, frame.Height, frame.Pts.TotalMilliseconds);
        }

        return frame;
    }

    /// <summary>
    /// Queues a raw video packet for decoding. Never blocks the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Drop-newest semantics.</b> The packet queue is bounded; when
    /// full, this method frees the NEW packet (drop-newest) and
    /// returns immediately, instead of awaiting space. The demux pump
    /// MUST not block on video send because the pump is single-
    /// threaded across both streams — a blocked video send starves the
    /// audio decoder, drains the audio sink, freezes the master clock,
    /// freezes <see cref="FrameFlow.Playback.PaceUntil"/>, and
    /// permanently stalls the video chain (self-reinforcing deadlock
    /// that only a manual seek can recover from). This is the
    /// "AvaloniaPlayer freezes video on seek; audio continues several
    /// seconds, then cuts" symptom from docs/DEFERRED_WORK.md.
    /// </para>
    /// <para>
    /// <b>Why drop-newest, not drop-oldest.</b> Drop-oldest would
    /// evict whatever's at the front of the queue — typically a
    /// keyframe right after a seek. Losing the keyframe corrupts the
    /// rest of the GOP and produces visibly garbled frames until the
    /// next IDR. Drop-newest preserves the queued prefix (decoder sees
    /// a complete keyframe + immediate P-frames + tail-truncated GOP),
    /// so the visible artifact is "video stops on the last good frame
    /// for a beat" rather than "video shows corrupt frames." Audio is
    /// unaffected either way. The drop count surfaces in
    /// <see cref="GetDiagnostics"/> so the operator can observe the
    /// shedding without log noise.
    /// </para>
    /// <para>
    /// <b>When healthy pipelines hit this path.</b> They don't, in
    /// practice. The 512-slot queue holds ~17 s of footage at 30 fps;
    /// typical seek transients clear in under a second. Sustained
    /// drops correlate with a real video-chain regression (slow
    /// downstream consumer, GPU contention, etc.) — investigate the
    /// chain when the count climbs, not this method.
    /// </para>
    /// </remarks>
    public ValueTask SendPacketAsync(nint packetPtr, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        // Fast path: queue has room.
        if (_packetQueue.Writer.TryWrite((packetPtr, isFlush: false)))
            return ValueTask.CompletedTask;

        // Queue full (ADR-0060).
        if (!DropNewestWhenQueueFull)
        {
            // Default / no audio sharing the pump: BLOCK so the single demux
            // pump is paced to the video consumer's rate. Without this, a pump
            // that nothing else throttles (e.g. the audio stream was discarded
            // for having no consumer, ADR-0059) reads the whole file at IO
            // speed and the drop path below sheds most of the video — starving
            // playback after roughly one queue's worth of frames. On
            // cancellation the caller (DecodingPipeline) retains the packet for
            // re-feed, so ownership stays consistent with the fast path.
            return _packetQueue.Writer.WriteAsync((packetPtr, isFlush: false), cancellationToken);
        }

        // Drop-newest: an audio stream shares this pump, so the video send must
        // never wedge it (that would starve audio + freeze the clock). Free the
        // new packet so we don't leak; the decoder resyncs on the next keyframe
        // once the queue drains.
        var ptr = packetPtr;
        FFAvCodec.av_packet_free(ref ptr);
        ReportShedRate();
        return ValueTask.CompletedTask;
    }

    /// <summary>Signals end-of-stream to flush buffered frames.</summary>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _packetQueue.Writer.WriteAsync((nint.Zero, isFlush: true), cancellationToken);
    }

    /// <summary>Marks the packet queue as complete.</summary>
    public void CompletePacketQueue() => _packetQueue.Writer.TryComplete();

    /// <inheritdoc/>
    public VideoDecoderDiagnosticsSnapshot GetDiagnostics() =>
        new(
            FramesDecoded: Interlocked.Read(ref _framesDecoded),
            DecodeErrors: Interlocked.Read(ref _decodeErrors),
            HardwareBackend: HardwareBackend,
            PacketsDroppedForBackpressure: Interlocked.Read(ref _packetsDroppedForBackpressure)
        );

    /// <inheritdoc/>
    public Task FirstFrameDecoded => _firstFrameDecodedTcs.Task;

    /// <summary>
    /// Replaces the completed packet queue with a fresh one so that the decoder
    /// can accept new packets after a pause/resume cycle. Any unread packets in the
    /// old queue are drained and freed.
    /// </summary>
    public void ResetPacketQueue()
    {
        // Drain and free any leftover packets from the old queue.
        while (_packetQueue.Reader.TryRead(out var item))
        {
            if (!item.isFlush && item.packetPtr != nint.Zero)
            {
                var ptr = item.packetPtr;
                FFAvCodec.av_packet_free(ref ptr);
            }
        }

        FreePendingRetryPacket();

        // Recreate at the SAME configured depth (_packetQueueCapacity). Every seek and
        // every loop iteration calls this method, so the reset-path depth must match the
        // construction depth — that sync is load-bearing for the drop-newest (audio
        // present) path: 7af6a83 bumped the default from 64 to 512 because a 64-deep
        // queue fills during the post-seek GOP burst and trips drop-newest mid-GOP,
        // breaking P-frame reconstruction ("AvaloniaPlayer freezes video on seek; audio
        // continues several seconds, then cuts" + garbled frames on loop). The no-audio
        // block path can run a smaller depth safely because a full-queue send blocks
        // instead of dropping; SubstrateSession picks the depth per stream-set.
        _packetQueue = CreatePacketQueue();
    }

    /// <summary>
    /// Creates the bounded packet queue at the configured
    /// <see cref="VideoDecoderOptions.PacketQueueCapacity"/>. Used by the constructor and
    /// <see cref="ResetPacketQueue"/> so both paths share the same depth and channel options.
    /// </summary>
    /// <remarks>
    /// <c>FullMode = Wait</c>: a full-queue send either blocks (block mode, the no-audio
    /// default) or is intercepted by <see cref="SendPacketAsync"/>'s drop-newest fast-path
    /// before it would ever wait (drop mode, when audio shares the pump). See
    /// <see cref="DropNewestWhenQueueFull"/>.
    /// </remarks>
    private Channel<(nint packetPtr, bool isFlush)> CreatePacketQueue() =>
        Channel.CreateBounded<(nint, bool)>(
            new BoundedChannelOptions(_packetQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }
        );

    /// <summary>
    /// Flushes the codec's internal decode buffers after a seek so that stale
    /// state is discarded before new packets arrive.
    /// </summary>
    public void Flush()
    {
        ThrowIfDisposed();

        lock (_codecSync)
        {
            if (_disposed)
            {
                return;
            }

            nint codecPtr = _codecCtx.DangerousGetHandle();
            FFAvCodec.avcodec_flush_buffers(codecPtr);

            // Defence-in-depth residual-frame drain: avcodec_flush_buffers is not guaranteed
            // to empty every decoder's output queue across every codec/version combination.
            // Pull any residual frames out explicitly so the post-flush decode loop never sees
            // a stale-PTS leftover. Shared with AudioDecoder.Flush via DecodeDriver — the two
            // copies that used to cite each other to stay in sync are now one function
            // (ADR-0055 §Context).
            nint framePtr = _frame.DangerousGetHandle();
            DecodeDriver.DrainResidualFrames(
                () => FFAvCodec.avcodec_receive_frame(codecPtr, framePtr),
                () => FFAvUtil.av_frame_unref(framePtr)
            );
        }
    }

    private void FreePendingRetryPacket()
    {
        nint packetPtr;
        lock (_codecSync)
        {
            packetPtr = _pendingRetryPacketPtr;
            _pendingRetryPacketPtr = nint.Zero;
        }

        if (packetPtr != nint.Zero)
        {
            FFAvCodec.av_packet_free(ref packetPtr);
        }
    }

    /// <summary>
    /// Copies pixel data from the native <c>AVFrame</c> into a pooled managed buffer
    /// and returns a <see cref="CpuVideoFrame"/>.
    /// Returns <see langword="null"/> if the conversion context cannot be created.
    /// </summary>
    /// <remarks>
    /// When the decoder is bound to a hardware accelerator (ADR-0033) and the
    /// frame's pixel format matches the hardware format
    /// (e.g. <c>AV_PIX_FMT_CUDA</c>), this method first calls
    /// <c>av_hwframe_transfer_data</c> to copy the frame into a CPU-side
    /// <c>AVFrame</c>, then runs the existing sws_scale path on the CPU copy.
    /// </remarks>
    /// <summary>
    /// ADR-0038 GPU yield path: clones the hardware-resident
    /// <c>AVFrame*</c> via <c>av_frame_clone</c> and wraps it in a
    /// <see cref="GpuVideoFrame"/>. The decoder's per-call AVFrame is
    /// then unref'd by the caller; the clone holds an independent
    /// reference to the same device-side buffer (ref-counted by
    /// FFmpeg's <c>AVBufferRef</c>).
    /// </summary>
    private GpuVideoFrame? BuildGpuFrame(nint framePtr)
    {
        var accessor = new AvFrameAccessor(framePtr);
        var pts = accessor.ComputePresentationTime(_timeBaseNum, _timeBaseDen);

        // The software format reported on the GpuVideoFrame is what
        // av_hwframe_transfer_data would produce on readback. For the
        // common CUDA / D3D11VA / VAAPI / VideoToolbox cases this is
        // NV12. P010 (10-bit) etc. is a Phase B concern when high-bit-
        // depth GPU workflows actually land.
        var managed = GpuVideoFrame.CloneFrom(
            sourceAvFrame: framePtr,
            width: accessor.Width,
            height: accessor.Height,
            softwareFormat: PixelFormat.Nv12,
            pts: pts,
            duration: TimeSpan.Zero,
            backend: HardwareBackend ?? HardwareDecodeBackendKind.Other
        );

        if (managed is null)
        {
            Interlocked.Increment(ref _decodeErrors);
        }

        return managed;
    }

    private unsafe CpuVideoFrame? BuildManagedFrame(nint framePtr)
    {
        // If this decoder is running hwaccel and the incoming frame is in the
        // hardware pixel format, transfer to a CPU-side AVFrame first. The
        // downstream sws_scale path then operates on the CPU copy and is
        // identical to the pure-software path.
        if (
            _swFrame is not null
            && _hwPixelFormat >= 0
            && new AvFrameAccessor(framePtr).Format == _hwPixelFormat
        )
        {
            nint swPtr = _swFrame.DangerousGetHandle();
            // av_hwframe_transfer_data with dst->format = AV_PIX_FMT_NONE
            // (the default after av_frame_alloc) selects an appropriate CPU
            // format for the source — typically NV12 for CUDA / VAAPI / D3D11.
            int rc = FFAvUtil.av_hwframe_transfer_data(swPtr, framePtr, 0);
            if (rc < 0)
            {
                Interlocked.Increment(ref _decodeErrors);
                LogHwTransferFailed(_logger, rc);
                return null;
            }

            // Copy PTS from the GPU frame so the CPU copy carries the same
            // timestamp — av_hwframe_transfer_data does not propagate it.
            unsafe
            {
                ref AVFrame src = ref Unsafe.AsRef<AVFrame>((void*)framePtr);
                ref AVFrame dst = ref Unsafe.AsRef<AVFrame>((void*)swPtr);
                dst.pts = src.pts;
                dst.time_base = src.time_base;
            }

            try
            {
                return BuildManagedFrameFromCpu(swPtr);
            }
            finally
            {
                // Free the buffers the transfer allocated so the next decode
                // cycle starts clean.
                FFAvUtil.av_frame_unref(swPtr);
            }
        }

        return BuildManagedFrameFromCpu(framePtr);
    }

    private unsafe CpuVideoFrame? BuildManagedFrameFromCpu(nint framePtr)
    {
        var accessor = new AvFrameAccessor(framePtr);

        int srcWidth = accessor.Width;
        int srcHeight = accessor.Height;
        int srcFormat = accessor.Format;
        long pts = accessor.Pts;

        if (srcWidth <= 0 || srcHeight <= 0)
            return null;

        // Ensure the sws context matches the current frame dimensions and format.
        // The first frame initialises it; format changes (rare) rebuild it.
        EnsureSwsContext(srcWidth, srcHeight, srcFormat);

        if (_swsCtx is null || _swsCtx.IsInvalid)
            return null;

        // Destination: BGRA32, stride = width * 4, no padding.
        int dstWidth = srcWidth;
        int dstHeight = srcHeight;
        const int bytesPerPixel = 4; // BGRA32
        int dstStride = dstWidth * bytesPerPixel;

        IMemoryOwner<byte> buffer = _pool.RentVideoBuffer(dstWidth, dstHeight, bytesPerPixel);

        try
        {
            using var pin = buffer.Memory.Pin();
            byte* dstData = (byte*)pin.Pointer;

            // sws_scale expects arrays of plane pointers and strides.
            // Source planes come from the AVFrame data array.
            byte* srcPlane0 = accessor.GetDataPointer(0);
            byte* srcPlane1 = accessor.GetDataPointer(1);
            byte* srcPlane2 = accessor.GetDataPointer(2);
            int srcLineSize0 = accessor.GetLineSize(0);
            int srcLineSize1 = accessor.GetLineSize(1);
            int srcLineSize2 = accessor.GetLineSize(2);

            byte** srcSlice = stackalloc byte*[4];
            srcSlice[0] = srcPlane0;
            srcSlice[1] = srcPlane1;
            srcSlice[2] = srcPlane2;
            srcSlice[3] = null;

            int* srcStrides = stackalloc int[4];
            srcStrides[0] = srcLineSize0;
            srcStrides[1] = srcLineSize1;
            srcStrides[2] = srcLineSize2;
            srcStrides[3] = 0;

            // sws_scale expects 4 destination plane pointers/strides even for packed
            // single-plane output formats like BGRA. Passing 1-element arrays lets the
            // native code read past the stack buffer, which can corrupt memory and crash
            // the runtime with an ExecutionEngineException.
            byte** dstSlice = stackalloc byte*[4];
            dstSlice[0] = dstData;
            dstSlice[1] = null;
            dstSlice[2] = null;
            dstSlice[3] = null;

            int* dstStrides = stackalloc int[4];
            dstStrides[0] = dstStride;
            dstStrides[1] = 0;
            dstStrides[2] = 0;
            dstStrides[3] = 0;

            int rowsWritten = FFSwScale.sws_scale(
                _swsCtx.DangerousGetHandle(),
                srcSlice,
                srcStrides,
                0,
                srcHeight,
                dstSlice,
                dstStrides
            );

            if (rowsWritten <= 0)
            {
                buffer.Dispose();
                return null;
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        TimeSpan presentationTime = ComputePresentationTime(pts);

        return new CpuVideoFrame(
            pixelData: buffer,
            width: dstWidth,
            height: dstHeight,
            stride: dstStride,
            format: PixelFormat.Bgra32,
            presentationTime: presentationTime
        );
    }

    /// <summary>
    /// Ensures the <c>SwsContext</c> is allocated and matches the given source
    /// dimensions and pixel format. Rebuilds it if the format changes.
    /// </summary>
    private void EnsureSwsContext(int srcWidth, int srcHeight, int srcFormat)
    {
        // Reuse only when the existing context matches the current frame exactly.
        if (
            _swsCtx is not null
            && !_swsCtx.IsInvalid
            && _swsSrcWidth == srcWidth
            && _swsSrcHeight == srcHeight
            && _swsSrcFormat == srcFormat
        )
            return;

        _swsCtx?.Dispose();
        _swsCtx = null;
        _swsSrcWidth = -1;
        _swsSrcHeight = -1;
        _swsSrcFormat = -1;

        nint ctx = FFSwScale.sws_getContext(
            srcWidth,
            srcHeight,
            srcFormat,
            srcWidth,
            srcHeight,
            FFSwScale.AvPixFmtBgra,
            FFSwScale.SwsBilinear,
            nint.Zero,
            nint.Zero,
            nint.Zero
        );

        if (ctx == nint.Zero)
        {
            _swsCtx = null;
            return;
        }

        _swsCtx = new SwsContextHandle(ctx);
        _swsSrcWidth = srcWidth;
        _swsSrcHeight = srcHeight;
        _swsSrcFormat = srcFormat;
    }

    /// <summary>
    /// Converts a raw FFmpeg PTS value in stream time base to a <see cref="TimeSpan"/>.
    /// Returns <see cref="TimeSpan.Zero"/> for <c>AV_NOPTS_VALUE</c>.
    /// </summary>
    private TimeSpan ComputePresentationTime(long pts)
    {
        if (pts == FFAvUtil.AvNoPtsValue || _timeBaseDen == 0)
            return TimeSpan.Zero;

        long microseconds = pts * (long)_timeBaseNum * FFAvUtil.AvTimeBase / _timeBaseDen;
        return TimeSpan.FromMicroseconds(microseconds);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_codecSync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

            // Release anyone awaiting first-frame readiness — the decoder is
            // going away, so the warmup contract can no longer be fulfilled.
            // No-op if the first frame already landed.
            _firstFrameDecodedTcs.TrySetException(
                new ObjectDisposedException(nameof(VideoDecoder))
            );

            // Complete the packet queue so no new writers can enqueue, then drain any
            // packets that were queued but never consumed by DecodeAsync (e.g. on
            // cancellation). Each clone was allocated by DecodingPipeline and must be
            // freed exactly once; DecodeAsync frees them in its finally block, but if
            // it exited early the packets are still alive in the channel.
            _packetQueue.Writer.TryComplete();
            while (_packetQueue.Reader.TryRead(out var item))
            {
                if (!item.isFlush && item.packetPtr != nint.Zero)
                {
                    var ptr = item.packetPtr;
                    FFAvCodec.av_packet_free(ref ptr);
                }
            }

            if (_pendingRetryPacketPtr != nint.Zero)
            {
                var pendingPtr = _pendingRetryPacketPtr;
                _pendingRetryPacketPtr = nint.Zero;
                FFAvCodec.av_packet_free(ref pendingPtr);
            }

            // ADR-0055: dispose a frame built under the lock but not yet yielded
            // (decoder torn down mid-drain).
            _builtFrame?.Dispose();
            _builtFrame = null;

            _swsCtx?.Dispose();
            _swsSrcWidth = -1;
            _swsSrcHeight = -1;
            _swsSrcFormat = -1;

            // ADR-0033: release hwaccel resources. The codec context owns one
            // ref on the hw_device_ctx — disposing it (via avcodec_free_context)
            // unrefs that one. The decoder still holds the original ref from
            // av_hwdevice_ctx_create which we release explicitly.
            _swFrame?.Dispose();
            _swFrame = null;

            _packet.Dispose();
            _frame.Dispose();
            _codecCtx.Dispose();

            if (_hwDeviceCtxRef != nint.Zero)
            {
                FFAvUtil.av_buffer_unref(ref _hwDeviceCtxRef);
            }
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoDecoder));
    }

    // ── Source-generated log methods (ADR-0010: hot-path decode loop) ────────

    /// <summary>
    /// Surfaces sustained shedding as a rate. The count has always been recorded; nothing
    /// ever said it out loud, so a pipeline dropping a third of its packets read as healthy
    /// in the logs and three separate investigations went looking elsewhere (#143, #145).
    /// </summary>
    private void ReportShedRate()
    {
        lock (_shedGate)
        {
            // Increment inside the gate so the total a report carries cannot be older than one
            // an earlier report already showed. Incrementing outside lets two callers acquire
            // in the opposite order and emit a decreasing cumulative count.
            var total = Interlocked.Increment(ref _packetsDroppedForBackpressure);

            (_shedWindow, var report) = ShedRateAccounting.Observe(
                _shedWindow,
                Stopwatch.GetTimestamp(),
                Stopwatch.Frequency,
                ShedRateAccounting.DefaultReportEvery,
                total
            );

            // Emitted under the gate so the log order matches the order the reports were
            // produced in. Holding a lock across a logger call is normally worth avoiding, but
            // a window closes at most once per report interval and only while shedding, so the
            // contention this can cause is one line every ten seconds on a path that is already
            // dropping packets.
            if (report is { } r)
                LogShedRate(_logger, r.Dropped, r.Seconds, r.PerSecond, r.TotalDropped);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Video packets shed to backpressure: {Dropped} in {Seconds:F1}s "
            + "({PerSecond:F1}/s), {TotalDropped} this session. The demux pump is delivering "
            + "faster than the video chain consumes; sustained shedding leaves gaps in the "
            + "decoded timeline and shows up downstream as a stalling presenter."
    )]
    private static partial void LogShedRate(
        ILogger logger,
        long dropped,
        double seconds,
        double perSecond,
        long totalDropped
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Video decoder opened for stream {StreamIndex}: {Width}x{Height}, codecId={CodecId}"
    )]
    private static partial void LogVideoDecoderOpened(
        ILogger logger,
        int streamIndex,
        int width,
        int height,
        int codecId
    );

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Decoded video frame {Width}x{Height} pts={PtsMs:F1}ms"
    )]
    private static partial void LogVideoFrameDecoded(
        ILogger logger,
        int width,
        int height,
        double ptsMs
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "av_hwframe_transfer_data failed with code {ReturnCode}; dropping frame."
    )]
    private static partial void LogHwTransferFailed(ILogger logger, int returnCode);
}
