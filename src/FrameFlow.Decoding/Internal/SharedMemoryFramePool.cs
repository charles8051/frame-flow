// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Media;

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// Pool interface for allocating managed frame and audio buffers.
/// The abstraction allows the backing implementation to evolve from simple allocation
/// to a tuned pool without changing decoder or presenter code (ADR-0012).
/// </summary>
internal interface IFrameBufferPool
{
    /// <summary>
    /// Rents a buffer large enough to hold one video frame at the given dimensions and format.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="bytesPerPixel">Bytes per pixel for the target pixel format.</param>
    /// <returns>
    /// An <see cref="IMemoryOwner{T}"/> whose <c>Memory</c> is at least
    /// <c>width * height * bytesPerPixel</c> bytes long.
    /// The caller owns the returned instance and must dispose it when done.
    /// </returns>
    IMemoryOwner<byte> RentVideoBuffer(int width, int height, int bytesPerPixel);
}

/// <summary>
/// v1 frame buffer pool backed by <see cref="MemoryPool{T}.Shared"/> (which uses
/// <see cref="ArrayPool{T}.Shared"/> internally).
/// </summary>
/// <remarks>
/// Per ADR-0012, this provides immediate LOH pressure reduction for large frames.
/// Tuned pool buckets can be introduced behind the same interface later without
/// changing decoder or presenter code.
/// </remarks>
internal sealed class SharedMemoryFramePool : IFrameBufferPool
{
    /// <inheritdoc/>
    public IMemoryOwner<byte> RentVideoBuffer(int width, int height, int bytesPerPixel)
    {
        int size = width * height * bytesPerPixel;
        return MemoryPool<byte>.Shared.Rent(size);
    }
}
