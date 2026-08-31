// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// Adapters that expose FrameFlow's <see cref="IVideoSink"/> and
/// <see cref="IAudioSink"/> implementations as substrate
/// <see cref="SinkNode{TIn}"/>s. The production sink classes don't
/// change — these adapters bridge the data plane from the substrate's
/// <see cref="VideoFrameRef"/> / <see cref="PcmAudioBufferRef"/>
/// wrappers into the sink's <c>PresentAsync</c> method.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are extension methods on the interface, not per
/// concrete sink.</b> Every <see cref="IVideoSink"/> declares
/// <see cref="IVideoSink.PresentAsync"/>; same for
/// <see cref="IAudioSink"/>. The adapter doesn't need to know whether
/// the underlying sink is Avalonia, SDL, OpenAL, or a test capturing
/// sink — it hands the payload to <c>PresentAsync</c> and lets the
/// sink do the rest. One pair of adapters covers every current and
/// future sink implementation.
/// </para>
/// <para>
/// <b>Why the bodies share a private helper.</b> The two adapters
/// differ only in their wrapper and payload types. The behaviour they
/// share — detach, null-guard, present — lives once in
/// <see cref="Adapt{TRef, TPayload}"/>. The public overloads stay
/// separate so call sites keep type inference:
/// <c>videoSink.AsSinkNode()</c> and <c>audioSink.AsSinkNode()</c>
/// need no explicit type arguments. A single public generic would
/// require two type parameters, and C# cannot partially infer them,
/// so every call site would have to spell both out.
/// </para>
/// <para>
/// <b>Lifecycle stays on the original sink.</b> Callers still
/// construct, activate, dispose, and (for audio) wire as
/// <see cref="IClockSource"/> the underlying sink themselves. The
/// adapter is purely a data-plane shim.
/// </para>
/// <para>
/// <b>Ref-counting bridge — ownership transfer.</b> The substrate
/// hands the adapter a refcounted wrapper (one ref). The sink
/// contract (ADR-0044) is "the sink takes ownership of the item and
/// is responsible for disposing it after presenting." The adapter
/// <c>Detach</c>es the inner payload from the wrapper and hands it to
/// the sink. That transfers ownership without invoking
/// <see cref="IVideoFrame.AddRef"/> (which one-shot decoder frames
/// and converter outputs reject), and the substrate's subsequent
/// wrapper-dispose becomes a no-op because the wrapper's slot is
/// null. The sink therefore owns the only outstanding reference and
/// disposes it as it always has. Works uniformly for one-shot
/// <c>CpuVideoFrame</c> (decoder output), pooled
/// <c>PooledCpuVideoFrame</c>, and <c>GpuVideoFrame</c>.
/// </para>
/// </remarks>
public static class SinkAdapters
{
    /// <summary>
    /// Wraps any <see cref="IVideoSink"/> as a
    /// <see cref="SinkNode{TIn}"/> over <see cref="VideoFrameRef"/>
    /// for graph wiring. Works uniformly with <c>AvaloniaVideoSink</c>,
    /// <c>SdlVideoSink</c>, <c>NullVideoSink</c>, test capturing sinks,
    /// etc.
    /// </summary>
    /// <param name="sink">The existing video sink to wrap.</param>
    /// <param name="id">Node id for graph diagnostics.</param>
    public static SinkNode<VideoFrameRef> AsSinkNode(
        this IVideoSink sink,
        string id = "video-sink"
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(id);

        return Adapt<VideoFrameRef, IVideoFrame>(id, static r => r.Detach(), sink.PresentAsync);
    }

    /// <summary>
    /// Wraps any <see cref="IAudioSink"/> as a
    /// <see cref="SinkNode{TIn}"/> over <see cref="PcmAudioBufferRef"/>
    /// for graph wiring. Works uniformly with <c>OpenAlAudioSink</c>,
    /// test capturing sinks, etc. The sink's <see cref="IClockSource"/>
    /// integration (e.g. OpenAL as master clock) is independent of this
    /// adapter — callers wire it directly to a
    /// <see cref="ClockSubject"/> or equivalent.
    /// </summary>
    /// <param name="sink">The existing audio sink to wrap.</param>
    /// <param name="id">Node id for graph diagnostics.</param>
    public static SinkNode<PcmAudioBufferRef> AsSinkNode(
        this IAudioSink sink,
        string id = "audio-sink"
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(id);

        return Adapt<PcmAudioBufferRef, IAudioBuffer>(
            id,
            static r => r.Detach(),
            sink.PresentAsync
        );
    }

    /// <summary>
    /// Shared body for the <c>AsSinkNode</c> overloads: detach the
    /// payload from the substrate wrapper, skip an already-drained
    /// wrapper, and hand ownership to the sink.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="detach"/> result means the wrapper was
    /// already disposed or detached upstream. That is not an error —
    /// there is simply nothing left to present, so the body returns
    /// without touching the sink. See the ownership-transfer remarks
    /// on <see cref="SinkAdapters"/> for why detaching rather than
    /// <c>AddRef</c>ing is the right transfer.
    /// </remarks>
    /// <typeparam name="TRef">The substrate's refcounted wrapper type.</typeparam>
    /// <typeparam name="TPayload">The payload the sink presents.</typeparam>
    private static SinkNode<TRef> Adapt<TRef, TPayload>(
        string id,
        Func<TRef, TPayload?> detach,
        Func<TPayload, CancellationToken, ValueTask> present
    )
        where TRef : class, IRefCounted
        where TPayload : class
    {
        return new SinkNode<TRef>(
            id,
            async (item, ct) =>
            {
                var payload = detach(item);
                if (payload is null)
                    return;
                await present(payload, ct).ConfigureAwait(false);
            }
        );
    }
}
