// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
    /// <paramref name="frame"/>. Every plane is copied; metadata (PTS,
    /// duration, format) is preserved.
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
    /// <b>Layout.</b> The clone is tightly packed, the layout
    /// <see cref="CpuVideoFrame.Create{TState}"/> gives its format. A source
    /// whose rows carry padding is copied row by row, so the clone's
    /// strides can be smaller than the source's. NV12's interleaved chroma
    /// is read from and written to <c>PlaneU</c>.
    /// </para>
    /// </remarks>
    /// <param name="frame">Source frame; not modified, not disposed.</param>
    /// <returns>An independently-disposable CPU clone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="frame"/> exposes no CPU view (i.e. <see cref="IVideoFrame.AsCpu"/>
    /// returned <see langword="null"/>), or a plane of that view is too short for the
    /// frame's size and format. GPU-only frames need a readback first.
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

        return CpuVideoFrame.Create(
            frame.Format,
            frame.Width,
            frame.Height,
            frame.Pts,
            frame.Duration,
            cpu,
            static (planes, source) =>
            {
                CopyPlane(source.PlaneY.Span, source.StrideY, planes.Y, planes.StrideY, "Y");
                CopyPlane(source.PlaneU.Span, source.StrideU, planes.U, planes.StrideU, "U");
                CopyPlane(source.PlaneV.Span, source.StrideV, planes.V, planes.StrideV, "V");
            }
        );
    }

    private static void CopyPlane(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int destinationStride,
        string plane
    )
    {
        if (destination.IsEmpty)
            return;

        int rows = destination.Length / destinationStride;
        long needed = ((long)(rows - 1) * sourceStride) + destinationStride;
        if (sourceStride < destinationStride || source.Length < needed)
        {
            throw new InvalidOperationException(
                $"CloneCpu: the source's {plane} plane has {source.Length} bytes at stride "
                    + $"{sourceStride}, too few for {rows} rows of {destinationStride} bytes."
            );
        }

        if (sourceStride == destinationStride)
        {
            source[..destination.Length].CopyTo(destination);
            return;
        }

        for (int row = 0; row < rows; row++)
        {
            source
                .Slice(row * sourceStride, destinationStride)
                .CopyTo(destination.Slice(row * destinationStride, destinationStride));
        }
    }
}
