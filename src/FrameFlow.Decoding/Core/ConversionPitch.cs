// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Core;

/// <summary>
/// The rows swscale converts a packed frame into (#724). Pure: a width and a pixel size in, a row
/// pitch out.
/// </summary>
/// <remarks>
/// <para>
/// swscale's x86 YUV-to-RGB converter, which it uses from yuv420p, yuv422p and yuva420p to a
/// packed RGB format of the same size, writes 16 pixels a pass. It starts from the width rounded
/// up to a multiple of 8, and drops the last 8 when they would run past the stride. Into tightly
/// packed rows it writes too little or too much: at a width of 854 the last 6 pixels of every row
/// are never written, and at 856 every row runs 8 pixels into the next, the last row past the end
/// of the plane.
/// </para>
/// <para>
/// Rows padded to a whole number of passes hold exactly what it writes. A conversion into tightly
/// packed rows that are already that long goes straight into the frame. Any other goes into padded
/// rows and is copied out.
/// </para>
/// </remarks>
internal static class ConversionPitch
{
    /// <summary>The pixels the x86 converter writes in one pass.</summary>
    public const int PassPixels = 16;

    /// <summary>
    /// The pitch to convert a row of <paramref name="width"/> pixels of
    /// <paramref name="bytesPerPixel"/> bytes into: the width rounded up to a whole number of
    /// passes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Either argument is zero or negative.</exception>
    /// <exception cref="OverflowException">The pitch does not fit in an <see cref="int"/>.</exception>
    public static int For(int width, int bytesPerPixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerPixel);

        int passes = width / PassPixels + (width % PassPixels == 0 ? 0 : 1);
        return checked(passes * PassPixels * bytesPerPixel);
    }
}
