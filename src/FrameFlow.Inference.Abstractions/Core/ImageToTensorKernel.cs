// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Core;

/// <summary>Which of the kernel's paths to take. Tests pin one to compare it with another.</summary>
internal enum ImageToTensorPath
{
    /// <summary>
    /// Tables for an unrotated crop and a per-row gather for a rotated bilinear one, each converted
    /// a vector at a time where the hardware allows. A rotated nearest crop is sampled per pixel.
    /// </summary>
    Auto,

    /// <summary><see cref="Auto"/>, converted one pixel at a time.</summary>
    Scalar,

    /// <summary>Each pixel sampled and converted on its own, whatever the crop.</summary>
    General,
}

/// <summary>
/// Writes a 32-bit BGRA or RGBA image into a tensor of floats, halves or bytes as an
/// <see cref="ImageToTensorPlan"/> says. It writes only <c>destination</c>, and the same inputs
/// write the same elements on every path.
/// </summary>
/// <remarks>
/// <para>
/// Every path computes a value with the same float operations in the same order, so the paths
/// agree bit for bit. The vector loops mirror the scalar helpers <see cref="NearestValue"/> and
/// <see cref="BilinearValue"/> operation for operation.
/// </para>
/// <para>
/// A float tensor in NCHW is written in place. Any other tensor has each row computed as floats
/// in a buffer and then stored: interleaved for NHWC, and converted for a half or byte tensor by
/// <see cref="ToHalves"/> or <see cref="ToBytes"/>, whose vector loops round as their scalar
/// forms do.
/// </para>
/// <para>
/// An unrotated crop is separable: a column's frame x is the same on every row, and a row's
/// frame y the same in every column. That path reads each row's pixels through a per-column table
/// into a buffer, then converts the buffer a vector at a time. A rotated crop computes each
/// pixel's position. For bilinear sampling it reads the four pixels around each into the same
/// buffers, a row at a time, and converts them the same way, which halves the time of a 256x256
/// crop. Nearest sampling converts too little per pixel to repay the buffers, so a rotated nearest
/// crop converts each pixel as it reads it.
/// </para>
/// <para>
/// A video operator calls the kernel once a frame, so under tiered compilation its first frames
/// run at tier 0, where <c>Vector&lt;T&gt;</c> operations are calls rather than instructions
/// (#546). The methods that loop over a row or the image are therefore
/// <see cref="MethodImplOptions.AggressiveOptimization"/>, and what they call per element or per
/// row is <see cref="MethodImplOptions.AggressiveInlining"/>, so it compiles into an optimized
/// caller. Code compiled this way gets no dynamic PGO.
/// </para>
/// </remarks>
internal static class ImageToTensorKernel
{
    private const int BytesPerPixel = 4;

    /// <summary>
    /// Runs <paramref name="plan"/>. The caller has checked that <paramref name="pixels"/> holds
    /// <paramref name="height"/> rows of <paramref name="stride"/> bytes, the last at least
    /// <c>4 · width</c> long, and that <paramref name="destination"/> holds the whole tensor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Run<T>(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        bool bgra,
        in ImageToTensorPlan plan,
        ImageToTensorOptions options,
        Span<T> destination,
        ImageToTensorPath path)
        where T : unmanaged
    {
        var channels = new Channels(bgra, options.Normalization, options.PadValue);
        int tensorWidth = plan.TensorWidth;
        int tensorHeight = plan.TensorHeight;
        bool nhwc = options.Layout == TensorLayout.Nhwc;
        bool rgb = options.ChannelOrder == TensorChannelOrder.Rgb;
        bool bilinear = options.Sampling == ImageSampling.Bilinear;
        bool padOutside = options.Border == ImageBorder.Pad;

        bool general = path == ImageToTensorPath.General;
        bool tables = plan.IsAxisAligned && !general;

        // Only a float tensor in NCHW takes each row's values where they are computed.
        bool inPlace = typeof(T) == typeof(float) && !nhwc;
        var source = new Source(pixels, width, height, stride);
        var scratch = new Scratch(
            tensorWidth,
            colours: !inPlace,
            interleaved: nhwc && typeof(T) != typeof(float),
            bilinear,
            rotated: !plan.IsAxisAligned && !general && bilinear);
        try
        {
            (int First, int End) columns = default;
            if (tables)
            {
                columns = BuildColumns(plan, width, bilinear, padOutside, scratch);
            }

            bool vector = path == ImageToTensorPath.Auto && Vector.IsHardwareAccelerated;
            int planeLength = tensorWidth * tensorHeight;
            int redPlane = rgb ? 0 : 2 * planeLength;
            int bluePlane = rgb ? 2 * planeLength : 0;
            for (int dy = 0; dy < tensorHeight; dy++)
            {
                int row = dy * tensorWidth;
                Span<float> r, g, b;
                if (inPlace)
                {
                    var floats = MemoryMarshal.Cast<T, float>(destination);
                    r = floats.Slice(redPlane + row, tensorWidth);
                    g = floats.Slice(planeLength + row, tensorWidth);
                    b = floats.Slice(bluePlane + row, tensorWidth);
                }
                else
                {
                    r = scratch.Red;
                    g = scratch.Green;
                    b = scratch.Blue;
                }

                // An unrotated row reads one frame row, so the whole row is inside or outside.
                if (!plan.CoversRow(dy) || (tables && padOutside && !Inside(plan.SourceY(0, dy), height)))
                {
                    channels.Pad(r, g, b);
                }
                else if (general || (!tables && !bilinear))
                {
                    GeneralRow(source, plan, dy, bilinear, padOutside, channels, r, g, b);
                }
                else if (!tables)
                {
                    RotatedBilinearRow(source, plan, dy, padOutside, scratch, channels, vector, r, g, b);
                }
                else if (bilinear)
                {
                    BilinearRow(source, plan, dy, columns, scratch, channels, vector, r, g, b);
                }
                else
                {
                    NearestRow(source, plan, dy, columns, scratch, channels, vector, r, g, b);
                }

                if (nhwc)
                {
                    var pixelsOut = destination.Slice(row * 3, tensorWidth * 3);
                    var interleaved = typeof(T) == typeof(float)
                        ? MemoryMarshal.Cast<T, float>(pixelsOut)
                        : scratch.Interleaved;
                    if (rgb)
                    {
                        Interleave(r, g, b, interleaved);
                    }
                    else
                    {
                        Interleave(b, g, r, interleaved);
                    }

                    if (typeof(T) != typeof(float))
                    {
                        Store(interleaved, pixelsOut, vector);
                    }
                }
                else if (!inPlace)
                {
                    Store(r, destination.Slice(redPlane + row, tensorWidth), vector);
                    Store(g, destination.Slice(planeLength + row, tensorWidth), vector);
                    Store(b, destination.Slice(bluePlane + row, tensorWidth), vector);
                }
            }
        }
        finally
        {
            scratch.Return();
        }
    }

    /// <summary>Converts a row of values to a half or byte tensor's elements.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store<T>(ReadOnlySpan<float> values, Span<T> destination, bool vector)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            ToBytes(values, MemoryMarshal.Cast<T, byte>(destination), vector);
        }
        else if (typeof(T) == typeof(Half))
        {
            ToHalves(values, MemoryMarshal.Cast<T, Half>(destination), vector);
        }
        else
        {
            throw new NotSupportedException($"The kernel writes float, Half or byte tensors; got {typeof(T).Name}.");
        }
    }

    /// <summary>
    /// Each value clamped to 0 to 255 and rounded to the nearest integer, ties to even. The vector
    /// loop clamps, rounds and converts as <see cref="ToByte"/> does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ToBytes(ReadOnlySpan<float> values, Span<byte> destination, bool vector)
    {
        int i = 0;
        if (vector)
        {
            // Four float vectors narrow to one byte vector. Each lane is 0 to 255, which narrowing
            // keeps: the signed byte it passes through has the same bits.
            int floats = Vector<float>.Count;
            var max = new Vector<float>(255f);
            for (; i <= values.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                var low = Vector.Narrow(
                    ToInt32(new Vector<float>(values[i..]), max),
                    ToInt32(new Vector<float>(values[(i + floats)..]), max));
                var high = Vector.Narrow(
                    ToInt32(new Vector<float>(values[(i + 2 * floats)..]), max),
                    ToInt32(new Vector<float>(values[(i + 3 * floats)..]), max));
                Vector.AsVectorByte(Vector.Narrow(low, high)).CopyTo(destination[i..]);
            }
        }

        for (; i < values.Length; i++)
        {
            destination[i] = ToByte(values[i]);
        }
    }

    // The vector form of ToByte up to the narrowing. It converts to int32, not uint32: without
    // AVX-512, x64 has no float to uint32 instruction, and the emulated conversion measured four
    // times slower.
    private static Vector<int> ToInt32(Vector<float> value, Vector<float> max)
        => Vector.ConvertToInt32(Vector.Round(Vector.Min(Vector.Max(value, Vector<float>.Zero), max)));

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 255f));

    /// <summary>
    /// Each value converted to <see cref="Half"/>, rounding to nearest with ties to even. The vector
    /// loop is <see cref="HalfBits"/>, which gives <c>(Half)value</c>'s bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ToHalves(ReadOnlySpan<float> values, Span<Half> destination, bool vector)
    {
        int i = 0;
        if (vector)
        {
            // Two float vectors narrow to one vector of 16-bit halves.
            var bits = MemoryMarshal.Cast<Half, ushort>(destination);
            int floats = Vector<float>.Count;
            for (; i <= values.Length - Vector<ushort>.Count; i += Vector<ushort>.Count)
            {
                Vector.Narrow(
                    HalfBits(new Vector<float>(values[i..])),
                    HalfBits(new Vector<float>(values[(i + floats)..]))).CopyTo(bits[i..]);
            }
        }

        for (; i < values.Length; i++)
        {
            destination[i] = (Half)values[i];
        }
    }

    /// <summary>
    /// <c>(Half)value</c> for each lane, in the low 16 bits, by the same branch-free steps as the
    /// runtime's scalar conversion. <see cref="Vector.Min{T}"/> and <see cref="Vector.Max{T}"/>
    /// propagate NaN as <c>float.Min</c> and <c>float.Max</c> do, so a NaN lane matches too.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<uint> HalfBits(Vector<float> value)
    {
        var sign = Vector.ShiftRightLogical(Vector.AsVectorUInt32(value) & new Vector<uint>(0x8000_0000u), 16);
        var real = Vector.AsVectorUInt32(Vector.Equals(value, value));

        // 65520 is the smallest float that rounds to a half's infinity. Adding 2^13 times the
        // value's leading power of two, or times 2^-14 (a half's smallest normal, whose precision
        // its subnormals share) if the value is smaller, leaves the sum's last bit worth a half's
        // last bit, so the add rounds to a half's precision, ties to even.
        value = Vector.Min(new Vector<float>(65520f), Vector.Abs(value));
        var step = Vector.AsVectorUInt32(Vector.Max(value, Vector.AsVectorSingle(new Vector<uint>(0x3880_0000u))));
        step = (step & new Vector<uint>(0x7F80_0000u)) + new Vector<uint>(0x0680_0000u);
        var bits = Vector.AsVectorUInt32(value + Vector.AsVectorSingle(step));

        // Rebias the exponent and shift the rounded bits into a half's. A NaN takes an all-ones
        // exponent and keeps the top of its payload.
        bits -= new Vector<uint>(0x3F00_0000u);
        var exponent = Vector.ShiftRightLogical(bits, 13);
        var nanExponent = Vector.AndNot(new Vector<uint>(0x7C00u), real);
        return ((bits & real) + exponent) | nanExponent | sign;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void NearestRow(
        in Source source,
        in ImageToTensorPlan plan,
        int dy,
        (int First, int End) columns,
        in Scratch scratch,
        in Channels channels,
        bool vector,
        Span<float> r,
        Span<float> g,
        Span<float> b)
    {
        int first = columns.First;
        int end = columns.End;
        channels.Pad(r[..first], g[..first], b[..first]);
        channels.Pad(r[end..], g[end..], b[end..]);

        nint row = source.RowOffset(NearestIndex(plan.SourceY(0, dy), source.Height - 1));
        var packed = scratch.Packed0.AsSpan(first, end - first);
        var offsets = scratch.Offset0.AsSpan(first, end - first);
        for (int i = 0; i < packed.Length; i++)
        {
            packed[i] = source.Read(row + offsets[i]);
        }

        ConvertNearest(packed, channels, vector, r[first..end], g[first..end], b[first..end]);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BilinearRow(
        in Source source,
        in ImageToTensorPlan plan,
        int dy,
        (int First, int End) columns,
        in Scratch scratch,
        in Channels channels,
        bool vector,
        Span<float> r,
        Span<float> g,
        Span<float> b)
    {
        int first = columns.First;
        int end = columns.End;
        int count = end - first;
        channels.Pad(r[..first], g[..first], b[..first]);
        channels.Pad(r[end..], g[end..], b[end..]);

        var (y0, y1, wy) = LinearIndex(plan.SourceY(0, dy), source.Height - 1);
        nint row0 = source.RowOffset(y0);
        nint row1 = source.RowOffset(y1);
        var p00 = scratch.Packed0.AsSpan(first, count);
        var p01 = scratch.Packed1!.AsSpan(first, count);
        var p10 = scratch.Packed2!.AsSpan(first, count);
        var p11 = scratch.Packed3!.AsSpan(first, count);
        var left = scratch.Offset0.AsSpan(first, count);
        var right = scratch.Offset1!.AsSpan(first, count);
        for (int i = 0; i < count; i++)
        {
            p00[i] = source.Read(row0 + left[i]);
            p01[i] = source.Read(row0 + right[i]);
            p10[i] = source.Read(row1 + left[i]);
            p11[i] = source.Read(row1 + right[i]);
        }

        var rowWeights = scratch.WeightY!.AsSpan(first, count);
        rowWeights.Fill(wy);
        ConvertBilinear(
            p00, p01, p10, p11, scratch.Weight!.AsSpan(first, count), rowWeights, channels, vector,
            r[first..end], g[first..end], b[first..end]);
    }

    /// <summary>
    /// A row of a rotated crop, sampled bilinearly: each pixel's position computed as
    /// <see cref="GeneralRow"/> computes it, the four frame pixels around it read into the scratch
    /// buffers with its weights, then the row converted as the table path converts, and the pixels
    /// outside the fitted crop or the frame padded after.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RotatedBilinearRow(
        in Source source,
        in ImageToTensorPlan plan,
        int dy,
        bool padOutside,
        in Scratch scratch,
        in Channels channels,
        bool vector,
        Span<float> r,
        Span<float> g,
        Span<float> b)
    {
        int count = r.Length;
        int maxX = source.Width - 1;
        int maxY = source.Height - 1;
        var padded = scratch.Padded!.AsSpan(0, count);
        var p00 = scratch.Packed0.AsSpan(0, count);
        var p01 = scratch.Packed1!.AsSpan(0, count);
        var p10 = scratch.Packed2!.AsSpan(0, count);
        var p11 = scratch.Packed3!.AsSpan(0, count);
        var columnWeights = scratch.Weight!.AsSpan(0, count);
        var rowWeights = scratch.WeightY!.AsSpan(0, count);
        bool anyPadded = false;
        for (int dx = 0; dx < count; dx++)
        {
            double x = plan.SourceX(dx, dy);
            double y = plan.SourceY(dx, dy);
            bool pad = !plan.CoversColumn(dx)
                || (padOutside && !(Inside(x, source.Width) && Inside(y, source.Height)));
            padded[dx] = pad;
            anyPadded |= pad;

            // A padded pixel reads its clamped neighbours too; its value is overwritten below.
            var (x0, x1, wx) = LinearIndex(x, maxX);
            var (y0, y1, wy) = LinearIndex(y, maxY);
            nint row0 = source.RowOffset(y0);
            nint row1 = source.RowOffset(y1);
            p00[dx] = source.Read(row0 + x0 * BytesPerPixel);
            p01[dx] = source.Read(row0 + x1 * BytesPerPixel);
            p10[dx] = source.Read(row1 + x0 * BytesPerPixel);
            p11[dx] = source.Read(row1 + x1 * BytesPerPixel);
            columnWeights[dx] = wx;
            rowWeights[dx] = wy;
        }

        ConvertBilinear(p00, p01, p10, p11, columnWeights, rowWeights, channels, vector, r, g, b);

        if (anyPadded)
        {
            for (int dx = 0; dx < count; dx++)
            {
                if (padded[dx])
                {
                    r[dx] = channels.PadRed;
                    g[dx] = channels.PadGreen;
                    b[dx] = channels.PadBlue;
                }
            }
        }
    }

    // One loop per sampling. A single loop that branched on it per pixel compiled, without a
    // profile, with the bilinear work inlined into the nearest path, which was 16% slower (#546).
    private static void GeneralRow(
        in Source source,
        in ImageToTensorPlan plan,
        int dy,
        bool bilinear,
        bool padOutside,
        in Channels channels,
        Span<float> r,
        Span<float> g,
        Span<float> b)
    {
        if (bilinear)
        {
            GeneralBilinearRow(source, plan, dy, padOutside, channels, r, g, b);
        }
        else
        {
            GeneralNearestRow(source, plan, dy, padOutside, channels, r, g, b);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void GeneralNearestRow(
        in Source source,
        in ImageToTensorPlan plan,
        int dy,
        bool padOutside,
        in Channels channels,
        Span<float> r,
        Span<float> g,
        Span<float> b)
    {
        int maxX = source.Width - 1;
        int maxY = source.Height - 1;
        for (int dx = 0; dx < r.Length; dx++)
        {
            double x = plan.SourceX(dx, dy);
            double y = plan.SourceY(dx, dy);
            if (!plan.CoversColumn(dx)
                || (padOutside && !(Inside(x, source.Width) && Inside(y, source.Height))))
            {
                r[dx] = channels.PadRed;
                g[dx] = channels.PadGreen;
                b[dx] = channels.PadBlue;
                continue;
            }

            uint p = source.Read(
                source.RowOffset(NearestIndex(y, maxY)) + NearestIndex(x, maxX) * BytesPerPixel);
            r[dx] = NearestValue(p, channels.ShiftRed, channels.ScaleRed, channels.OffsetRed);
            g[dx] = NearestValue(p, channels.ShiftGreen, channels.ScaleGreen, channels.OffsetGreen);
            b[dx] = NearestValue(p, channels.ShiftBlue, channels.ScaleBlue, channels.OffsetBlue);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void GeneralBilinearRow(
        in Source source,
        in ImageToTensorPlan plan,
        int dy,
        bool padOutside,
        in Channels channels,
        Span<float> r,
        Span<float> g,
        Span<float> b)
    {
        int maxX = source.Width - 1;
        int maxY = source.Height - 1;
        for (int dx = 0; dx < r.Length; dx++)
        {
            double x = plan.SourceX(dx, dy);
            double y = plan.SourceY(dx, dy);
            if (!plan.CoversColumn(dx)
                || (padOutside && !(Inside(x, source.Width) && Inside(y, source.Height))))
            {
                r[dx] = channels.PadRed;
                g[dx] = channels.PadGreen;
                b[dx] = channels.PadBlue;
                continue;
            }

            var (x0, x1, wx) = LinearIndex(x, maxX);
            var (y0, y1, wy) = LinearIndex(y, maxY);
            nint row0 = source.RowOffset(y0);
            nint row1 = source.RowOffset(y1);
            uint p00 = source.Read(row0 + x0 * BytesPerPixel);
            uint p01 = source.Read(row0 + x1 * BytesPerPixel);
            uint p10 = source.Read(row1 + x0 * BytesPerPixel);
            uint p11 = source.Read(row1 + x1 * BytesPerPixel);
            r[dx] = BilinearValue(p00, p01, p10, p11, wx, wy, channels.ShiftRed, channels.ScaleRed, channels.OffsetRed);
            g[dx] = BilinearValue(p00, p01, p10, p11, wx, wy, channels.ShiftGreen, channels.ScaleGreen, channels.OffsetGreen);
            b[dx] = BilinearValue(p00, p01, p10, p11, wx, wy, channels.ShiftBlue, channels.ScaleBlue, channels.OffsetBlue);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertNearest(
        ReadOnlySpan<uint> packed,
        in Channels channels,
        bool vector,
        Span<float> r,
        Span<float> g,
        Span<float> b)
    {
        int i = 0;
        if (vector)
        {
            var mask = new Vector<uint>(0xFFu);
            var scaleR = new Vector<float>(channels.ScaleRed);
            var scaleG = new Vector<float>(channels.ScaleGreen);
            var scaleB = new Vector<float>(channels.ScaleBlue);
            var offsetR = new Vector<float>(channels.OffsetRed);
            var offsetG = new Vector<float>(channels.OffsetGreen);
            var offsetB = new Vector<float>(channels.OffsetBlue);
            for (; i <= packed.Length - Vector<uint>.Count; i += Vector<uint>.Count)
            {
                var p = new Vector<uint>(packed[i..]);
                (Sample(p, channels.ShiftRed, mask) * scaleR + offsetR).CopyTo(r[i..]);
                (Sample(p, channels.ShiftGreen, mask) * scaleG + offsetG).CopyTo(g[i..]);
                (Sample(p, channels.ShiftBlue, mask) * scaleB + offsetB).CopyTo(b[i..]);
            }
        }

        for (; i < packed.Length; i++)
        {
            uint p = packed[i];
            r[i] = NearestValue(p, channels.ShiftRed, channels.ScaleRed, channels.OffsetRed);
            g[i] = NearestValue(p, channels.ShiftGreen, channels.ScaleGreen, channels.OffsetGreen);
            b[i] = NearestValue(p, channels.ShiftBlue, channels.ScaleBlue, channels.OffsetBlue);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertBilinear(
        ReadOnlySpan<uint> p00,
        ReadOnlySpan<uint> p01,
        ReadOnlySpan<uint> p10,
        ReadOnlySpan<uint> p11,
        ReadOnlySpan<float> weights,
        ReadOnlySpan<float> rowWeights,
        in Channels channels,
        bool vector,
        Span<float> r,
        Span<float> g,
        Span<float> b)
    {
        int i = 0;
        if (vector)
        {
            var mask = new Vector<uint>(0xFFu);
            var scaleR = new Vector<float>(channels.ScaleRed);
            var scaleG = new Vector<float>(channels.ScaleGreen);
            var scaleB = new Vector<float>(channels.ScaleBlue);
            var offsetR = new Vector<float>(channels.OffsetRed);
            var offsetG = new Vector<float>(channels.OffsetGreen);
            var offsetB = new Vector<float>(channels.OffsetBlue);
            for (; i <= p00.Length - Vector<uint>.Count; i += Vector<uint>.Count)
            {
                var a = new Vector<uint>(p00[i..]);
                var c = new Vector<uint>(p01[i..]);
                var d = new Vector<uint>(p10[i..]);
                var e = new Vector<uint>(p11[i..]);
                var columnWeight = new Vector<float>(weights[i..]);
                var rowWeight = new Vector<float>(rowWeights[i..]);
                (Lerp(a, c, d, e, columnWeight, rowWeight, channels.ShiftRed, mask) * scaleR + offsetR).CopyTo(r[i..]);
                (Lerp(a, c, d, e, columnWeight, rowWeight, channels.ShiftGreen, mask) * scaleG + offsetG).CopyTo(g[i..]);
                (Lerp(a, c, d, e, columnWeight, rowWeight, channels.ShiftBlue, mask) * scaleB + offsetB).CopyTo(b[i..]);
            }
        }

        for (; i < p00.Length; i++)
        {
            float wx = weights[i];
            float wy = rowWeights[i];
            r[i] = BilinearValue(p00[i], p01[i], p10[i], p11[i], wx, wy, channels.ShiftRed, channels.ScaleRed, channels.OffsetRed);
            g[i] = BilinearValue(p00[i], p01[i], p10[i], p11[i], wx, wy, channels.ShiftGreen, channels.ScaleGreen, channels.OffsetGreen);
            b[i] = BilinearValue(p00[i], p01[i], p10[i], p11[i], wx, wy, channels.ShiftBlue, channels.ScaleBlue, channels.OffsetBlue);
        }
    }

    // The vector form of BilinearValue before normalization, in the same order.
    private static Vector<float> Lerp(
        Vector<uint> p00,
        Vector<uint> p01,
        Vector<uint> p10,
        Vector<uint> p11,
        Vector<float> wx,
        Vector<float> wy,
        int shift,
        Vector<uint> mask)
    {
        var s00 = Sample(p00, shift, mask);
        var s01 = Sample(p01, shift, mask);
        var s10 = Sample(p10, shift, mask);
        var s11 = Sample(p11, shift, mask);
        var top = s00 + (s01 - s00) * wx;
        var bottom = s10 + (s11 - s10) * wx;
        return top + (bottom - top) * wy;
    }

    private static Vector<float> Sample(Vector<uint> packed, int shift, Vector<uint> mask)
        => Vector.ConvertToSingle(Vector.AsVectorInt32(Vector.ShiftRightLogical(packed, shift) & mask));

    private static float Sample(uint packed, int shift) => (int)((packed >> shift) & 0xFFu);

    private static float NearestValue(uint packed, int shift, float scale, float offset)
        => Sample(packed, shift) * scale + offset;

    private static float BilinearValue(
        uint p00,
        uint p01,
        uint p10,
        uint p11,
        float wx,
        float wy,
        int shift,
        float scale,
        float offset)
    {
        float s00 = Sample(p00, shift);
        float s01 = Sample(p01, shift);
        float s10 = Sample(p10, shift);
        float s11 = Sample(p11, shift);
        float top = s00 + (s01 - s00) * wx;
        float bottom = s10 + (s11 - s10) * wx;
        return (top + (bottom - top) * wy) * scale + offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Interleave(
        ReadOnlySpan<float> first,
        ReadOnlySpan<float> second,
        ReadOnlySpan<float> third,
        Span<float> destination)
    {
        for (int i = 0; i < first.Length; i++)
        {
            destination[3 * i] = first[i];
            destination[3 * i + 1] = second[i];
            destination[3 * i + 2] = third[i];
        }
    }

    /// <summary>The pixel a position falls in, with the edge pixel repeated outside.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int NearestIndex(double position, int max) => Clamp(Math.Floor(position), max);

    /// <summary>
    /// The two pixels whose centres straddle a position, and the weight of the second. Pixel
    /// <c>i</c>'s centre is at <c>i + ½</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int I0, int I1, float Weight) LinearIndex(double position, int max)
    {
        double centred = position - 0.5;
        double floor = Math.Floor(centred);
        return (Clamp(floor, max), Clamp(floor + 1, max), (float)(centred - floor));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(double index, int max)
        => !(index > 0) ? 0 : index >= max ? max : (int)index;

    /// <summary>Whether a position falls in one of <paramref name="size"/> pixels, each covering <c>[i, i + 1)</c>.</summary>
    internal static bool Inside(double position, int size) => position >= 0 && position < size;

    private readonly ref struct Source
    {
        private readonly ref byte _origin;
        private readonly int _stride;

        public Source(ReadOnlySpan<byte> pixels, int width, int height, int stride)
        {
            _origin = ref MemoryMarshal.GetReference(pixels);
            _stride = stride;
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        public nint RowOffset(int y) => (nint)y * _stride;

        // Offsets come from indices clamped to the frame, which the caller checked fits the span.
        public uint Read(nint offset) => Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref _origin, offset));
    }

    /// <summary>
    /// Fills the per-column tables in <paramref name="scratch"/>: a byte offset and, for bilinear, a
    /// second offset and a weight. Returns the range of columns inside the fitted crop and, when
    /// <paramref name="padOutside"/> is set, inside the frame. An unrotated crop's frame x rises
    /// with the column, so both ranges are contiguous and so is their overlap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static (int First, int End) BuildColumns(
        in ImageToTensorPlan plan, int width, bool bilinear, bool padOutside, in Scratch scratch)
    {
        int first = 0;
        while (first < plan.TensorWidth && !ReadsColumn(plan, first, width, padOutside))
        {
            first++;
        }

        int end = first;
        while (end < plan.TensorWidth && ReadsColumn(plan, end, width, padOutside))
        {
            end++;
        }

        int max = width - 1;
        for (int dx = first; dx < end; dx++)
        {
            double x = plan.SourceX(dx, 0);
            if (bilinear)
            {
                var (x0, x1, weight) = LinearIndex(x, max);
                scratch.Offset0[dx] = x0 * BytesPerPixel;
                scratch.Offset1![dx] = x1 * BytesPerPixel;
                scratch.Weight![dx] = weight;
            }
            else
            {
                scratch.Offset0[dx] = NearestIndex(x, max) * BytesPerPixel;
            }
        }

        return (first, end);
    }

    private static bool ReadsColumn(in ImageToTensorPlan plan, int dx, int width, bool padOutside)
        => plan.CoversColumn(dx) && (!padOutside || Inside(plan.SourceX(dx, 0), width));

    /// <summary>Pooled per-call buffers, sized to one tensor row.</summary>
    private readonly struct Scratch
    {
        private readonly float[]? _colours;
        private readonly float[]? _interleaved;
        private readonly int _width;

        public Scratch(int width, bool colours, bool interleaved, bool bilinear, bool rotated)
        {
            _width = width;
            Offset0 = ArrayPool<int>.Shared.Rent(width);
            Packed0 = ArrayPool<uint>.Shared.Rent(width);
            if (rotated)
            {
                Padded = ArrayPool<bool>.Shared.Rent(width);
            }

            if (bilinear)
            {
                Offset1 = ArrayPool<int>.Shared.Rent(width);
                Weight = ArrayPool<float>.Shared.Rent(width);
                WeightY = ArrayPool<float>.Shared.Rent(width);
                Packed1 = ArrayPool<uint>.Shared.Rent(width);
                Packed2 = ArrayPool<uint>.Shared.Rent(width);
                Packed3 = ArrayPool<uint>.Shared.Rent(width);
            }

            if (colours)
            {
                _colours = ArrayPool<float>.Shared.Rent(3 * width);
            }

            if (interleaved)
            {
                _interleaved = ArrayPool<float>.Shared.Rent(3 * width);
            }
        }

        public int[] Offset0 { get; }

        public int[]? Offset1 { get; }

        public float[]? Weight { get; }

        public float[]? WeightY { get; }

        public bool[]? Padded { get; }

        public uint[] Packed0 { get; }

        public uint[]? Packed1 { get; }

        public uint[]? Packed2 { get; }

        public uint[]? Packed3 { get; }

        public Span<float> Red => _colours.AsSpan(0, _width);

        public Span<float> Green => _colours.AsSpan(_width, _width);

        public Span<float> Blue => _colours.AsSpan(2 * _width, _width);

        /// <summary>A row of pixels, interleaved, before it is converted.</summary>
        public Span<float> Interleaved => _interleaved.AsSpan(0, 3 * _width);

        public void Return()
        {
            ArrayPool<int>.Shared.Return(Offset0);
            ArrayPool<uint>.Shared.Return(Packed0);
            if (Offset1 is not null)
            {
                ArrayPool<int>.Shared.Return(Offset1);
                ArrayPool<float>.Shared.Return(Weight!);
                ArrayPool<float>.Shared.Return(WeightY!);
                ArrayPool<uint>.Shared.Return(Packed1!);
                ArrayPool<uint>.Shared.Return(Packed2!);
                ArrayPool<uint>.Shared.Return(Packed3!);
            }

            if (_colours is not null)
            {
                ArrayPool<float>.Shared.Return(_colours);
            }

            if (_interleaved is not null)
            {
                ArrayPool<float>.Shared.Return(_interleaved);
            }

            if (Padded is not null)
            {
                ArrayPool<bool>.Shared.Return(Padded);
            }
        }
    }

    /// <summary>Where each colour sits in a packed pixel, and how each is normalized.</summary>
    private readonly struct Channels
    {
        public Channels(bool bgra, TensorNormalization normalization, byte padValue)
        {
            // Byte i of a pixel is bits 8i to 8i + 7 of a little-endian uint.
            ShiftRed = Shift(bgra ? 2 : 0);
            ShiftGreen = Shift(1);
            ShiftBlue = Shift(bgra ? 0 : 2);
            (ScaleRed, OffsetRed) = normalization.Red;
            (ScaleGreen, OffsetGreen) = normalization.Green;
            (ScaleBlue, OffsetBlue) = normalization.Blue;
            float pad = padValue;
            PadRed = pad * ScaleRed + OffsetRed;
            PadGreen = pad * ScaleGreen + OffsetGreen;
            PadBlue = pad * ScaleBlue + OffsetBlue;

            static int Shift(int byteIndex) => BitConverter.IsLittleEndian ? 8 * byteIndex : 24 - 8 * byteIndex;
        }

        public int ShiftRed { get; }

        public int ShiftGreen { get; }

        public int ShiftBlue { get; }

        public float ScaleRed { get; }

        public float ScaleGreen { get; }

        public float ScaleBlue { get; }

        public float OffsetRed { get; }

        public float OffsetGreen { get; }

        public float OffsetBlue { get; }

        public float PadRed { get; }

        public float PadGreen { get; }

        public float PadBlue { get; }

        public void Pad(Span<float> r, Span<float> g, Span<float> b)
        {
            r.Fill(PadRed);
            g.Fill(PadGreen);
            b.Fill(PadBlue);
        }
    }
}
