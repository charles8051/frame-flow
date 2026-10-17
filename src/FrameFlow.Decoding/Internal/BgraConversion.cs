// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Decoding.Core;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// Converts a decoded CPU <c>AVFrame</c> to packed <c>Bgra32</c> as
/// <see cref="BgraConversionInput"/> says (#792). The software decoder and the hardware readback
/// both convert through here, so a frame converts the same whichever decoder produced it.
/// </summary>
internal static unsafe class BgraConversion
{
    // Past every repacked row and plane, as FFmpeg pads its own frames: swscale's SIMD reads
    // whole passes and can read beyond the last sample of a row.
    private const int Room = 64;

    /// <summary>
    /// Makes a context that converts <paramref name="width"/> by <paramref name="height"/> frames
    /// read as <paramref name="input"/> to <c>Bgra32</c> of the same size, or returns
    /// <see cref="nint.Zero"/> when swscale refuses. The caller frees it with
    /// <see cref="FFSwScale.sws_freeContext"/>.
    /// </summary>
    public static nint CreateContext(int width, int height, BgraConversionInput input)
    {
        nint ctx = FFSwScale.sws_getContext(
            width,
            height,
            input.SourceFormat,
            width,
            height,
            FFSwScale.AvPixFmtBgra,
            BgraConversionInput.ScaleFlags,
            nint.Zero,
            nint.Zero,
            nint.Zero
        );
        if (ctx == nint.Zero)
            return nint.Zero;

        // The frame's matrix and range in place of the BT.601 and the range sws_getContext infers
        // from the format (#792, #796). The destination table is unused: the output is RGB.
        nint coefficients = FFSwScale.sws_getCoefficients(input.Matrix);
        int rc = FFSwScale.sws_setColorspaceDetails(
            ctx,
            coefficients,
            input.FullRange ? 1 : 0,
            FFSwScale.sws_getCoefficients(FFSwScale.SwsCsDefault),
            0,
            0,
            1 << 16,
            1 << 16
        );
        if (rc < 0)
        {
            FFSwScale.sws_freeContext(ctx);
            return nint.Zero;
        }

        return ctx;
    }

    /// <summary>
    /// Converts the CPU <c>AVFrame</c> at <paramref name="source"/>, read as
    /// <paramref name="input"/>, into <paramref name="destination"/>: tightly packed rows of
    /// <paramref name="width"/> pixels and <paramref name="stride"/> bytes, as many as the
    /// context converts. Returns what <c>sws_scale</c> returned.
    /// </summary>
    public static int Convert(
        nint swsContext,
        BgraConversionInput input,
        nint source,
        Span<byte> destination,
        int width,
        int stride
    )
    {
        var accessor = new AvFrameAccessor(source);

        // The destination's size, which is the context's. A frame transferred off the GPU can
        // carry more rows than that, and swscale refuses a slice taller than its context.
        int height = destination.Length / stride;

        byte** planes = stackalloc byte*[4];
        int* strides = stackalloc int[4];
        for (int plane = 0; plane < 4; plane++)
        {
            planes[plane] = accessor.GetDataPointer(plane);
            strides[plane] = accessor.GetLineSize(plane);
        }

        if (!input.SplitChroma)
            return PackedConversion.Scale(swsContext, planes, strides, height, destination, width, stride);

        // NV12: U and V into planes of their own, the luma plane as it is.
        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        int chromaStride = Align(chromaWidth) + Room;
        int chromaLength = checked(chromaStride * chromaHeight + Room);

        byte[] split = ArrayPool<byte>.Shared.Rent(checked(chromaLength * 2));
        try
        {
            Span<byte> u = split.AsSpan(0, chromaLength);
            Span<byte> v = split.AsSpan(chromaLength, chromaLength);
            for (int row = 0; row < chromaHeight; row++)
            {
                ChromaSplit.Nv12Row(
                    new ReadOnlySpan<byte>(planes[1] + (nint)row * strides[1], chromaWidth * 2),
                    u.Slice(row * chromaStride, chromaWidth),
                    v.Slice(row * chromaStride, chromaWidth),
                    chromaWidth
                );
            }

            fixed (byte* buffer = split)
            {
                byte** planar = stackalloc byte*[4];
                int* planarStrides = stackalloc int[4];
                planar[0] = planes[0];
                planar[1] = buffer;
                planar[2] = buffer + chromaLength;
                planar[3] = null;
                planarStrides[0] = strides[0];
                planarStrides[1] = chromaStride;
                planarStrides[2] = chromaStride;
                planarStrides[3] = 0;
                return PackedConversion.Scale(swsContext, planar, planarStrides, height, destination, width, stride);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(split);
        }
    }

    private static int Align(int bytes) => (bytes + Room - 1) & ~(Room - 1);
}
