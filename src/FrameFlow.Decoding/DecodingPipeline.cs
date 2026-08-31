// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading.Channels;
using FrameFlow.Decoding.Internal;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding;

/// <summary>
/// Orchestrates packet reading from a <see cref="DemuxSession"/> and routes packets
/// to video and/or audio decoders. This class owns the demux read loop and ensures
/// packets are delivered to the correct decoder by stream index.
/// </summary>
/// <remarks>
/// <para>
/// The PlaybackSession uses this class instead of calling decoder APIs directly,
/// because both the <see cref="VideoDecoder"/> and <see cref="AudioDecoder"/> need
/// native <c>AVPacket*</c> pointers that only exist within the native demux layer.
/// </para>
/// <para>
/// Threading: the demux pump, video decode, and audio decode run on separate tasks.
/// The demux pump reads packets sequentially from a single <c>AVFormatContext</c>
/// (per ADR-0009), then routes each packet to the appropriate decoder via its
/// internal queue. The decoders drain their queues independently.
/// </para>
/// </remarks>
public sealed partial class DecodingPipeline : IAsyncDisposable, ISeekResettable
{
    private readonly DemuxSession _demuxSession;
    private readonly VideoDecoder? _videoDecoder;
    private readonly AudioDecoder? _audioDecoder;
    private readonly ILogger _logger;
    private readonly int _videoStreamIndex;
    private readonly int _audioStreamIndex;
    private nint _pendingPacketPtr;
    private int _pendingPacketStreamIndex = -1;
    private bool _disposed;

    /// <summary>
    /// Creates a decoding pipeline for the given demux session and decoders.
    /// </summary>
    public DecodingPipeline(
        DemuxSession demuxSession,
        VideoDecoder? videoDecoder,
        AudioDecoder? audioDecoder,
        ILogger? logger = null
    )
    {
        _demuxSession = demuxSession ?? throw new ArgumentNullException(nameof(demuxSession));
        _videoDecoder = videoDecoder;
        _audioDecoder = audioDecoder;
        _logger = logger ?? NullLogger.Instance;

        _videoStreamIndex = demuxSession.MediaInfo.VideoStreams.FirstOrDefault()?.StreamIndex ?? -1;
        _audioStreamIndex = demuxSession.MediaInfo.AudioStreams.FirstOrDefault()?.StreamIndex ?? -1;
    }

    /// <summary>
    /// Read-only access to the underlying demux session, exposed so the
    /// playback layer can roll up its diagnostics (ADR-0034). Do not call
    /// mutating operations through this accessor.
    /// </summary>
    public IDemuxSession DemuxSession => _demuxSession;

    /// <summary>
    /// Runs the demux pump: reads packets from the format context and routes them to
    /// the video and audio decoders by stream index. Returns when natural EOF is
    /// reached or the <paramref name="cancellationToken"/> is cancelled.
    /// Non-EOF demux failures and malformed native return codes fault the pump.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method does <b>not</b> finalize decoder queues. The caller is responsible
    /// for calling <see cref="FinalizeDecodersAsync"/> after the pump exits to flush
    /// and complete the decoder queues, or for managing decoder queue lifecycle
    /// directly (e.g., in the long-lived worker model per ADR-0022).
    /// </para>
    /// <para>
    /// Call this from a dedicated Task. It runs until EOF or cancellation.
    /// </para>
    /// </remarks>
    public async Task RunDemuxPumpAsync(CancellationToken cancellationToken)
    {
        var fmtCtx = _demuxSession.FormatContextPtr;

        // Allocate a reusable packet for reading.
        nint packetPtr = FFAvCodec.av_packet_alloc();
        if (packetPtr == nint.Zero)
            throw new InvalidOperationException("Failed to allocate AVPacket for demux pump.");

        LogDemuxPumpStarted(_logger, _videoStreamIndex, _audioStreamIndex);

        // The pure routing/sequencing core (DemuxPump) decides every branch below; this
        // method is the thin imperative shell that performs the native read, the clone+queue,
        // the discard, and the EOF/fault effect each action names — and owns await,
        // cancellation, and the native pointer it can never hand to the core (ADR-0005).
        // Seed the phase from whether a packet was retained on a prior (pre-resume) run: a
        // held packet is delivered before any further reads. The native clone itself lives in
        // the shell's _pendingPacketPtr field across runs (it can never enter the pure core,
        // ADR-0005); the phase is re-derived from it via the same Retain transition the
        // cancellation path models.
        var state = _pendingPacketPtr != nint.Zero
            ? DemuxPump.Retain(DemuxPumpState.Initial)
            : DemuxPumpState.Initial;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var step = DemuxPump.Step(state);

                if (step.Action == DemuxPumpAction.DeliverPending)
                {
                    // SendPendingPacketAsync re-retains the clone (leaving _pendingPacketPtr set)
                    // and rethrows if its queue write is interrupted; reaching the next line means
                    // the pending packet was delivered.
                    await SendPendingPacketAsync(cancellationToken).ConfigureAwait(false);
                    state = DemuxPump.PendingDelivered(state);
                    continue;
                }

                // step.Action == DemuxPumpAction.ReadNext
                int readRet = FFAvFormat.av_read_frame(fmtCtx, packetPtr);
                var readKind = ClassifyDemuxReadResult(readRet);

                int streamIndex = -1;
                if (readKind == DemuxReadResultKind.PacketAvailable)
                {
                    var pkt = new AvPacketAccessor(packetPtr);
                    streamIndex = pkt.StreamIndex;

                    // ADR-0034: bump the demux session's packet/bytes counters. The
                    // pump reads directly from the format context for performance,
                    // so the session's GetDiagnostics counters wouldn't otherwise see
                    // this traffic. Bump before routing so a packet that is about to be
                    // discarded (no consumer) is still counted, matching prior behaviour.
                    _demuxSession.RecordPacketRead(pkt.Size);
                }

                var outcome = DemuxPump.Route(
                    readKind,
                    streamIndex,
                    _videoStreamIndex,
                    _audioStreamIndex,
                    _videoDecoder is not null,
                    _audioDecoder is not null
                );

                var transition = DemuxPump.Advance(state, outcome);
                state = transition.State;

                switch (transition.Action)
                {
                    case DemuxPumpAction.RouteToVideo:
                    case DemuxPumpAction.RouteToAudio:
                        // Clone the packet — the decoder queue is async, so the decoder
                        // will read it later. The pump reuses packetPtr for the next read.
                        nint clone = FFAvCodec.av_packet_alloc();
                        FFAvCodec.av_packet_ref(clone, packetPtr);
                        await QueueClonedPacketAsync(clone, streamIndex, cancellationToken)
                            .ConfigureAwait(false);
                        FFAvCodec.av_packet_unref(packetPtr);
                        break;

                    case DemuxPumpAction.DiscardUnselected:
                        // No consumer for this stream (ADR-0059): free the read buffer, read on.
                        FFAvCodec.av_packet_unref(packetPtr);
                        break;

                    case DemuxPumpAction.Complete:
                        // ADR-0034: latch EOF for the session's diagnostics surface.
                        _demuxSession.RecordEndOfStream();
                        LogDemuxPumpEof(_logger);
                        return;

                    case DemuxPumpAction.FaultRead:
                        if (readKind == DemuxReadResultKind.Malformed)
                        {
                            LogDemuxPumpUnexpectedReadResult(_logger, readRet);
                        }
                        else
                        {
                            LogDemuxPumpReadFault(_logger, readRet);
                        }
                        throw CreateDemuxReadFailureException(readRet, readKind);

                    default:
                        throw new InvalidOperationException(
                            $"Unhandled demux pump action {transition.Action} from a read step."
                        );
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal exit
        }
        finally
        {
            FFAvCodec.av_packet_free(ref packetPtr);
        }
    }

    internal enum DemuxReadResultKind
    {
        PacketAvailable = 0,
        EndOfStream,
        Fault,
        Malformed,
    }

    internal static int EndOfStreamReadCode => FFAvUtil.AvErrorEof;

    internal static DemuxReadResultKind ClassifyDemuxReadResult(int readResult)
    {
        if (readResult == 0)
        {
            return DemuxReadResultKind.PacketAvailable;
        }

        if (readResult == FFAvUtil.AvErrorEof)
        {
            return DemuxReadResultKind.EndOfStream;
        }

        if (readResult < 0)
        {
            return DemuxReadResultKind.Fault;
        }

        return DemuxReadResultKind.Malformed;
    }

    internal static InvalidOperationException CreateDemuxReadFailureException(
        int readResult,
        DemuxReadResultKind readOutcome
    ) =>
        readOutcome switch
        {
            DemuxReadResultKind.Fault => new InvalidOperationException(
                $"Demux pump read failed with FFmpeg error code {readResult}."
            ),
            DemuxReadResultKind.Malformed => new InvalidOperationException(
                $"Demux pump received unexpected av_read_frame result {readResult}. Expected 0 for a packet, {FFAvUtil.AvErrorEof} for EOF, or a negative FFmpeg error code."
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(readOutcome),
                readOutcome,
                "A demux read failure exception can only be created for fault or malformed outcomes."
            ),
        };

    /// <summary>
    /// Flushes both decoders (draining remaining buffered frames) and then completes
    /// their packet queues so that <c>DecodeAsync</c> enumerations terminate.
    /// Call this after <see cref="RunDemuxPumpAsync"/> returns for normal EOF handling.
    /// </summary>
    public async Task FinalizeDecodersAsync(CancellationToken cancellationToken = default)
    {
        if (_videoDecoder is not null)
        {
            try
            {
                await _videoDecoder.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            _videoDecoder.CompletePacketQueue();
        }

        if (_audioDecoder is not null)
        {
            try
            {
                await _audioDecoder.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            _audioDecoder.CompletePacketQueue();
        }
    }

    /// <summary>
    /// Drops any packet the demux pump stashed for redelivery after a
    /// cancellation. Call this between pump runs when the source position
    /// has changed (i.e. after a seek) — otherwise the next pump run
    /// will deliver the pre-seek packet to the decoder before any
    /// post-seek packets, producing a stale-PTS frame at the head of the
    /// post-seek stream. Safe to call when no packet is pending (no-op).
    /// </summary>
    /// <remarks>
    /// The retention behaviour itself is correct for pause/resume — we
    /// don't want to lose work the pump already did. It's only seek that
    /// invalidates the retained packet, because the new pump will start
    /// reading from the seeked file position and the retained packet
    /// belongs to the pre-seek timeline.
    /// </remarks>
    public void DiscardPendingPacket()
    {
        // Drive the drop/no-op decision through the pure core's Seek transition: the phase is
        // re-derived from whether the native clone field is set (the pointer lives only in the
        // shell, ADR-0005). DropPending => free the retained clone; None => nothing was held.
        var phase =
            _pendingPacketPtr != nint.Zero ? DemuxPumpPhase.HavePending : DemuxPumpPhase.NeedRead;
        var transition = DemuxPump.Seek(new DemuxPumpState(phase));

        if (transition.Action == DemuxPumpAction.DropPending)
        {
            LogDiscardPendingPacket(_logger, _pendingPacketStreamIndex);
            FFAvCodec.av_packet_free(ref _pendingPacketPtr);
            _pendingPacketStreamIndex = -1;
        }
    }

    /// <summary>
    /// <see cref="ISeekResettable"/> implementation (ADR-0056): on a seek, drop the pump's
    /// retained pre-seek packet so it is not delivered as the head of the post-seek stream.
    /// </summary>
    public void ResetForSeek() => DiscardPendingPacket();

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        if (_pendingPacketPtr != nint.Zero)
        {
            FFAvCodec.av_packet_free(ref _pendingPacketPtr);
            _pendingPacketStreamIndex = -1;
        }

        // Decoders and demux session are owned by the caller (PlaybackSession).
        return ValueTask.CompletedTask;
    }

    private async Task QueueClonedPacketAsync(
        nint clone,
        int streamIndex,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (streamIndex == _videoStreamIndex && _videoDecoder is not null)
            {
                await _videoDecoder.SendPacketAsync(clone, cancellationToken).ConfigureAwait(false);
            }
            else if (streamIndex == _audioStreamIndex && _audioDecoder is not null)
            {
                await _audioDecoder.SendPacketAsync(clone, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                FFAvCodec.av_packet_free(ref clone);
                return;
            }

            clone = nint.Zero;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pendingPacketPtr = clone;
            _pendingPacketStreamIndex = streamIndex;
            clone = nint.Zero;
            throw;
        }
        finally
        {
            if (clone != nint.Zero)
            {
                FFAvCodec.av_packet_free(ref clone);
            }
        }
    }

    private async Task SendPendingPacketAsync(CancellationToken cancellationToken)
    {
        if (_pendingPacketPtr == nint.Zero)
        {
            return;
        }

        var pendingPacketPtr = _pendingPacketPtr;
        var pendingStreamIndex = _pendingPacketStreamIndex;
        _pendingPacketPtr = nint.Zero;
        _pendingPacketStreamIndex = -1;

        await QueueClonedPacketAsync(pendingPacketPtr, pendingStreamIndex, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Source-generated log methods (ADR-0010: hot-path packet pump) ────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Demux pump started. VideoStreamIndex={VideoStreamIndex}, AudioStreamIndex={AudioStreamIndex}"
    )]
    private static partial void LogDemuxPumpStarted(
        ILogger logger,
        int videoStreamIndex,
        int audioStreamIndex
    );

    [LoggerMessage(Level = LogLevel.Debug, Message = "Demux pump reached end of stream.")]
    private static partial void LogDemuxPumpEof(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Demux pump read fault. ErrorCode={ErrorCode}"
    )]
    private static partial void LogDemuxPumpReadFault(ILogger logger, int errorCode);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Demux pump received unexpected av_read_frame result {ReadResult}."
    )]
    private static partial void LogDemuxPumpUnexpectedReadResult(ILogger logger, int readResult);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Demux pump pending packet discarded (streamIndex={StreamIndex}). Pre-seek retained packet dropped to prevent stale-PTS frame at head of post-seek stream."
    )]
    private static partial void LogDiscardPendingPacket(ILogger logger, int streamIndex);
}
