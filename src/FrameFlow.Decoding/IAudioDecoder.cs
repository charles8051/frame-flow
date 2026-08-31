// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media;

namespace FrameFlow.Decoding;

public interface IAudioDecoder : IAsyncDisposable, ISeekResettable
{
    IAsyncEnumerable<PcmAudioBuffer> DecodeAsync(CancellationToken cancellationToken = default);

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
    /// (ADR-0034). Default returns
    /// <see cref="AudioDecoderDiagnosticsSnapshot.Empty"/>.
    /// </summary>
    AudioDecoderDiagnosticsSnapshot GetDiagnostics() => AudioDecoderDiagnosticsSnapshot.Empty;
}
