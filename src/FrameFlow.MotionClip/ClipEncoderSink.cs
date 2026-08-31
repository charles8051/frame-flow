// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading.Channels;
using FrameFlow.Encoding;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.MotionClip;

/// <summary>
/// Graph sink that consumes <see cref="ClipSegment"/>s and writes each to an
/// H.264 MP4 via <see cref="Mp4VideoWriter"/> (ADR-0040 encoder terminal,
/// ADR-0053). Owns a bounded in-memory queue and a background worker that
/// drains it; the sink-node body just <em>enqueues</em>. Producers — the
/// in-graph sink body, and the disconnect-flush path in <c>CameraTracking</c> —
/// share the same queue, so the encoder is the single sequencing point and
/// nothing else has to know whether the graph is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an internal queue.</b> Awaiting <c>EncodeAsync</c> directly from
/// <c>onSessionEnded</c> coupled camera reconnect to encoder latency: the
/// host can't start its reconnect loop until the callback returns, so a
/// 400-frame flush-encode (≈2.5 s, sometimes much longer) pinned the app
/// in a stuck "Recording" state across the unplug. Decoupling lets the
/// reconnect run immediately while the worker finishes the partial clip.
/// </para>
/// <para>
/// <b>Back-pressure.</b> The queue is bounded (default 4 segments,
/// <see cref="BoundedChannelFullMode.Wait"/>). When the encoder falls behind,
/// the sink-body enqueue blocks, which propagates back through the substrate
/// to the gate. The disconnect-flush enqueue uses the same wait semantics —
/// in the realistic case it's well within capacity and returns instantly.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="IAsyncDisposable.DisposeAsync"/> completes
/// the queue writer and awaits the worker, so a pending flush-on-disconnect
/// segment lands on disk before the app exits. Construct once at app
/// startup; the worker spins up immediately and idles on the channel.
/// </para>
/// </remarks>
public sealed class ClipEncoderSink : IAsyncDisposable
{
    private readonly ClipEncoderOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<ClipSegment> _queue;
    private readonly Task _workerTask;
    private long _clipsSaved;
    private int _disposed;

    public ClipEncoderSink(ClipEncoderOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(options.OutputDirectory);

        // Bounded so a slow encoder can't grow memory without bound. Wait
        // semantics on both writer and reader so the gate naturally back-
        // pressures through the substrate when the encoder is behind.
        _queue = Channel.CreateBounded<ClipSegment>(
            new BoundedChannelOptions(options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        _workerTask = Task.Run(RunWorkerAsync);
    }

    /// <summary>Number of clips successfully written so far.</summary>
    public long ClipsSaved => Interlocked.Read(ref _clipsSaved);

    /// <summary>
    /// Builds the sink node bound to this encoder. The body just enqueues the
    /// incoming segment onto the worker channel and returns; the encode runs
    /// asynchronously, decoupled from the graph's flow control.
    /// </summary>
    public SinkNode<ClipSegment> Build(string id = "clip-encoder") =>
        new(
            id,
            async (segment, ct) => await EnqueueAsync(segment, ct).ConfigureAwait(false)
        );

    /// <summary>
    /// Enqueues <paramref name="segment"/> for encoding. AddRefs the segment so
    /// the worker owns its own reference (the substrate disposes the sink
    /// body's input ref after the body returns). Direct-path callers — the
    /// disconnect-flush in <c>CameraTracking</c> — invoke this and then
    /// dispose their own ref in their <c>finally</c>; either way, the worker
    /// keeps the segment alive until it's encoded.
    /// </summary>
    public async ValueTask EnqueueAsync(ClipSegment segment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // AddRef so the worker has its own ref, independent of whatever the
        // caller does after this method returns. ClipSegment.AddRef returns
        // the same instance (it's IRefCounted-by-refcount, not by wrapping).
        ClipSegment owned = (ClipSegment)segment.AddRef();
        try
        {
            await _queue.Writer.WriteAsync(owned, ct).ConfigureAwait(false);
        }
        catch
        {
            // Enqueue failed (cancellation, channel completed) — restore the
            // refcount so the caller's finally{} doesn't leak the frames.
            owned.Dispose();
            throw;
        }
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (
                ClipSegment segment in _queue.Reader.ReadAllAsync().ConfigureAwait(false)
            )
            {
                try
                {
                    await EncodeAsync(segment, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogWorkerEncodeFaulted(_logger, ex);
                }
                finally
                {
                    segment.Dispose(); // release the worker's ref
                }
            }
        }
        catch (Exception ex)
        {
            LogWorkerLoopFaulted(_logger, ex);
        }
    }

    /// <summary>
    /// Encodes a single segment to disk. Called only by the worker; not a
    /// hot path for callers. Public-internal so tests / direct-mode tooling
    /// can drive it synchronously if needed.
    /// </summary>
    internal async Task EncodeAsync(ClipSegment segment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_clip.mp4";
        var path = Path.Combine(_options.OutputDirectory, fileName);
        var encoderOptions = new H264EncoderOptions
        {
            FrameRateNumerator = _options.FrameRate,
            FrameRateDenominator = 1,
            BitRate = _options.BitRate,
        };

        IReadOnlyList<IVideoFrame> frames = segment.Frames;
        int frameCount = frames.Count;
        var refs = new List<VideoFrameRef>(frameCount);
        foreach (IVideoFrame f in frames)
            refs.Add(new VideoFrameRef(f));

        LogEncodeStarted(_logger, path, frameCount, segment.Reason.ToString(), null);
        try
        {
            await using var writer = Mp4VideoWriter.Create(path, encoderOptions);
            int wrote = 0;
            foreach (VideoFrameRef r in refs)
            {
                await writer.WriteAsync(r, ct).ConfigureAwait(false);
                wrote++;
                // Periodic progress trace so a hang/slow encode lands a
                // clear waypoint in the log instead of going dark.
                if (wrote % 120 == 0)
                    LogEncodeProgress(_logger, wrote, frameCount, null);
            }
            LogEncodeWritesDone(_logger, wrote, null);
            await writer.CompleteAsync(ct).ConfigureAwait(false);
            LogEncodeMuxerCompleted(_logger, null);

            Interlocked.Increment(ref _clipsSaved);
            LogClipSaved(_logger, path, frameCount, segment.Reason.ToString(), null);
        }
        catch (Exception ex)
        {
            LogClipSaveFailed(_logger, path, ex);
            throw;
        }
        finally
        {
            // VideoFrameRef.Dispose is idempotent; the writer disposes each
            // ref it consumes, so this sweep cleans up any tail that wasn't
            // written on the error path and is a no-op for the rest.
            foreach (VideoFrameRef r in refs)
                r.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Stop accepting new segments; the worker drains what's already
        // queued (in particular, the disconnect-flush segment) before its
        // ReadAllAsync loop exits.
        _queue.Writer.TryComplete();
        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogWorkerJoinThrew(_logger, ex);
        }
    }

    // ── Logging ───────────────────────────────────────────────────────

    private static readonly Action<ILogger, string, int, string, Exception?> LogEncodeStarted =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogEncodeStarted)),
            "Clip encode starting: {Path} ({FrameCount} frames, reason={Reason})"
        );

    private static readonly Action<ILogger, int, int, Exception?> LogEncodeProgress =
        LoggerMessage.Define<int, int>(
            LogLevel.Debug,
            new EventId(2, nameof(LogEncodeProgress)),
            "Clip encode progress: {Wrote}/{Total} frames"
        );

    private static readonly Action<ILogger, int, Exception?> LogEncodeWritesDone =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(3, nameof(LogEncodeWritesDone)),
            "Clip encode: all {Wrote} frames written; finalising muxer"
        );

    private static readonly Action<ILogger, Exception?> LogEncodeMuxerCompleted =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(4, nameof(LogEncodeMuxerCompleted)),
            "Clip encode: muxer trailer written"
        );

    private static readonly Action<ILogger, string, int, string, Exception?> LogClipSaved =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Information,
            new EventId(5, nameof(LogClipSaved)),
            "Clip saved: {Path} ({FrameCount} frames, reason={Reason})"
        );

    private static readonly Action<ILogger, string, Exception?> LogClipSaveFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(6, nameof(LogClipSaveFailed)),
            "Clip save failed: {Path}"
        );

    private static readonly Action<ILogger, Exception?> LogWorkerEncodeFaulted =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(7, nameof(LogWorkerEncodeFaulted)),
            "Encoder worker: single-segment encode faulted; continuing."
        );

    private static readonly Action<ILogger, Exception?> LogWorkerLoopFaulted =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(8, nameof(LogWorkerLoopFaulted)),
            "Encoder worker loop faulted; encoder is now offline."
        );

    private static readonly Action<ILogger, Exception?> LogWorkerJoinThrew =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(9, nameof(LogWorkerJoinThrew)),
            "Encoder worker join threw during shutdown."
        );
}

/// <summary>Configuration for <see cref="ClipEncoderSink"/>.</summary>
public sealed record ClipEncoderOptions
{
    public required string OutputDirectory { get; init; }
    public required int FrameRate { get; init; }
    public int BitRate { get; init; } = 2_000_000;

    /// <summary>
    /// Maximum pending segments in the encoder's internal queue. Each entry
    /// can hold a full clip's worth of frames (default ≤30 s × fps × bgra
    /// CPU clones ≈ a few hundred MB at 720p), so keep this small. Default 4.
    /// </summary>
    public int QueueCapacity { get; init; } = 4;
}
