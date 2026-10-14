// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Decoding.Core;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// Runs <c>sws_scale</c> into one packed plane of tightly packed rows, such as a
/// <see cref="FrameFlow.Media.PixelFormat.Bgra32"/> frame's, so that every pixel of every row is
/// written and nothing past the plane is (#724). <see cref="ConversionPitch"/> has the reason.
/// </summary>
internal static unsafe class PackedConversion
{
    /// <summary>
    /// Converts <paramref name="sourceHeight"/> source rows into <paramref name="destination"/>,
    /// whose rows are <paramref name="width"/> pixels and <paramref name="stride"/> bytes long with
    /// no padding. Returns what <c>sws_scale</c> returned: the rows written, or a negative error.
    /// </summary>
    /// <param name="swsContext">A context that converts to the destination's format and size.</param>
    /// <param name="sourcePlanes">The four source plane pointers swscale reads.</param>
    /// <param name="sourceStrides">The four source strides.</param>
    /// <param name="sourceHeight">How many source rows to convert, from the top.</param>
    /// <param name="destination">The destination plane.</param>
    /// <param name="width">The destination width in pixels.</param>
    /// <param name="stride">The destination row length in bytes: the width times the pixel size.</param>
    public static int Scale(
        nint swsContext,
        byte** sourcePlanes,
        int* sourceStrides,
        int sourceHeight,
        Span<byte> destination,
        int width,
        int stride
    )
    {
        int pitch = ConversionPitch.For(width, stride / width);
        if (pitch == stride)
            return ScaleInto(swsContext, sourcePlanes, sourceStrides, sourceHeight, destination, stride);

        int rows = destination.Length / stride;
        byte[] padded = ArrayPool<byte>.Shared.Rent(checked(pitch * rows));
        try
        {
            int written = ScaleInto(
                swsContext,
                sourcePlanes,
                sourceStrides,
                sourceHeight,
                padded.AsSpan(0, pitch * rows),
                pitch
            );

            for (int row = 0; row < Math.Min(written, rows); row++)
                padded.AsSpan(row * pitch, stride).CopyTo(destination.Slice(row * stride, stride));

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(padded);
        }
    }

    private static int ScaleInto(
        nint swsContext,
        byte** sourcePlanes,
        int* sourceStrides,
        int sourceHeight,
        Span<byte> destination,
        int pitch
    )
    {
        fixed (byte* plane = destination)
        {
            // sws_scale expects 4 destination plane pointers/strides even for packed
            // single-plane output formats like BGRA. Passing 1-element arrays lets the
            // native code read past the stack buffer, which can corrupt memory and crash
            // the runtime with an ExecutionEngineException.
            byte** planes = stackalloc byte*[4];
            planes[0] = plane;
            planes[1] = null;
            planes[2] = null;
            planes[3] = null;

            int* strides = stackalloc int[4];
            strides[0] = pitch;
            strides[1] = 0;
            strides[2] = 0;
            strides[3] = 0;

            return FFSwScale.sws_scale(
                swsContext,
                sourcePlanes,
                sourceStrides,
                0,
                sourceHeight,
                planes,
                strides
            );
        }
    }
}
