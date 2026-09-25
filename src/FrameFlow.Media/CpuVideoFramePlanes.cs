// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Fills the planes of a new <see cref="CpuVideoFrame"/>. Called once, synchronously, by
/// <see cref="CpuVideoFrame.Create{TState}"/> before the frame is published.
/// </summary>
/// <typeparam name="TState">The state the caller passes through, so the callback can be static.</typeparam>
/// <param name="planes">The frame's writable planes. They exist only for the call.</param>
/// <param name="state">The state passed to <see cref="CpuVideoFrame.Create{TState}"/>.</param>
public delegate void CpuVideoFrameFill<in TState>(CpuVideoFramePlanes planes, TState state);

/// <summary>
/// The writable planes of a <see cref="CpuVideoFrame"/> while its fill callback runs.
/// </summary>
/// <remarks>
/// <para>
/// A <see langword="ref struct">ref struct</see>, so the spans cannot be stored on the heap or
/// returned from the callback, and no writable alias outlives the fill (ADR-0080, decision 5).
/// A producer that keeps a pointer past the callback through <see langword="fixed"/> can still
/// write after publication; not doing so is the producer's obligation.
/// </para>
/// <para>
/// Planes are tightly packed rows. <see cref="Y"/> holds the whole image for packed formats
/// (<see cref="PixelFormat.Bgra32"/>, <see cref="PixelFormat.Rgba32"/>,
/// <see cref="PixelFormat.Yuyv422"/>, <see cref="PixelFormat.Uyvy422"/>).
/// <see cref="PixelFormat.Yuv420P"/> uses all three. <see cref="PixelFormat.Nv12"/> puts its
/// interleaved chroma in <see cref="U"/> and leaves <see cref="V"/> empty, the same layout
/// <see cref="CpuFrameData"/> reports.
/// </para>
/// </remarks>
public readonly ref struct CpuVideoFramePlanes
{
    internal CpuVideoFramePlanes(
        Span<byte> storage,
        CpuFrameLayout layout,
        PixelFormat format,
        int width,
        int height
    )
    {
        Y = storage.Slice(0, layout.LengthY);
        U = storage.Slice(layout.OffsetU, layout.LengthU);
        V = storage.Slice(layout.OffsetV, layout.LengthV);
        StrideY = layout.StrideY;
        StrideU = layout.StrideU;
        StrideV = layout.StrideV;
        Format = format;
        Width = width;
        Height = height;
    }

    /// <summary>The first plane: luma, or the whole image for a packed format.</summary>
    public Span<byte> Y { get; }

    /// <summary>The second plane: Cb, or interleaved CbCr for NV12. Empty for packed formats.</summary>
    public Span<byte> U { get; }

    /// <summary>The third plane: Cr. Empty for packed formats and NV12.</summary>
    public Span<byte> V { get; }

    /// <summary>Bytes per row of <see cref="Y"/>.</summary>
    public int StrideY { get; }

    /// <summary>Bytes per row of <see cref="U"/>, or 0 when it is empty.</summary>
    public int StrideU { get; }

    /// <summary>Bytes per row of <see cref="V"/>, or 0 when it is empty.</summary>
    public int StrideV { get; }

    /// <summary>The frame's pixel format.</summary>
    public PixelFormat Format { get; }

    /// <summary>The frame's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The frame's height in pixels.</summary>
    public int Height { get; }
}

/// <summary>
/// Where each plane of a tightly packed CPU frame sits in its storage.
/// </summary>
internal readonly record struct CpuFrameLayout(
    int StrideY,
    int RowsY,
    int StrideU,
    int RowsU,
    int StrideV,
    int RowsV
)
{
    public int LengthY => StrideY * RowsY;
    public int LengthU => StrideU * RowsU;
    public int LengthV => StrideV * RowsV;
    public int OffsetU => LengthY;
    public int OffsetV => LengthY + LengthU;
    public int TotalBytes => LengthY + LengthU + LengthV;

    /// <summary>The layout for <paramref name="format"/> at the given size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative, or the frame does not fit in one array.</exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not a known pixel format.</exception>
    public static CpuFrameLayout For(PixelFormat format, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        long chromaWidth = (width + 1L) / 2;
        long chromaHeight = (height + 1L) / 2;

        (long strideY, long strideU, long rowsU, long strideV, long rowsV) = format switch
        {
            PixelFormat.Bgra32 or PixelFormat.Rgba32 => (width * 4L, 0L, 0L, 0L, 0L),
            PixelFormat.Yuyv422 or PixelFormat.Uyvy422 => (chromaWidth * 4, 0L, 0L, 0L, 0L),
            PixelFormat.Yuv420P => (
                (long)width,
                chromaWidth,
                chromaHeight,
                chromaWidth,
                chromaHeight
            ),
            PixelFormat.Nv12 => ((long)width, chromaWidth * 2, chromaHeight, 0L, 0L),
            _ => throw new ArgumentException($"Unknown pixel format {format}.", nameof(format)),
        };

        long total = (strideY * height) + (strideU * rowsU) + (strideV * rowsV);
        if (total > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"A {width}x{height} {format} frame needs {total} bytes, more than one array holds."
            );
        }

        return new CpuFrameLayout(
            (int)strideY,
            height,
            (int)strideU,
            (int)rowsU,
            (int)strideV,
            (int)rowsV
        );
    }
}
