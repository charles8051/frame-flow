// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Yolo;

/// <summary>
/// A video frame paired with its YOLOv8 detection results. Implements
/// <see cref="IRefCounted"/> so it can ride the substrate; the
/// inner <see cref="VideoFrameRef"/> owns the underlying frame's ref,
/// the detection list is an immutable value piggy-backed for the ride.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the old substrate's "video frame + metadata bag entry"
/// pattern. Where the old shape required consumers to
/// <c>packet.Metadata.Get&lt;DetectionResults&gt;()</c> and pray for
/// a hit, the new shape is typed at the edge — a consumer that
/// receives <see cref="DetectedVideoFrameRef"/> is guaranteed to
/// have both halves.
/// </para>
/// <para>
/// <b>Ref ownership.</b> Each wrapper owns exactly one ref on the
/// underlying frame (via the contained <see cref="VideoFrameRef"/>).
/// <see cref="AddRef"/> bumps the underlying refcount and returns a
/// new wrapper with its own <see cref="VideoFrameRef"/> copy;
/// <see cref="Dispose"/> releases the wrapper's ref by disposing
/// the inner <see cref="VideoFrameRef"/>.
/// </para>
/// </remarks>
public sealed class DetectedVideoFrameRef : IRefCounted
{
    /// <summary>The underlying video frame, refcount-owned by this wrapper.</summary>
    public VideoFrameRef Video { get; }

    /// <summary>Detection results for this frame. Empty when nothing was detected above the model's threshold.</summary>
    public IReadOnlyList<Detection> Detections { get; }

    public DetectedVideoFrameRef(VideoFrameRef video, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(detections);
        Video = video;
        Detections = detections;
    }

    public IRefCounted AddRef()
    {
        var videoCopy = (VideoFrameRef)Video.AddRef();
        return new DetectedVideoFrameRef(videoCopy, Detections);
    }

    public void Dispose() => Video.Dispose();
}
