// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// Adapters that expose FrameFlow's <see cref="IVideoSink"/> and
/// <see cref="IAudioSink"/> implementations as substrate
/// <see cref="SinkNode{TIn}"/>s. The production sink classes don't
/// change — these adapters bridge the data plane from the graph's
/// <see cref="IVideoFrame"/> and <see cref="PcmAudioBuffer"/> items into
/// the sink's <c>PresentAsync</c> method.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are extension methods on the interface, not per
/// concrete sink.</b> Every <see cref="IVideoSink"/> declares
/// <see cref="IVideoSink.PresentAsync"/>; same for
/// <see cref="IAudioSink"/>. The adapter doesn't need to know whether
/// the underlying sink is Avalonia, SDL, OpenAL, or a test capturing
/// sink — it hands the item to <c>PresentAsync</c> and lets the
/// sink do the rest. One pair of adapters covers every current and
/// future sink implementation.
/// </para>
/// <para>
/// <b>Lifecycle stays on the original sink.</b> Callers still
/// construct, activate, dispose, and (for audio) wire as
/// <see cref="IClockSource"/> the underlying sink themselves. The
/// adapter is purely a data-plane shim.
/// </para>
/// <para>
/// <b>Ownership.</b> The sink contract (ADR-0044) is "the sink takes
/// ownership of the item and is responsible for disposing it after
/// presenting." The substrate also releases the reference it held for
/// the sink body once the body returns. So the adapter takes one
/// reference of its own for the sink: <c>AddRef</c> returns the same
/// item (ADR-0080), and the sink disposes it as it always has.
/// </para>
/// </remarks>
public static class SinkAdapters
{
    /// <summary>
    /// Wraps any <see cref="IVideoSink"/> as a
    /// <see cref="SinkNode{TIn}"/> over <see cref="IVideoFrame"/>
    /// for graph wiring. Works uniformly with <c>AvaloniaVideoSink</c>,
    /// <c>SdlVideoSink</c>, <c>NullVideoSink</c>, test capturing sinks,
    /// etc.
    /// </summary>
    /// <param name="sink">The existing video sink to wrap.</param>
    /// <param name="id">Node id for graph diagnostics.</param>
    public static SinkNode<IVideoFrame> AsSinkNode(
        this IVideoSink sink,
        string id = "video-sink"
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(id);

        return new SinkNode<IVideoFrame>(
            id,
            (frame, ct) => sink.PresentAsync(frame.AddRef(), ct),
            // The frame in the call, and what the sink keeps after it.
            holding: sink.MaxHeldFrames is { } kept ? Holding.AtMost(kept + 1) : Holding.Unbounded
        );
    }

    /// <summary>
    /// Wraps any <see cref="IAudioSink"/> as a
    /// <see cref="SinkNode{TIn}"/> over <see cref="PcmAudioBuffer"/>
    /// for graph wiring. Works uniformly with <c>OpenAlAudioSink</c>,
    /// test capturing sinks, etc. The sink's <see cref="IClockSource"/>
    /// integration (e.g. OpenAL as master clock) is independent of this
    /// adapter.
    /// </summary>
    /// <param name="sink">The existing audio sink to wrap.</param>
    /// <param name="id">Node id for graph diagnostics.</param>
    public static SinkNode<PcmAudioBuffer> AsSinkNode(
        this IAudioSink sink,
        string id = "audio-sink"
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(id);

        return new SinkNode<PcmAudioBuffer>(
            id,
            (buffer, ct) => sink.PresentAsync(buffer.AddRef(), ct)
        );
    }
}
