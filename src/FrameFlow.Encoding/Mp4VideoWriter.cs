// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Encoding;

/// <summary>
/// The H.264-in-MP4 encoder + muxer terminal (ADR-0040, the concrete consumer
/// is ADR-0052's clip recorder). Composes an <see cref="IVideoEncoder"/> and an
/// <see cref="IMuxer"/> and owns the end-of-stream ordering — flush the encoder,
/// write its tail packets, then write the container trailer — that a correct
/// MP4 requires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a terminal rather than a graph operator.</b> The current
/// <c>FrameFlow.Graph</c> substrate (ADR-0049) has no per-operator
/// end-of-stream hook, yet both the encoder (flush) and the muxer (trailer)
/// need one. This terminal makes the boundary explicit via
/// <see cref="CompleteAsync"/>, mirroring how sinks are finalized by their
/// owner rather than by the pump (ADR-0044). It serves the two real shapes:
/// </para>
/// <list type="bullet">
/// <item><b>Direct.</b> Hand a finite sequence of frames and await completion
/// (<see cref="WriteAsync"/> per frame, then <see cref="CompleteAsync"/>, or the
/// one-call <see cref="RecordAsync"/>). This is exactly what ADR-0052's recorder
/// needs for a clip.</item>
/// <item><b>Graph branch.</b> Compose <see cref="AsSinkNode"/> into a live
/// pipeline; drive it with <c>graph.RunAsync</c>, then call
/// <see cref="CompleteAsync"/> once the run drains.</item>
/// </list>
/// <para>
/// <b>Geometry.</b> The encoder infers coded geometry from the first frame
/// unless <see cref="H264EncoderOptions.Width"/> / <c>Height</c> are set. All
/// frames must share that geometry.
/// </para>
/// <para>
/// <b>Threading.</b> Not thread-safe; a single producer drives the writer.
/// </para>
/// </remarks>
public sealed class Mp4VideoWriter : IAsyncDisposable
{
    private readonly IVideoEncoder _encoder;
    private readonly IMuxer _muxer;

    private bool _muxerStarted;
    private bool _completed;
    private bool _disposed;

    private Mp4VideoWriter(IVideoEncoder encoder, IMuxer muxer)
    {
        _encoder = encoder;
        _muxer = muxer;
    }

    /// <summary>
    /// Creates a writer that encodes H.264 and muxes to the MP4 at
    /// <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Output <c>.mp4</c> file path.</param>
    /// <param name="options">H.264 encoder options, or <see langword="null"/> for defaults.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostics (ADR-0010).</param>
    public static Mp4VideoWriter Create(
        string path,
        H264EncoderOptions? options = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IVideoEncoder encoder = Encoder.H264(options, loggerFactory);
        IMuxer muxer = Muxer.Mp4(path, loggerFactory);
        return new Mp4VideoWriter(encoder, muxer);
    }

    /// <summary>The encoder's static description (ADR-0040 diagnostics).</summary>
    public EncoderInfo EncoderInfo => _encoder.Info;

    /// <summary>
    /// Encodes and writes one frame. Takes ownership of <paramref name="frame"/>
    /// and disposes it after reading its pixels.
    /// </summary>
    public async ValueTask WriteAsync(VideoFrameRef frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        try
        {
            await WriteCoreAsync(frame.Frame, ct).ConfigureAwait(false);
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// Encodes and writes one frame without taking ownership — the caller (or
    /// the graph substrate) disposes it. Used by <see cref="AsSinkNode"/>.
    /// </summary>
    private async ValueTask WriteCoreAsync(IVideoFrame frame, CancellationToken ct)
    {
        if (_completed)
            throw new InvalidOperationException("Cannot write after CompleteAsync.");

        IReadOnlyList<EncodedPacket> packets = _encoder.Encode(frame);
        await EnsureMuxerStartedAsync(ct).ConfigureAwait(false);

        foreach (EncodedPacket packet in packets)
        {
            try
            {
                await _muxer.WriteAsync(packet, ct).ConfigureAwait(false);
            }
            finally
            {
                packet.Dispose();
            }
        }
    }

    private async ValueTask EnsureMuxerStartedAsync(CancellationToken ct)
    {
        if (_muxerStarted || !_encoder.IsOpen)
            return;
        _muxer.AddVideoStream(_encoder);
        await _muxer.StartAsync(ct).ConfigureAwait(false);
        _muxerStarted = true;
    }

    /// <summary>
    /// Flushes the encoder, writes its tail packets, and writes the MP4
    /// trailer. Idempotent. After this returns the file is a complete,
    /// seekable MP4. If no frames were ever written, no output file is
    /// produced.
    /// </summary>
    public async ValueTask CompleteAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            return;
        _completed = true;

        IReadOnlyList<EncodedPacket> tail = _encoder.Flush();

        // Edge case: frames were encoded but the encoder buffered every packet
        // until flush — start the muxer now so the tail has somewhere to go.
        await EnsureMuxerStartedAsync(ct).ConfigureAwait(false);

        foreach (EncodedPacket packet in tail)
        {
            try
            {
                await _muxer.WriteAsync(packet, ct).ConfigureAwait(false);
            }
            finally
            {
                packet.Dispose();
            }
        }

        await _muxer.CompleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exposes this writer as a graph <see cref="SinkNode{T}"/> consuming
    /// <see cref="VideoFrameRef"/>. The substrate owns frame disposal; the
    /// consumer must call <see cref="CompleteAsync"/> after the graph's
    /// <c>RunAsync</c> drains, since the substrate has no per-sink end-of-stream
    /// hook to trigger the encoder flush and MP4 trailer.
    /// </summary>
    /// <param name="id">Node id for graph diagnostics.</param>
    public SinkNode<VideoFrameRef> AsSinkNode(string id = "mp4-writer")
    {
        ArgumentNullException.ThrowIfNull(id);
        return new SinkNode<VideoFrameRef>(
            id,
            async (item, ct) =>
            {
                // Read the frame's pixels and encode; the substrate disposes
                // the wrapper after this body returns (do not dispose here).
                await WriteCoreAsync(item.Frame, ct).ConfigureAwait(false);
            }
        );
    }

    /// <summary>
    /// One-call helper: encode every frame in <paramref name="frames"/> to an
    /// MP4 at <paramref name="path"/> and finalize. Each frame's ownership
    /// transfers to the writer. Returns the output path.
    /// </summary>
    public static async Task<string> RecordAsync(
        string path,
        IAsyncEnumerable<VideoFrameRef> frames,
        H264EncoderOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(frames);
        await using var writer = Create(path, options, loggerFactory);
        await foreach (VideoFrameRef frame in frames.WithCancellation(ct).ConfigureAwait(false))
        {
            await writer.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        await writer.CompleteAsync(ct).ConfigureAwait(false);
        return path;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Best-effort finalization so a forgotten CompleteAsync still leaves a
        // valid MP4 (when frames were written).
        if (!_completed && _muxerStarted)
        {
            try
            {
                await CompleteCoreOnDisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Teardown must not throw.
            }
        }

        await _muxer.DisposeAsync().ConfigureAwait(false);
        _encoder.Dispose();
    }

    private async ValueTask CompleteCoreOnDisposeAsync()
    {
        _completed = true;
        IReadOnlyList<EncodedPacket> tail = _encoder.Flush();
        foreach (EncodedPacket packet in tail)
        {
            try
            {
                await _muxer.WriteAsync(packet, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                packet.Dispose();
            }
        }
        await _muxer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
