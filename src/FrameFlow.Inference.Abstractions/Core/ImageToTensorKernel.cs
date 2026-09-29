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
/// Writes a 32-bit BGRA or RGBA image into a float tensor as an <see cref="ImageToTensorPlan"/>
/// says. It writes only <c>destination</c>, and the same inputs write the same floats on every
/// path.
/// </summary>
/// <remarks>
/// <para>
/// Every path computes a value with the same float operations in the same order, so the paths
/// agree bit for bit. The vector loops mirror the scalar helpers <see cref="NearestValue"/> and
/// <see cref="BilinearValue"/> operation for operation.
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
/// </remarks>
internal static class ImageToTensorKernel
{
    private const int BytesPerPixel = 4;

    /// <summary>
    /// Runs <paramref name="plan"/>. The caller has checked that <paramref name="pixels"/> holds
    /// <paramref name="height"/> rows of <paramref name="stride"/> bytes, the last at least
    /// <c>4 · width</c> long, and that <paramref name="destination"/> holds the whole tensor.
    /// </summary>
    public static void Run(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        bool bgra,
        in ImageToTensorPlan plan,
        ImageToTensorOptions options,
        Span<float> destination,
        ImageToTensorPath path)
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

        var source = new Source(pixels, width, height, stride);
        var scratch = new Scratch(tensorWidth, nhwc, bilinear, rotated: !plan.IsAxisAligned && !general && bilinear);
        try
        {
            (int First, int End) columns = default;
            if (tables)
            {
                columns = BuildColumns(plan, width, bilinear, padOutside, scratch);
            }

            bool vector = path == ImageToTensorPath.Auto && Vector.IsHardwareAccelerated;
            int planeLength = tensorWidth * tensorHeight;
            for (int dy = 0; dy < tensorHeight; dy++)
            {
                Span<float> r, g, b;
                if (nhwc)
                {
                    r = scratch.Red;
                    g = scratch.Green;
                    b = scratch.Blue;
                }
                else
                {
                    int row = dy * tensorWidth;
                    r = destination.Slice((rgb ? 0 : 2 * planeLength) + row, tensorWidth);
                    g = destination.Slice(planeLength + row, tensorWidth);
                    b = destination.Slice((rgb ? 2 * planeLength : 0) + row, tensorWidth);
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
                    var pixelsOut = destination.Slice(dy * tensorWidth * 3, tensorWidth * 3);
                    if (rgb)
                    {
                        Interleave(r, g, b, pixelsOut);
                    }
                    else
                    {
                        Interleave(b, g, r, pixelsOut);
                    }
                }
            }
        }
        finally
        {
            scratch.Return();
        }
    }

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

            if (!bilinear)
            {
                uint p = source.Read(
                    source.RowOffset(NearestIndex(y, maxY)) + NearestIndex(x, maxX) * BytesPerPixel);
                r[dx] = NearestValue(p, channels.ShiftRed, channels.ScaleRed, channels.OffsetRed);
                g[dx] = NearestValue(p, channels.ShiftGreen, channels.ScaleGreen, channels.OffsetGreen);
                b[dx] = NearestValue(p, channels.ShiftBlue, channels.ScaleBlue, channels.OffsetBlue);
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
    internal static int NearestIndex(double position, int max) => Clamp(Math.Floor(position), max);

    /// <summary>
    /// The two pixels whose centres straddle a position, and the weight of the second. Pixel
    /// <c>i</c>'s centre is at <c>i + ½</c>.
    /// </summary>
    internal static (int I0, int I1, float Weight) LinearIndex(double position, int max)
    {
        double centred = position - 0.5;
        double floor = Math.Floor(centred);
        return (Clamp(floor, max), Clamp(floor + 1, max), (float)(centred - floor));
    }

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
        private readonly int _width;

        public Scratch(int width, bool nhwc, bool bilinear, bool rotated)
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

            if (nhwc)
            {
                _colours = ArrayPool<float>.Shared.Rent(3 * width);
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
