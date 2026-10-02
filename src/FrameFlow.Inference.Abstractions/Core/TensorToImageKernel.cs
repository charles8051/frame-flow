// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FrameFlow.Media;

namespace FrameFlow.Inference.Core;

/// <summary>Which of the kernel's paths to take. Tests pin one to compare it with another.</summary>
internal enum TensorToImagePath
{
    /// <summary>Vectorised where the hardware allows, one pixel at a time otherwise.</summary>
    Auto,

    /// <summary>One pixel at a time.</summary>
    Scalar,
}

/// <summary>
/// Writes an image-shaped float tensor into packed 32-bit BGRA or RGBA as
/// <see cref="TensorToImageOptions"/> says. It writes only the first <c>4 · Width</c> bytes of each
/// destination row, and the same inputs write the same bytes on every path.
/// </summary>
/// <remarks>
/// <para>
/// Each value becomes a byte in four steps: <c>(value − Offset) · (1 / Scale)</c> with the channel's
/// normalization, a clamp to 0 to 255 that sends NaN to 0, a round to the nearest integer with ties
/// to even, and a shift into its place in the pixel beside an alpha of 255. The vector loop does the
/// same float operations in the same order as the scalar loop, so the paths agree bit for bit.
/// </para>
/// <para>
/// The offset is subtracted before the scale is applied. A value close to a large offset then keeps
/// its difference exactly; scaling first would round it at the scaled offset's magnitude.
/// </para>
/// <para>
/// A planar tensor's rows are read in place. An interleaved (NHWC) tensor's row is first split
/// into three planes, which copies floats without changing them.
/// </para>
/// <para>
/// A video operator calls the kernel once a frame, so under tiered compilation its first frames
/// run at tier 0, where <c>Vector&lt;T&gt;</c> operations are calls rather than instructions
/// (#546). The methods that loop over a row or the image are therefore
/// <see cref="MethodImplOptions.AggressiveOptimization"/>. What they call per element is small
/// enough that the JIT inlines it. Code compiled this way gets no dynamic PGO.
/// </para>
/// </remarks>
internal static class TensorToImageKernel
{
    private const float MaxSample = 255f;

    /// <summary>
    /// Runs the conversion. The caller has checked the options, that <paramref name="tensor"/>
    /// holds <see cref="TensorToImageOptions.ElementCount"/> floats, and that
    /// <paramref name="destination"/> holds <c>Height</c> rows of <paramref name="stride"/> bytes,
    /// the last at least <c>4 · Width</c> long.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Run(
        ReadOnlySpan<float> tensor,
        TensorToImageOptions options,
        Span<byte> destination,
        int stride,
        TensorToImagePath path)
    {
        var channels = new Channels(options.Normalization, options.Format == PixelFormat.Bgra32);
        int width = options.Width;
        int height = options.Height;
        int planeLength = width * height;
        bool grey = options.Channels == 1;
        bool nhwc = !grey && options.Layout == TensorLayout.Nhwc;
        bool rgb = options.ChannelOrder == TensorChannelOrder.Rgb;
        bool vector = path == TensorToImagePath.Auto && Vector.IsHardwareAccelerated;

        float[]? scratch = nhwc ? ArrayPool<float>.Shared.Rent(3 * width) : null;
        try
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<float> r, g, b;
                if (grey)
                {
                    r = g = b = tensor.Slice(y * width, width);
                }
                else if (nhwc)
                {
                    var first = scratch.AsSpan(0, width);
                    var second = scratch.AsSpan(width, width);
                    var third = scratch.AsSpan(2 * width, width);
                    Deinterleave(tensor.Slice(3 * y * width, 3 * width), first, second, third);
                    r = rgb ? first : third;
                    g = second;
                    b = rgb ? third : first;
                }
                else
                {
                    int row = y * width;
                    r = tensor.Slice((rgb ? 0 : 2 * planeLength) + row, width);
                    g = tensor.Slice(planeLength + row, width);
                    b = tensor.Slice((rgb ? 2 * planeLength : 0) + row, width);
                }

                var pixels = MemoryMarshal.Cast<byte, uint>(destination.Slice(y * stride, 4 * width));
                ConvertRow(r, g, b, channels, vector, pixels);
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>The factor that undoes a channel's scale, computed in double and rounded to a float once.</summary>
    internal static float Reciprocal(float scale) => (float)(1.0 / scale);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertRow(
        ReadOnlySpan<float> r,
        ReadOnlySpan<float> g,
        ReadOnlySpan<float> b,
        in Channels channels,
        bool vector,
        Span<uint> pixels)
    {
        int i = 0;
        if (vector)
        {
            var offsetR = new Vector<float>(channels.OffsetRed);
            var offsetG = new Vector<float>(channels.OffsetGreen);
            var offsetB = new Vector<float>(channels.OffsetBlue);
            var reciprocalR = new Vector<float>(channels.ReciprocalRed);
            var reciprocalG = new Vector<float>(channels.ReciprocalGreen);
            var reciprocalB = new Vector<float>(channels.ReciprocalBlue);
            var alpha = new Vector<uint>(channels.Alpha);
            for (; i <= pixels.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var red = Quantize((new Vector<float>(r[i..]) - offsetR) * reciprocalR);
                var green = Quantize((new Vector<float>(g[i..]) - offsetG) * reciprocalG);
                var blue = Quantize((new Vector<float>(b[i..]) - offsetB) * reciprocalB);
                (alpha
                    | Vector.ShiftLeft(red, channels.ShiftRed)
                    | Vector.ShiftLeft(green, channels.ShiftGreen)
                    | Vector.ShiftLeft(blue, channels.ShiftBlue)).CopyTo(pixels[i..]);
            }
        }

        for (; i < pixels.Length; i++)
        {
            pixels[i] = channels.Alpha
                | (Quantize((r[i] - channels.OffsetRed) * channels.ReciprocalRed) << channels.ShiftRed)
                | (Quantize((g[i] - channels.OffsetGreen) * channels.ReciprocalGreen) << channels.ShiftGreen)
                | (Quantize((b[i] - channels.OffsetBlue) * channels.ReciprocalBlue) << channels.ShiftBlue);
        }
    }

    /// <summary>The vector form of <see cref="Quantize(float)"/>, in the same order.</summary>
    private static Vector<uint> Quantize(Vector<float> sample)
    {
        var max = new Vector<float>(MaxSample);
        var clamped = Vector.ConditionalSelect(
            Vector.GreaterThan(sample, Vector<float>.Zero), Vector.Min(sample, max), Vector<float>.Zero);
        return Vector.AsVectorUInt32(Vector.ConvertToInt32(Vector.Round(clamped)));
    }

    /// <summary>
    /// Clamps a sample to 0 to 255, NaN to 0, and rounds it to the nearest integer, ties to even.
    /// </summary>
    private static uint Quantize(float sample)
    {
        float clamped = sample > 0f ? MathF.Min(sample, MaxSample) : 0f;
        return (uint)(int)MathF.Round(clamped);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Deinterleave(
        ReadOnlySpan<float> source,
        Span<float> first,
        Span<float> second,
        Span<float> third)
    {
        for (int i = 0; i < first.Length; i++)
        {
            first[i] = source[3 * i];
            second[i] = source[3 * i + 1];
            third[i] = source[3 * i + 2];
        }
    }

    /// <summary>Where each colour goes in a packed pixel, and how each is un-normalized.</summary>
    private readonly struct Channels
    {
        public Channels(TensorNormalization normalization, bool bgra)
        {
            // Byte i of a pixel is bits 8i to 8i + 7 of a little-endian uint.
            ShiftRed = Shift(bgra ? 2 : 0);
            ShiftGreen = Shift(1);
            ShiftBlue = Shift(bgra ? 0 : 2);
            Alpha = 0xFFu << Shift(3);
            OffsetRed = normalization.Red.Offset;
            OffsetGreen = normalization.Green.Offset;
            OffsetBlue = normalization.Blue.Offset;
            ReciprocalRed = Reciprocal(normalization.Red.Scale);
            ReciprocalGreen = Reciprocal(normalization.Green.Scale);
            ReciprocalBlue = Reciprocal(normalization.Blue.Scale);

            static int Shift(int byteIndex) => BitConverter.IsLittleEndian ? 8 * byteIndex : 24 - 8 * byteIndex;
        }

        public int ShiftRed { get; }

        public int ShiftGreen { get; }

        public int ShiftBlue { get; }

        public uint Alpha { get; }

        public float OffsetRed { get; }

        public float OffsetGreen { get; }

        public float OffsetBlue { get; }

        public float ReciprocalRed { get; }

        public float ReciprocalGreen { get; }

        public float ReciprocalBlue { get; }
    }
}
