// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// Manages a pool of reusable <see cref="IVideoFrame"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// The pool provides backpressure: when all frames are in-flight,
/// <see cref="RentAsync"/> blocks until a frame is returned via
/// <see cref="Return"/> (or <see cref="IVideoFrame.Dispose"/>).
/// </para>
/// <para>
/// V1 provides a CPU-backed pool. The <see cref="MemoryDomain"/> property
/// allows callers to query the domain before renting.
/// </para>
/// </remarks>
public interface IFramePool : IDisposable
{
    /// <summary>The memory domain of frames managed by this pool.</summary>
    FrameMemoryDomain MemoryDomain { get; }

    /// <summary>
    /// Rents a frame of the requested dimensions and format.
    /// Blocks asynchronously when no frames are available (backpressure).
    /// </summary>
    /// <param name="width">Requested frame width in pixels.</param>
    /// <param name="height">Requested frame height in pixels.</param>
    /// <param name="format">Requested pixel format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A rented frame with ref count 1.</returns>
    ValueTask<IVideoFrame> RentAsync(
        int width,
        int height,
        PixelFormat format,
        CancellationToken ct
    );

    /// <summary>
    /// Returns a frame to the pool, making it available for reuse.
    /// </summary>
    /// <param name="frame">The frame to return.</param>
    void Return(IVideoFrame frame);
}
