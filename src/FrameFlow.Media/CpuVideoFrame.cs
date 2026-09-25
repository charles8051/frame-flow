// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// A decoded video frame whose pixel data resides in CPU-managed memory.
/// This is the only concrete frame type in the v1 software path.
/// </summary>
/// <remarks>
/// <para>
/// Pixel data is owned by <see cref="PixelData"/>. The caller is responsible
/// for calling <see cref="Dispose"/> after presenting the frame, which returns
/// the backing buffer to the pool (per ADR-0012).
/// </para>
/// <para>
/// Implements <see cref="IVideoFrame"/> for the sink-based pipeline path.
/// </para>
/// <para>
/// The frame counts its own references (<see cref="RefCounting"/>, ADR-0080):
/// <see cref="AddRef"/> returns this same instance, every holder shares the one
/// buffer, and the final <see cref="Dispose"/> returns it to its pool. After that
/// <see cref="AsCpu"/> returns <see langword="null"/> and <see cref="ToCpu"/> throws.
/// </para>
/// </remarks>
public sealed class CpuVideoFrame : IVideoFrame
{
    private int _refCount = 1;
    private readonly int _bytes;

    /// <summary>Pooled pixel buffer, returned to its pool on the frame's final release.</summary>
    public IMemoryOwner<byte> PixelData { get; }

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <summary>Row stride in bytes (may include padding).</summary>
    public int Stride { get; }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <summary>Presentation timestamp relative to the start of the media stream.</summary>
    public TimeSpan PresentationTime { get; }

    // ── IVideoFrame metadata ──────────────────────────────────────────

    /// <inheritdoc />
    public TimeSpan Pts => PresentationTime;

    /// <inheritdoc />
    public TimeSpan Duration { get; }

    /// <inheritdoc />
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    public CpuVideoFrame(
        IMemoryOwner<byte> pixelData,
        int width,
        int height,
        int stride,
        PixelFormat format,
        TimeSpan presentationTime,
        TimeSpan duration = default
    )
    {
        PixelData = pixelData;
        _bytes = pixelData.Memory.Length;
        Diagnostics.CpuFrameMetrics.OnFrameCreated(_bytes);
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        PresentationTime = presentationTime;
        Duration = duration;
    }

    // ── IVideoFrame ref counting ──────────────────────────────────────

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The frame has already been released.</exception>
    public IVideoFrame AddRef()
    {
        RefCounting.AddRef(ref _refCount, this);
        return this;
    }

    // ── IVideoFrame domain access ─────────────────────────────────────

    /// <inheritdoc />
    public CpuFrameData? AsCpu()
    {
        if (Volatile.Read(ref _refCount) <= 0)
            return null;

        return new CpuFrameData(
            PlaneY: PixelData.Memory,
            PlaneU: ReadOnlyMemory<byte>.Empty,
            PlaneV: ReadOnlyMemory<byte>.Empty,
            StrideY: Stride,
            StrideU: 0,
            StrideV: 0,
            Width: Width,
            Height: Height
        );
    }

    /// <inheritdoc />
    public CpuFrameData ToCpu() =>
        AsCpu() ?? throw new ObjectDisposedException(nameof(CpuVideoFrame));

    /// <inheritdoc />
    public void Dispose()
    {
        if (!RefCounting.Release(ref _refCount, this))
            return;

        try
        {
            PixelData.Dispose();
        }
        finally
        {
            // The frame is released whether or not its buffer owner's Dispose throws.
            Diagnostics.CpuFrameMetrics.OnFrameReleased(_bytes);
        }
    }
}
