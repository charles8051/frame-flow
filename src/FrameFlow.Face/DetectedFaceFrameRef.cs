// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Face;

/// <summary>
/// A video frame paired with its BlazeFace detection results. The face
/// analogue of <c>FrameFlow.Yolo.DetectedVideoFrameRef</c>: the inner
/// <see cref="VideoFrameRef"/> owns the underlying frame's ref, and the
/// immutable detection list piggy-backs for the ride.
/// </summary>
/// <remarks>
/// <b>Ref ownership.</b> Each wrapper owns exactly one ref on the
/// underlying frame (via the contained <see cref="VideoFrameRef"/>).
/// <see cref="AddRef"/> bumps the underlying refcount and returns a new
/// wrapper with its own <see cref="VideoFrameRef"/> copy;
/// <see cref="Dispose"/> releases the wrapper's ref by disposing the
/// inner <see cref="VideoFrameRef"/>.
/// </remarks>
public sealed class DetectedFaceFrameRef : IRefCounted
{
    /// <summary>The underlying video frame, refcount-owned by this wrapper.</summary>
    public VideoFrameRef Video { get; }

    /// <summary>Face detections for this frame. Empty when no face scored above threshold.</summary>
    public IReadOnlyList<FaceDetection> Faces { get; }

    public DetectedFaceFrameRef(VideoFrameRef video, IReadOnlyList<FaceDetection> faces)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(faces);
        Video = video;
        Faces = faces;
    }

    public IRefCounted AddRef()
    {
        var videoCopy = (VideoFrameRef)Video.AddRef();
        return new DetectedFaceFrameRef(videoCopy, Faces);
    }

    public void Dispose() => Video.Dispose();
}
