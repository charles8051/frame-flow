// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
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
/// Decodes compressed audio packets into normalised stereo S16 PCM blocks.
/// </summary>
/// <remarks>
/// <para>
/// This decoder implements the FFmpeg send-packet / receive-frame loop.  It owns an
/// <c>AVCodecContext</c>, a reusable <c>AVFrame</c>, a reusable <c>AVPacket</c>, and an
/// <c>SwrContext</c> for resampling to the normalised output format.
/// </para>
/// <para>
/// Output format contract:
/// <list type="bullet">
///   <item>Sample format: signed 16-bit interleaved (AV_SAMPLE_FMT_S16)</item>
///   <item>Channel count: 2 (stereo)</item>
///   <item>Sample rate: configurable via <see cref="AudioDecoderOptions"/>; defaults to 48 000 Hz</item>
///   <item>PTS: normalised to <see cref="TimeSpan"/> using the stream time base; synthesised when
///   AV_NOPTS_VALUE is encountered so the playback layer always receives a valid timestamp</item>
/// </list>
/// </para>
/// <para>
/// Ownership contract (ADR-0005, ADR-0012):
/// All FFmpeg native resources are allocated by this class and freed during <see cref="DisposeAsync"/>.
/// Each yielded <see cref="PcmAudioBuffer"/> owns its audio buffer via <see cref="IMemoryOwner{T}"/>
/// (rented from <see cref="MemoryPool{T}.Shared"/>). Ownership of each block transfers to the
/// caller on yield; the caller must dispose each block after consumption.
/// </para>
/// <para>
/// Threading (ADR-0009): designed to run on a single dedicated decode worker task.
/// All native resource access is single-threaded; do not call methods concurrently.
/// </para>
/// <para>
/// Cancellation (ADR-0013): <see cref="DecodeAsync"/> observes the supplied token at every
/// iteration. <see cref="OperationCanceledException"/> from token cancellation is not
/// re-thrown; it terminates the sequence cleanly.
/// </para>
/// </remarks>
public sealed partial class AudioDecoder : IAudioDecoder, IDecodeCodec<PcmAudioBuffer>
{
    // -----------------------------------------------------------------------
    // Native resource handles — all owned exclusively by this instance.
    // -----------------------------------------------------------------------

    private readonly CodecContextHandle _codecCtx;
    private readonly FrameHandle _frame;
    private readonly PacketHandle _packet;
    private readonly SwrContextHandle _swrCtx;

    private readonly ILogger _logger;

    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    private readonly int _targetSampleRate;
    private const int TargetChannels = 2; // always stereo output

    // Stream time base copied at construction for PTS rescaling.
    private readonly int _timeBaseNum;
    private readonly int _timeBaseDen;

    // Internal packet queue — DecodeAsync drains this. Bounded; backpressure is
    // applied to senders once full (see AudioDecoderOptions.PacketQueueCapacity).
    // Created in the constructor and recreated by ResetPacketQueue so the depth
    // always reflects the configured capacity.
    private readonly int _packetQueueCapacity;
    private Channel<(nint packetPtr, bool isFlush)> _packetQueue;

    // ADR-0055 follow-up: PTS synthesis is a pure fold (AudioPtsSynthesis); this is the
    // threaded accumulator value (cumulative output samples), reset on Flush.
    private PtsSynthesisState _ptsSynthesis = PtsSynthesisState.Initial;

    // ADR-0034: diagnostics counters. Single-writer decode loop; Interlocked
    // suffices for cross-thread snapshot reads.
    private long _buffersDecoded;

    // _decodeErrors is exposed via AudioDecoderDiagnosticsSnapshot.DecodeErrors
    // but not yet incremented anywhere — the decode loop currently propagates
    // exceptions rather than counting them. Kept as a reserved counter so the
    // diagnostics record shape stays stable; will be wired when ADR-0034
    // failure-mode telemetry lands.
#pragma warning disable CS0649
    private long _decodeErrors;
#pragma warning restore CS0649

    private int _usedSyntheticPts; // bool encoded as int (latches once set)

    // SWR is allocated at construction but configured and initialised lazily on the first
    // decoded frame (ADR-0012). Many codecs (AAC, MP3, Opus, FLAC) do not set
    // AVCodecParameters.format until after decoding begins; reading it at open time yields
    // AV_SAMPLE_FMT_U8 (0) which causes swr_init to fail with EINVAL (-22).
    private bool _swrInitialized;

    // ADR-0055: the in-flight input packet (a clone owned by DecodingPipeline) while it is
    // being fed to the codec. Held across a send-EAGAIN so the shared DecodeDriver can
    // re-send the SAME packet rather than drop it; freed once the codec accepts it (see the
    // IDecodeCodec.SendCurrentInput implementation) and on reset/dispose.
    private nint _currentInputPtr;

    // True once the input stream is exhausted or a flush sentinel is read; makes
    // SendCurrentInput send a null flush packet. Cleared at the start of each DecodeAsync.
    private bool _inFlushMode;

    private bool _disposed;

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initialises the audio decoder for the specified stream.
    /// </summary>
    /// <param name="formatCtxPtr">
    /// Raw value of the <c>AVFormatContext*</c> pointer owned by the demux session.
    /// This value is read during construction only; the decoder does not store it.
    /// </param>
    /// <param name="streamIndex">
    /// Index of the audio stream within the format context.
    /// </param>
    /// <param name="options">
    /// Output configuration. When null the defaults from <see cref="AudioDecoderOptions"/>
    /// are used.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the codec is not found, the context cannot be opened, or the resampler
    /// cannot be initialised.
    /// </exception>
    public unsafe AudioDecoder(
        nint formatCtxPtr,
        int streamIndex,
        AudioDecoderOptions? options = null,
        ILogger? logger = null
    )
    {
        _logger = logger ?? NullLogger.Instance;
        _targetSampleRate = options?.TargetSampleRate ?? 48_000;

        _packetQueueCapacity = options?.PacketQueueCapacity ?? 512;
        if (_packetQueueCapacity < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _packetQueueCapacity,
                $"{nameof(AudioDecoderOptions.PacketQueueCapacity)} must be at least 1."
            );
        _packetQueue = CreatePacketQueue();

        // ------------------------------------------------------------------
        // Locate the AVStream → AVCodecParameters for this stream
        // ------------------------------------------------------------------
        ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)formatCtxPtr);
        AVStream* streamPtr = fmtCtx.streams[streamIndex];
        AVCodecParameters* codecPar = streamPtr->codecpar;

        int codecId = (int)codecPar->codec_id;

        // Stream time base for PTS rescaling
        _timeBaseNum = streamPtr->time_base.num;
        _timeBaseDen = streamPtr->time_base.den;

        // ------------------------------------------------------------------
        // Find and open the codec
        // ------------------------------------------------------------------
        nint codec = FFAvCodec.avcodec_find_decoder(codecId);
        if (codec == nint.Zero)
            throw new InvalidOperationException(
                $"Audio decoder: no decoder found for codec id {codecId}."
            );

        nint ctx = FFAvCodec.avcodec_alloc_context3(codec);
        if (ctx == nint.Zero)
            throw new InvalidOperationException(
                "Audio decoder: failed to allocate AVCodecContext."
            );
        _codecCtx = new CodecContextHandle(ctx);

        int copyResult = FFAvCodec.avcodec_parameters_to_context(ctx, (nint)codecPar);
        if (copyResult < 0)
            throw new InvalidOperationException(
                $"Audio decoder: avcodec_parameters_to_context failed ({copyResult})."
            );

        int openResult = FFAvCodec.avcodec_open2(ctx, codec, nint.Zero);
        if (openResult < 0)
            throw new InvalidOperationException(
                $"Audio decoder: avcodec_open2 failed ({openResult})."
            );

        // ------------------------------------------------------------------
        // Allocate reusable frame and packet
        // ------------------------------------------------------------------
        nint frame = FFAvUtil.av_frame_alloc();
        if (frame == nint.Zero)
            throw new InvalidOperationException("Audio decoder: failed to allocate AVFrame.");
        _frame = new FrameHandle(frame);

        nint pkt = FFAvCodec.av_packet_alloc();
        if (pkt == nint.Zero)
            throw new InvalidOperationException("Audio decoder: failed to allocate AVPacket.");
        _packet = new PacketHandle(pkt);

        // ------------------------------------------------------------------
        // Allocate the SWR resampler context.
        // Configuration and swr_init are deferred to the first decoded frame
        // because AVCodecParameters.format is not reliably set for many
        // codecs (AAC, MP3, Opus, FLAC) until the decoder produces its first
        // frame. Reading it here often yields AV_SAMPLE_FMT_NONE and causes
        // swr_init to fail with EINVAL (-22). See _swrInitialized and
        // InitializeSwrFromFrame for the deferred path.
        // ------------------------------------------------------------------
        nint swrCtx = FFSwResample.swr_alloc();
        if (swrCtx == nint.Zero)
            throw new InvalidOperationException("Audio decoder: failed to allocate SwrContext.");
        _swrCtx = new SwrContextHandle(swrCtx);
    }

    // -----------------------------------------------------------------------
    // IAudioDecoder implementation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Decodes audio packets and yields normalised stereo S16 PCM blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is the consumer side of the internal packet queue.  Callers must
    /// feed packets via <see cref="SendPacketAsync"/> from a separate producer task.
    /// The sequence completes when the queue is marked complete (via
    /// <see cref="CompletePacketQueue"/>) and all queued packets have been decoded.
    /// </para>
    /// <para>
    /// Each yielded <see cref="PcmAudioBuffer"/> is owned by the caller; it must be
    /// disposed after consumption to return its pooled buffer (ADR-0012).
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<PcmAudioBuffer> DecodeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // ADR-0055: the send/receive sequencing now lives in the shared, FFmpeg-free
        // DecodeProtocol Mealy core, cranked by DecodeDriver. This decoder only supplies
        // the effects (queue read, send, receive, resample) through IDecodeCodec. Clearing
        // the flush latch makes the enumeration replayable across pause/resume sessions.
        _inFlushMode = false;

        await foreach (
            var block in DecodeDriver
                .RunAsync<PcmAudioBuffer>(this, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return block;
        }
    }

    // ── IDecodeCodec<PcmAudioBuffer> — the effect surface DecodeDriver cranks (ADR-0055) ──

    /// <summary>
    /// Reads the next input from the packet queue. Returns <see langword="false"/> on a
    /// flush sentinel or once the queue completes, putting the codec into flush mode so
    /// <see cref="SendCurrentInput"/> sends a null packet.
    /// </summary>
    async ValueTask<bool> IDecodeCodec<PcmAudioBuffer>.TryBeginNextInputAsync(
        CancellationToken cancellationToken
    )
    {
        while (await _packetQueue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_packetQueue.Reader.TryRead(out var item))
            {
                if (item.isFlush)
                {
                    _inFlushMode = true;
                    _currentInputPtr = nint.Zero;
                    return false;
                }

                _currentInputPtr = item.packetPtr;
                return true;
            }
        }

        // Queue completed without an explicit flush sentinel — flush and drain to EOF.
        _inFlushMode = true;
        _currentInputPtr = nint.Zero;
        return false;
    }

    /// <summary>
    /// Sends the current input (or a null flush packet) to the codec. The cloned packet is
    /// freed once the decoder accepts it; on <see cref="CodecReturn.Again"/> it is held so
    /// the driver can re-send the SAME packet — the branch the previous hand-rolled loop
    /// lacked, which would have silently dropped the stalled packet (ADR-0055).
    /// </summary>
    CodecReturn IDecodeCodec<PcmAudioBuffer>.SendCurrentInput()
    {
        nint codecPtr = _codecCtx.DangerousGetHandle();
        nint pktArg = _inFlushMode ? nint.Zero : _currentInputPtr;

        CodecReturn result = DecodeDriver.Classify(
            FFAvCodec.avcodec_send_packet(codecPtr, pktArg)
        );

        if (!_inFlushMode && _currentInputPtr != nint.Zero && result != CodecReturn.Again)
        {
            var ptr = _currentInputPtr;
            FFAvCodec.av_packet_free(ref ptr);
            _currentInputPtr = nint.Zero;
        }

        return result;
    }

    /// <summary>Receives one decoded frame into the reusable <c>AVFrame</c>.</summary>
    CodecReturn IDecodeCodec<PcmAudioBuffer>.ReceiveFrame()
    {
        nint codecPtr = _codecCtx.DangerousGetHandle();
        nint framePtr = _frame.DangerousGetHandle();
        return DecodeDriver.Classify(FFAvCodec.avcodec_receive_frame(codecPtr, framePtr));
    }

    /// <summary>
    /// Resamples the most recently received frame into a stereo S16 <see cref="PcmAudioBuffer"/>,
    /// lazily initialising SWR from the first frame (ADR-0012), and recycles the native frame.
    /// Returns <see langword="null"/> when the resampler yields no samples for this frame.
    /// </summary>
    PcmAudioBuffer? IDecodeCodec<PcmAudioBuffer>.BuildFrame()
    {
        nint framePtr = _frame.DangerousGetHandle();
        nint swrPtr = _swrCtx.DangerousGetHandle();

        // Initialise SWR on the first decoded frame so AVFrame.format is used rather than
        // the unreliable AVCodecParameters.format.
        if (!_swrInitialized)
        {
            unsafe
            {
                InitializeSwrFromFrame(framePtr, swrPtr);
            }
            _swrInitialized = true;
        }

        PcmAudioBuffer? block;
        unsafe
        {
#pragma warning disable CA2000 // Ownership transfers to the caller via the decode enumeration
            block = ResampleFrame(framePtr, swrPtr);
#pragma warning restore CA2000
        }

        // Recycle the decoder's reusable frame for the next receive.
        FFAvUtil.av_frame_unref(framePtr);

        if (block is not null)
        {
            Interlocked.Increment(ref _buffersDecoded);
        }

        return block;
    }

    // -----------------------------------------------------------------------
    // Packet feeding API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Queues a raw audio packet for decoding.
    /// </summary>
    /// <remarks>
    /// The caller retains ownership of the <c>AVPacket</c> pointer; this method does not
    /// unref or free it.  The caller must ensure the pointer remains valid until the packet
    /// has been consumed by <see cref="DecodeAsync"/>.  For <see cref="Channel{T}"/>-based
    /// pipelines, copy the relevant packet data before calling this method if the original
    /// packet is about to be unref'd.
    /// </remarks>
    /// <param name="packetPtr">Raw <c>AVPacket*</c> pointer value to decode.</param>
    /// <param name="cancellationToken">Token to observe while waiting for queue capacity.</param>
    public ValueTask SendPacketAsync(nint packetPtr, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _packetQueue.Writer.WriteAsync((packetPtr, isFlush: false), cancellationToken);
    }

    /// <summary>
    /// Signals end-of-stream, causing the decoder to flush any buffered frames and then
    /// terminate the <see cref="DecodeAsync"/> sequence.
    /// </summary>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _packetQueue.Writer.WriteAsync((nint.Zero, isFlush: true), cancellationToken);
    }

    /// <summary>
    /// Marks the packet queue as complete so that <see cref="DecodeAsync"/> will return
    /// after draining queued packets. Idempotent.
    /// </summary>
    public void CompletePacketQueue() => _packetQueue.Writer.TryComplete();

    /// <inheritdoc/>
    public AudioDecoderDiagnosticsSnapshot GetDiagnostics() =>
        new(
            BuffersDecoded: Interlocked.Read(ref _buffersDecoded),
            DecodeErrors: Interlocked.Read(ref _decodeErrors),
            UsedSyntheticPts: Volatile.Read(ref _usedSyntheticPts) == 1
        );

    /// <summary>
    /// Replaces the completed packet queue with a fresh one so that the decoder
    /// can accept new packets after a pause/resume cycle. Any unread packets in the
    /// old queue are drained and freed.
    /// </summary>
    public void ResetPacketQueue()
    {
        int drained = 0;
        while (_packetQueue.Reader.TryRead(out var item))
        {
            if (!item.isFlush && item.packetPtr != nint.Zero)
            {
                var ptr = item.packetPtr;
                FFAvCodec.av_packet_free(ref ptr);
            }
            drained++;
        }

        // ADR-0055: release any input packet held for a send-retry, and clear the flush
        // latch so the next DecodeAsync starts a fresh session.
        if (_currentInputPtr != nint.Zero)
        {
            var held = _currentInputPtr;
            FFAvCodec.av_packet_free(ref held);
            _currentInputPtr = nint.Zero;
        }

        _inFlushMode = false;

        _packetQueue = CreatePacketQueue();

        LogAudioDecoderResetPacketQueue(_logger, drained);
    }

    /// <summary>
    /// Creates the bounded packet queue at the configured
    /// <see cref="AudioDecoderOptions.PacketQueueCapacity"/>. Used by the
    /// constructor and <see cref="ResetPacketQueue"/> so both paths share the
    /// same depth and channel options.
    /// </summary>
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
    /// Flushes the internal codec buffers. Call after seeking so that stale decoded
    /// state is discarded before new packets arrive.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        nint codecPtr = _codecCtx.DangerousGetHandle();
        FFAvCodec.avcodec_flush_buffers(codecPtr);

        // Belt-and-suspenders residual-frame drain, shared with VideoDecoder.Flush via
        // DecodeDriver (ADR-0055 §Context retired the hand-aligned copies). Verified
        // harmless on FFmpeg 7.x: the immediate root cause of the post-seek stale-PTS frame
        // turned out to be a pre-seek packet retained by DecodingPipeline._pendingPacketPtr,
        // not a codec-buffered frame, but this drain stays as defence-in-depth against
        // future codec quirks.
        nint framePtr = _frame.DangerousGetHandle();
        DecodeDriver.DrainResidualFrames(
            () => FFAvCodec.avcodec_receive_frame(codecPtr, framePtr),
            () => FFAvUtil.av_frame_unref(framePtr)
        );

        _ptsSynthesis = PtsSynthesisState.Initial;
        // Re-arm lazy SWR initialisation in case the post-seek stream differs
        // in format or channel count (e.g. adaptive streaming).
        _swrInitialized = false;
        LogAudioDecoderFlush(_logger);
    }

    // -----------------------------------------------------------------------
    // SWR lazy initialisation — deferred to first decoded frame
    // -----------------------------------------------------------------------

    /// <summary>
    /// Configures and initialises the <c>SwrContext</c> using format information read
    /// from the first decoded <c>AVFrame</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called exactly once, on the first frame produced by <c>avcodec_receive_frame</c>.
    /// <c>AVFrame.format</c> is always correctly populated by the decoder,
    /// whereas <c>AVCodecParameters.format</c> is often 0 (AV_SAMPLE_FMT_NONE)
    /// for demuxed streams (AAC, MP3, Opus, FLAC) because the demuxer does not set it.
    /// </para>
    /// <para>
    /// The channel count is read from <c>AVFrame.ch_layout.nb_channels</c>.
    /// The sample rate is read from <c>AVFrame.sample_rate</c>.
    /// The sample format is read from <c>AVFrame.format</c>.
    /// Field positions are from the <c>FFmpeg.AutoGen.Abstractions</c> 7.1.1 binding.
    /// </para>
    /// <para>
    /// Channel layout is configured via the <c>in_channel_count</c> / <c>out_channel_count</c>
    /// integer options rather than the legacy mask API because the old <c>in_channel_layout</c>
    /// mask option is deprecated and may silently produce incorrect layouts for multi-channel
    /// sources in FFmpeg 7.x. FFmpeg derives the default layout from the channel count.
    /// </para>
    /// </remarks>
    /// <param name="framePtr">
    /// Raw <c>AVFrame*</c> pointer, filled by <c>avcodec_receive_frame</c>.
    /// Must not have been unreffed yet.
    /// </param>
    /// <param name="swrPtr">
    /// Allocated but not yet initialised <c>SwrContext*</c> pointer.
    /// Ownership remains with <see cref="_swrCtx"/>; this method only configures it.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>swr_init</c> returns a negative AVERROR code.
    /// </exception>
    private unsafe void InitializeSwrFromFrame(nint framePtr, nint swrPtr)
    {
        ref AVFrame frame = ref Unsafe.AsRef<AVFrame>((void*)framePtr);

        int sourceSampleFmt = frame.format;
        int sourceSampleRate = frame.sample_rate;
        int sourceChannels = frame.ch_layout.nb_channels;

        // Use the FFmpeg 7.x channel layout string API via av_opt_set.
        // The legacy in_channel_layout mask API is deprecated and swr_init rejects it
        // with EINVAL (-22) in FFmpeg 7.x even when other parameters are correct.
        string inChlayout = sourceChannels switch
        {
            1 => "mono",
            2 => "stereo",
            6 => "5.1",
            8 => "7.1",
            _ => $"{sourceChannels}c",
        };

        FFAvUtil.av_opt_set(swrPtr, "in_chlayout", inChlayout, 0);
        FFAvUtil.av_opt_set_int(swrPtr, "in_sample_rate", sourceSampleRate, 0);
        FFAvUtil.av_opt_set_int(swrPtr, "in_sample_fmt", sourceSampleFmt, 0);

        FFAvUtil.av_opt_set(swrPtr, "out_chlayout", "stereo", 0);
        FFAvUtil.av_opt_set_int(swrPtr, "out_sample_rate", _targetSampleRate, 0);
        FFAvUtil.av_opt_set_int(swrPtr, "out_sample_fmt", FFAvUtil.AvSampleFmtS16, 0);

        int swrInit = FFSwResample.swr_init(swrPtr);
        if (swrInit < 0)
        {
            LogSwrInitFailed(_logger, swrInit, sourceChannels, sourceSampleRate, sourceSampleFmt);
            throw new InvalidOperationException(
                $"Audio decoder: swr_init failed ({swrInit}). "
                    + $"Source: {sourceChannels}ch/{sourceSampleRate}Hz/fmt={sourceSampleFmt}, "
                    + $"Target: {TargetChannels}ch/{_targetSampleRate}Hz/S16."
            );
        }

        LogSwrInitialized(
            _logger,
            sourceChannels,
            sourceSampleRate,
            sourceSampleFmt,
            _targetSampleRate
        );
    }

    // -----------------------------------------------------------------------
    // Resampling — produces PcmAudioBuffer from a decoded AVFrame
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a decoded audio frame to a stereo S16 <see cref="PcmAudioBuffer"/>.
    /// </summary>
    /// <param name="framePtr">
    /// Raw <c>AVFrame*</c> pointer. The frame must have been filled by
    /// <c>avcodec_receive_frame</c> and must not have been unreffed yet.
    /// Ownership of the frame data remains with the decoder; the frame is unreffed
    /// by the caller immediately after this method returns.
    /// </param>
    /// <param name="swrPtr">Initialised <c>SwrContext*</c>.</param>
    /// <returns>
    /// A <see cref="PcmAudioBuffer"/> backed by pooled memory (ADR-0012), or
    /// <see langword="null"/> when the resampler produces no output for this input.
    /// Caller owns the returned block and must dispose it.
    /// </returns>
    private unsafe PcmAudioBuffer? ResampleFrame(nint framePtr, nint swrPtr)
    {
        ref AVFrame frame = ref Unsafe.AsRef<AVFrame>((void*)framePtr);

        int nbInputSamples = frame.nb_samples;

        // Upper bound on output samples including resampler internal latency.
        long delayedSamples = FFSwResample.swr_get_delay(swrPtr, _targetSampleRate);
        int maxOutputSamples = (int)(delayedSamples + nbInputSamples) + 256;

        // Rent a pooled buffer large enough for maxOutputSamples × TargetChannels shorts.
        IMemoryOwner<short> owner = MemoryPool<short>.Shared.Rent(
            maxOutputSamples * TargetChannels
        );
        int actualOutputSamples;

        fixed (short* outBufFixed = owner.Memory.Span)
        {
            nint outBuf = (nint)outBufFixed;

            // extended_data is the byte** plane pointer array that swr_convert expects
            // directly as its input parameter. For interleaved formats extended_data[0]
            // points to the single interleaved buffer. For planar formats extended_data[i]
            // points to channel i's plane buffer.
            //
            // The fallback to framePtr (= &data[0] since data is at offset 0) handles
            // the edge case of a malformed frame where extended_data is null.
            nint inPlanes = (nint)frame.extended_data;
            if (inPlanes == nint.Zero)
                inPlanes = framePtr;

            actualOutputSamples = FFSwResample.swr_convert(
                swrPtr,
                ref outBuf,
                maxOutputSamples,
                inPlanes,
                nbInputSamples
            );
        }

        if (actualOutputSamples < 0)
        {
            owner.Dispose();
            throw new InvalidOperationException(
                $"swr_convert failed with code {actualOutputSamples}."
            );
        }

        if (actualOutputSamples == 0)
        {
            owner.Dispose();
            return null;
        }

        // ------------------------------------------------------------------
        // Normalise PTS → TimeSpan
        // ------------------------------------------------------------------
        long framePts = frame.pts;

        // Prefer the frame's own time_base if the decoder set it; otherwise fall back to
        // the stream time base captured at construction.
        int tbNum = frame.time_base.num;
        int tbDen = frame.time_base.den;
        if (tbNum == 0 || tbDen == 0)
        {
            tbNum = _timeBaseNum;
            tbDen = _timeBaseDen;
        }

        // PTS synthesis is a pure fold (ADR-0055 follow-up): compute this frame's
        // timestamp and advance the accumulator value in one total function.
        bool hasValidPts = framePts != FFAvUtil.AvNoPtsValue && tbDen != 0;
        var ptsStep = AudioPtsSynthesis.Advance(
            _ptsSynthesis,
            hasValidPts,
            framePts,
            tbNum,
            tbDen,
            actualOutputSamples,
            _targetSampleRate
        );
        _ptsSynthesis = ptsStep.State;
        TimeSpan pts = ptsStep.Pts;
        if (ptsStep.UsedSynthetic)
        {
            // ADR-0034: latch the synthetic-PTS diagnostic so the snapshot can tell the
            // consumer the source had missing timestamps.
            Interlocked.Exchange(ref _usedSyntheticPts, 1);
        }

        // sampleCount = output samples per channel × channels (interleaved)
        int sampleCount = actualOutputSamples * TargetChannels;
        return new PcmAudioBuffer(owner, sampleCount, _targetSampleRate, TargetChannels, pts);
    }

    // -----------------------------------------------------------------------
    // Disposal (IAsyncDisposable)
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        LogAudioDecoderDisposing(_logger);

        // Complete the queue so that any pending DecodeAsync iteration terminates,
        // then drain any packets that were queued but never consumed (e.g. on
        // cancellation). Each clone was allocated by DecodingPipeline and must be
        // freed exactly once; DecodeAsync frees them after avcodec_send_packet, but
        // if it exited early the packets are still alive in the channel.
        _packetQueue.Writer.TryComplete();
        while (_packetQueue.Reader.TryRead(out var item))
        {
            if (!item.isFlush && item.packetPtr != nint.Zero)
            {
                var ptr = item.packetPtr;
                FFAvCodec.av_packet_free(ref ptr);
            }
        }

        // ADR-0055: free any input packet held for a pending send-retry.
        if (_currentInputPtr != nint.Zero)
        {
            var held = _currentInputPtr;
            FFAvCodec.av_packet_free(ref held);
            _currentInputPtr = nint.Zero;
        }

        // Release all native resources. Order mirrors reverse of allocation.
        _swrCtx.Dispose();
        _frame.Dispose();
        _packet.Dispose();
        _codecCtx.Dispose();

        return ValueTask.CompletedTask;
    }

    // ── Source-generated log methods (ADR-0010) ─────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "SWR initialized: {SourceChannels}ch/{SourceSampleRate}Hz/fmt={SourceFormat} → 2ch/{TargetSampleRate}Hz/S16"
    )]
    private static partial void LogSwrInitialized(
        ILogger logger,
        int sourceChannels,
        int sourceSampleRate,
        int sourceFormat,
        int targetSampleRate
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "swr_init failed ({ErrorCode}). Source: {SourceChannels}ch/{SourceSampleRate}Hz/fmt={SourceFormat}"
    )]
    private static partial void LogSwrInitFailed(
        ILogger logger,
        int errorCode,
        int sourceChannels,
        int sourceSampleRate,
        int sourceFormat
    );

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audio decoder disposing.")]
    private static partial void LogAudioDecoderDisposing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AudioDecoder flushed.")]
    private static partial void LogAudioDecoderFlush(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AudioDecoder packet queue reset (PacketsDrained={Drained})."
    )]
    private static partial void LogAudioDecoderResetPacketQueue(ILogger logger, int drained);
}
