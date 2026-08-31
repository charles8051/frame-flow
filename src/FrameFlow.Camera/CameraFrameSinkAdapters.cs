// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using Periphery.Camera;

namespace FrameFlow.Camera;

/// <summary>
/// Adapters that expose <see cref="ICameraFrameSink"/> implementations
/// as FrameFlow.Graph <see cref="SinkNode{TIn}"/> nodes, so a
/// camera-side sink (preview surface, inference loop, file writer, …)
/// can sit at the end of a <see cref="Graph"/> without per-sink
/// boilerplate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership transfer.</b> The substrate hands the sink-node body
/// one ref on the incoming <see cref="CameraFrameAdapter"/>. The
/// <see cref="ICameraFrameSink.PresentAsync"/> contract says the sink
/// takes ownership of the frame and disposes it. To honor both
/// contracts without double-disposing, the adapter
/// <see cref="Periphery.Camera.ICameraFrame.AddRef"/>s the inner camera
/// frame before handing it to the sink — the sink owns that AddRef'd
/// ref and disposes it, the substrate disposes the adapter ref after
/// the body returns. Net refcount delta is zero per frame.
/// </para>
/// <para>
/// <b>Format-change relay.</b> The substrate has no first-class
/// channel for sideband format events, so
/// <see cref="ICameraFrameSink.OnFormatChangedAsync"/> is not surfaced
/// through the sink node. Callers that need format-change notifications
/// keep an out-of-band reference to the sink and call it directly.
/// </para>
/// </remarks>
public static class CameraFrameSinkAdapters
{
    /// <summary>
    /// Wraps an <see cref="ICameraFrameSink"/> as a
    /// <see cref="SinkNode{CameraFrameAdapter}"/> suitable for inclusion
    /// in a <see cref="Graph"/>. The wrapped sink's lifecycle is
    /// unchanged — callers continue to own construction, format-change
    /// notifications, and disposal.
    /// </summary>
    /// <param name="sink">The camera-frame sink to wrap.</param>
    /// <param name="id">Node id for graph diagnostics.</param>
    public static SinkNode<CameraFrameAdapter> AsSinkNode(
        this ICameraFrameSink sink,
        string id = "camera-sink"
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(id);

        return new SinkNode<CameraFrameAdapter>(
            id,
            async (adapter, ct) =>
            {
                // AddRef the inner camera frame before handing it to
                // the sink: the sink owns its ref per the
                // ICameraFrameSink contract; the substrate owns the
                // adapter ref it pulled from the channel. Both
                // dispose their own ref independently.
                var sinkRef = adapter.Inner.AddRef();
                await sink.PresentAsync(sinkRef, ct).ConfigureAwait(false);
            }
        );
    }
}
