// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Graph;

namespace FrameFlow.Decoding;

/// <summary>
/// Adapters that expose <see cref="IVideoDecoder"/> and
/// <see cref="IAudioDecoder"/> as source nodes.
/// The decoders already yield
/// <see cref="IAsyncEnumerable{T}"/>, so the adapter is a thin
/// shim that owns an enumerator and emits each yielded frame or
/// buffer as the graph item itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frame ownership.</b> The decoder's
/// <see cref="IAsyncEnumerator{T}"/> yields frames where each
/// frame's refcount is owned by the consumer (per the
/// <see cref="IVideoDecoder"/> contract). The adapter emits each
/// yielded <see cref="IVideoFrame"/> or <see cref="PcmAudioBuffer"/>
/// with that ref, and the substrate releases it when the item is
/// terminal-consumed (ADR-0080, decision 6).
/// </para>
/// <para>
/// <b>Lifecycle.</b> The enumerator is lazily created on first
/// pull and disposed on EOS or graph-cancellation. If the graph
/// cancels mid-iteration, the adapter catches the
/// <see cref="OperationCanceledException"/> and disposes the
/// enumerator before re-throwing — so native decoder resources
/// don't leak across graph runs.
/// </para>
/// </remarks>
public static class DecoderSourceAdapters
{
    /// <summary>
    /// Wraps an <see cref="IVideoDecoder"/> as a source node that
    /// yields <see cref="IVideoFrame"/> items.
    /// </summary>
    /// <param name="decoder">The video decoder to wrap.</param>
    /// <param name="id">Node id for graph diagnostics.</param>
    public static SourceNode<IVideoFrame> AsSourceNode(
        this IVideoDecoder decoder,
        string id = "video-decoder"
    )
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(id);

        IAsyncEnumerator<IVideoFrame>? enumerator = null;

        return new SourceNode<IVideoFrame>(
            id,
            async (ct) =>
            {
                enumerator ??= decoder.DecodeAsync(ct).GetAsyncEnumerator(ct);

                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    // EOS — dispose the enumerator now (Cleanup will
                    // also try but it's idempotent in practice). Null
                    // it out so Cleanup's second dispose is a no-op.
                    var toDispose = enumerator;
                    enumerator = null;
                    await toDispose.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                // Decoder yields frame with refcount=1 (owned by consumer).
                // The graph takes that ref and releases it after the item
                // terminal-consumes.
                return enumerator.Current;
            },
            // A decoder yielding hardware frames checks what the graph can hold against its
            // pool before each run (ADR-0081).
            onBudget: decoder is VideoDecoder video ? video.CheckFrameBudget : null,
            cleanup: async () =>
            {
                // The substrate calls this once when the source pump
                // exits, regardless of reason (EOS, cancellation,
                // fault). For cancellation specifically, this is the
                // only path that disposes the enumerator — the body's
                // own catch doesn't run if cancellation arrives
                // between body invocations.
                if (enumerator is not null)
                {
                    var toDispose = enumerator;
                    enumerator = null;
                    try { await toDispose.DisposeAsync().ConfigureAwait(false); }
                    catch { /* best-effort cleanup */ }
                }
            }
        );
    }

    /// <summary>
    /// Wraps an <see cref="IAudioDecoder"/> as a source node that
    /// yields <see cref="PcmAudioBuffer"/> items.
    /// </summary>
    public static SourceNode<PcmAudioBuffer> AsSourceNode(
        this IAudioDecoder decoder,
        string id = "audio-decoder"
    )
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(id);

        IAsyncEnumerator<PcmAudioBuffer>? enumerator = null;

        return new SourceNode<PcmAudioBuffer>(
            id,
            async (ct) =>
            {
                enumerator ??= decoder.DecodeAsync(ct).GetAsyncEnumerator(ct);

                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    var toDispose = enumerator;
                    enumerator = null;
                    await toDispose.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                return enumerator.Current;
            },
            cleanup: async () =>
            {
                if (enumerator is not null)
                {
                    var toDispose = enumerator;
                    enumerator = null;
                    try { await toDispose.DisposeAsync().ConfigureAwait(false); }
                    catch { /* best-effort cleanup */ }
                }
            }
        );
    }
}
