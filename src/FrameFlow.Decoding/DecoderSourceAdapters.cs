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
/// shim that owns an enumerator and converts each yielded frame
/// into a refcounted wrapper.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frame ownership.</b> The decoder's
/// <see cref="IAsyncEnumerator{T}"/> yields frames where each
/// frame's refcount is owned by the consumer (per the
/// <see cref="IVideoDecoder"/> contract). The adapter wraps each
/// yielded frame in a <see cref="VideoFrameRef"/> /
/// <see cref="PcmAudioBufferRef"/> that adopts the existing ref.
/// The substrate then disposes the wrapper when the item is
/// terminal-consumed, which disposes the underlying frame's ref.
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
    /// yields <see cref="VideoFrameRef"/> items.
    /// </summary>
    /// <param name="decoder">The video decoder to wrap.</param>
    /// <param name="id">Node id for graph diagnostics.</param>
    public static SourceNode<VideoFrameRef> AsSourceNode(
        this IVideoDecoder decoder,
        string id = "video-decoder"
    )
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(id);

        IAsyncEnumerator<IVideoFrame>? enumerator = null;

        return new SourceNode<VideoFrameRef>(
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
                // VideoFrameRef adopts the ref; substrate disposes the
                // wrapper after the item terminal-consumes.
                return new VideoFrameRef(enumerator.Current);
            },
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
    /// yields <see cref="PcmAudioBufferRef"/> items.
    /// </summary>
    public static SourceNode<PcmAudioBufferRef> AsSourceNode(
        this IAudioDecoder decoder,
        string id = "audio-decoder"
    )
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(id);

        IAsyncEnumerator<PcmAudioBuffer>? enumerator = null;

        return new SourceNode<PcmAudioBufferRef>(
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

                return new PcmAudioBufferRef(enumerator.Current);
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
