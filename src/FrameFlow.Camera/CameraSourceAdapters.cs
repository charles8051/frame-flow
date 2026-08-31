// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Periphery.Camera;

namespace FrameFlow.Camera;

/// <summary>
/// Adapters that expose a pull-style camera-frame source (any
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="ICameraFrame"/>) as
/// a FrameFlow.Graph <see cref="SourceNode{TOut}"/>, so a
/// <c>CameraSession.CaptureAsync()</c> stream can sit at the head of a
/// <see cref="Graph"/> without per-call boilerplate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> Each item the enumerator yields already owns one
/// ref on its frame (this is the camera pool's lease contract). The
/// source node wraps the camera frame in a <see cref="CameraFrameAdapter"/>
/// — the adapter inherits the inner frame's ref — and hands the
/// adapter to the substrate channel. Downstream operators / sinks
/// dispose the adapter after use, which in turn disposes the inner
/// camera frame's ref. Net refcount delta is zero per frame.
/// </para>
/// <para>
/// <b>Enumerator lifecycle.</b> The enumerator is created lazily inside
/// the producer body (using the substrate's cancellation token) and
/// disposed via the source node's <c>Cleanup</c> hook when the pump
/// exits — EOS, cancellation, or exception. This guarantees the
/// underlying <see cref="CameraSession"/> stops producing once the
/// graph tears down.
/// </para>
/// </remarks>
public static class CameraSourceAdapters
{
    /// <summary>
    /// Wraps an <see cref="IAsyncEnumerable{T}"/> of
    /// <see cref="ICameraFrame"/>-derived frames as a
    /// <see cref="SourceNode{CameraFrameAdapter}"/>. The most common
    /// caller is <c>session.CaptureAsync(...).AsSourceNode()</c>.
    /// </summary>
    /// <typeparam name="T">
    /// Concrete frame type emitted by the enumerable. Constrained to
    /// <see cref="ICameraFrame"/>; the adapter handles the substrate
    /// refcount contract.
    /// </typeparam>
    /// <param name="source">The camera-frame source.</param>
    /// <param name="id">Node id for graph diagnostics.</param>
    public static SourceNode<CameraFrameAdapter> AsSourceNode<T>(
        this IAsyncEnumerable<T> source,
        string id = "camera-source"
    )
        where T : class, ICameraFrame
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(id);

        IAsyncEnumerator<T>? enumerator = null;
        return new SourceNode<CameraFrameAdapter>(
            id,
            async (ct) =>
            {
                enumerator ??= source.GetAsyncEnumerator(ct);
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    return null;
                }
                // The enumerator's current frame already owns one ref
                // (camera pool's lease). The adapter wraps it and
                // assumes ownership of that ref; downstream disposes
                // the adapter, which disposes the inner frame.
                return new CameraFrameAdapter(enumerator.Current);
            },
            cleanup: async () =>
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
        );
    }

    /// <summary>
    /// Wraps an <see cref="IAsyncEnumerable{T}"/> of
    /// <see cref="ICameraFrame"/>s as a
    /// <see cref="SourceNode{VideoFrameRef}"/>, so a camera capture
    /// stream drops directly into the same pipeline shape video
    /// decoder outputs use (<c>SourceNode&lt;VideoFrameRef&gt;</c>).
    /// Each emitted frame is a <see cref="CameraVideoFrame"/> wrapped
    /// in a <see cref="VideoFrameRef"/>. Downstream sinks call
    /// <c>frame.CloneCpu()</c> as they would on a decoded video frame
    /// — the clone is an independent owned copy; disposing the
    /// VideoFrameRef returns the inner camera frame to its pool.
    /// </summary>
    public static SourceNode<VideoFrameRef> AsVideoFrameSourceNode<T>(
        this IAsyncEnumerable<T> source,
        string id = "camera-video-source"
    )
        where T : class, ICameraFrame
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(id);

        IAsyncEnumerator<T>? enumerator = null;
        return new SourceNode<VideoFrameRef>(
            id,
            async (ct) =>
            {
                enumerator ??= source.GetAsyncEnumerator(ct);
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    return null;
                }
                // Adopt the camera frame's ref via CameraVideoFrame,
                // then wrap as VideoFrameRef so the substrate sees an
                // IRefCounted with the standard IVideoFrame surface.
                var videoFrame = new CameraVideoFrame(enumerator.Current);
                return new VideoFrameRef(videoFrame);
            },
            cleanup: async () =>
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
        );
    }
}
