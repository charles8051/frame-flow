// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;

namespace FrameFlow.Media;

/// <summary>
/// Helpers for working with <see cref="IVideoFrame"/> instances. Lives
/// alongside <see cref="CpuVideoFrame"/> in <c>FrameFlow.Media</c>; no
/// new dependencies required.
/// </summary>
public static class VideoFrameExtensions
{
    /// <summary>
    /// Returns a fresh, independently-disposable CPU copy of
    /// <paramref name="frame"/>. Pixel data is copied into a new
    /// <see cref="IMemoryOwner{T}"/> rented from
    /// <see cref="MemoryPool{T}.Shared"/>; metadata (PTS, duration,
    /// format) is preserved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The standard "give me another reference to this frame" primitive
    /// is <see cref="IVideoFrame.AddRef"/>, which every frame type supports
    /// (ADR-0080). This helper is for a caller that wants a private copy
    /// instead: a deep CPU clone that it owns end-to-end, independent of
    /// the source frame's buffer and pool.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Allocates one buffer rental + a single memcpy of the
    /// pixel plane. For the multicast / live-captioning scenarios this
    /// is the price of fan-out to <c>N</c> branches at independent
    /// rates; the alternative would be plumbing a frame pool into
    /// every operator on the hot path.
    /// </para>
    /// <para>
    /// <b>Packed formats only today.</b> The clone reads
    /// <see cref="IVideoFrame.AsCpu"/>'s <c>PlaneY</c> as the entire
    /// pixel payload — correct for BGRA32 / RGBA32 (and the broadcast
    /// examples that <c>ConvertPixelFormat(Bgra32)</c> before
    /// branching). Planar YUV inputs would clone only the Y plane.
    /// When a planar use case appears, extend the helper to copy
    /// PlaneU / PlaneV as well.
    /// </para>
    /// </remarks>
    /// <param name="frame">Source frame; not modified, not disposed.</param>
    /// <returns>An independently-disposable CPU clone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="frame"/> exposes no CPU view (i.e. <see cref="IVideoFrame.AsCpu"/>
    /// returned <see langword="null"/>). GPU-only frames need a readback first.
    /// </exception>
    public static CpuVideoFrame CloneCpu(this IVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var cpu =
            frame.AsCpu()
            ?? throw new InvalidOperationException(
                "CloneCpu: source frame has no CPU view. GPU-resident frames must be "
                    + "read back to CPU before cloning."
            );

        // PlaneY holds the full packed payload for BGRA32 / RGBA32.
        // Stride may include padding; copying the entire span (including
        // padding bytes) keeps the new frame byte-identical and the
        // clone's stride matches the source's.
        var srcPlane = cpu.PlaneY.Span;
        var owner = MemoryPool<byte>.Shared.Rent(srcPlane.Length);
        srcPlane.CopyTo(owner.Memory.Span);

        return new CpuVideoFrame(
            pixelData: owner,
            width: frame.Width,
            height: frame.Height,
            stride: cpu.StrideY,
            format: frame.Format,
            presentationTime: frame.Pts,
            duration: frame.Duration
        );
    }
}
