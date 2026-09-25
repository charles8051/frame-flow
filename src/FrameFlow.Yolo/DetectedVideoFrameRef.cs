// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Yolo;

/// <summary>
/// A video frame paired with its YOLOv8 detection results. Implements
/// <see cref="IRefCounted"/> so it can ride the substrate; the
/// detection list is an immutable value piggy-backed for the ride.
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
/// <b>Ref ownership.</b> The item counts its own references by the shared
/// rule (<see cref="RefCounting"/>, ADR-0080): <see cref="AddRef"/> returns
/// this same instance, and every holder shares it. It holds one reference on
/// <see cref="Video"/>, taken by whoever built it, and releases that
/// reference on its own final release.
/// </para>
/// </remarks>
public sealed class DetectedVideoFrameRef : IRefCounted, IFrame
{
    private int _refCount = 1;

    /// <summary>The underlying video frame. This item holds one reference on it.</summary>
    public IVideoFrame Video { get; }

    /// <summary>Detection results for this frame. Empty when nothing was detected above the model's threshold.</summary>
    public IReadOnlyList<Detection> Detections { get; }

    /// <summary>
    /// Pairs <paramref name="video"/> with its results. Takes over one reference on
    /// <paramref name="video"/>, which the item releases on its final release.
    /// </summary>
    public DetectedVideoFrameRef(IVideoFrame video, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(detections);
        Video = video;
        Detections = detections;
    }

    /// <summary>The frame's width.</summary>
    public int Width => Video.Width;

    /// <summary>The frame's height.</summary>
    public int Height => Video.Height;

    /// <summary>The frame's presentation time.</summary>
    public TimeSpan Timestamp => Video.Timestamp;

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The item has already been released.</exception>
    public IRefCounted AddRef()
    {
        RefCounting.AddRef(ref _refCount, this);
        return this;
    }

    /// <summary>Releases one reference; the final release releases the frame.</summary>
    public void Dispose()
    {
        if (RefCounting.Release(ref _refCount, this))
            Video.Dispose();
    }
}
