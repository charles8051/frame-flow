// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media;

namespace FrameFlow.Decoding;

/// <summary>
/// Decodes video packets into frames. Yields <see cref="IVideoFrame"/> instances
/// whose concrete type depends on the decoder backend (CPU or hardware-accelerated).
/// Ownership of each frame transfers to the caller; the caller must dispose each frame.
/// </summary>
public interface IVideoDecoder : IAsyncDisposable, ISeekResettable
{
    IAsyncEnumerable<IVideoFrame> DecodeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the internal packet queue with a fresh one so that the decoder
    /// can accept new packets after a pause/resume cycle.
    /// </summary>
    void ResetPacketQueue();

    /// <summary>
    /// Flushes the codec's internal decode buffers after a seek.
    /// </summary>
    void Flush();

    /// <summary>
    /// Default seek reset (ADR-0056): replace the packet queue, then flush the codec
    /// buffers. Tolerates a concurrent dispose. Concrete decoders inherit this.
    /// </summary>
    void ISeekResettable.ResetForSeek()
    {
        ResetPacketQueue();
        try
        {
            Flush();
        }
        catch (ObjectDisposedException)
        {
            // Raced a concurrent dispose; the decoder is going away — nothing to flush.
        }
    }

    /// <summary>
    /// Sends a flush signal to the decoder's packet queue, causing the codec to
    /// drain any remaining buffered frames before the queue completes.
    /// </summary>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the packet queue writer so that <see cref="DecodeAsync"/> terminates
    /// once all queued packets have been consumed.
    /// </summary>
    void CompletePacketQueue();

    /// <summary>
    /// Returns a coherent snapshot of the decoder's observable state
    /// (ADR-0034). Default implementation returns
    /// <see cref="VideoDecoderDiagnosticsSnapshot.Empty"/>; concrete decoders
    /// override to surface real counters and the bound hardware backend.
    /// </summary>
    VideoDecoderDiagnosticsSnapshot GetDiagnostics() => VideoDecoderDiagnosticsSnapshot.Empty;

    /// <summary>
    /// Completes when the decoder has produced its first frame after construction.
    /// Allows callers to absorb hardware-decoder cold-start latency before opening
    /// downstream gates, so audio doesn't run ahead of video on a fresh start.
    /// </summary>
    /// <remarks>
    /// Faults with <see cref="ObjectDisposedException"/> if the decoder is disposed
    /// before producing a frame. Default implementation returns
    /// <see cref="Task.CompletedTask"/> for decoders that don't need warmup.
    /// </remarks>
    Task FirstFrameDecoded => Task.CompletedTask;
}
