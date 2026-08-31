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
/// Decoder-produced frames are one-shot (not pooled), so <see cref="AddRef"/>
/// throws <see cref="NotSupportedException"/>. Pooled frames use the
/// <c>Playback.CpuVideoFrame</c> implementation instead.
/// </para>
/// </remarks>
public sealed class CpuVideoFrame : IVideoFrame
{
    /// <summary>Pooled pixel buffer. Caller must dispose to return to pool.</summary>
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
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        PresentationTime = presentationTime;
        Duration = duration;
    }

    // ── IVideoFrame ref counting ──────────────────────────────────────

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Decoder-produced frames are one-shot and do not participate in
    /// ref-counted pooling. Use <c>Playback.CpuVideoFrame</c> for pooled frames.
    /// </exception>
    public IVideoFrame AddRef() =>
        throw new NotSupportedException(
            "Decoder-produced Media.CpuVideoFrame is one-shot and does not support ref counting."
        );

    // ── IVideoFrame domain access ─────────────────────────────────────

    /// <inheritdoc />
    public CpuFrameData? AsCpu()
    {
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
    public CpuFrameData ToCpu() => AsCpu()!.Value;

    /// <inheritdoc />
    public void Dispose() => PixelData.Dispose();
}
