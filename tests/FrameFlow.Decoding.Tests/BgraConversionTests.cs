using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Core;
using FrameFlow.Decoding.Internal;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The same 4:2:0 samples convert to the same Bgra32 whether they arrive planar, as a software
/// decoder produces them, or semi-planar, as a hardware frame reads back (#792).
/// </summary>
/// <remarks>
/// The pictures carry sharp vertical chroma edges and per-sample noise. swscale's general scaler,
/// which it takes from NV12, blends neighbouring chroma rows on even luma rows; its unscaled
/// converter, which it takes from yuv420p, does not. Converted as decoded, those rows differ by
/// most of the chroma swing.
/// </remarks>
public sealed unsafe class BgraConversionTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    [RequiresFfmpegTheory]
    [InlineData(64, 16, AVColorRange.AVCOL_RANGE_MPEG)]
    [InlineData(64, 16, AVColorRange.AVCOL_RANGE_JPEG)]
    [InlineData(854, 480, AVColorRange.AVCOL_RANGE_MPEG)]
    [InlineData(33, 17, AVColorRange.AVCOL_RANGE_UNSPECIFIED)]
    public void Nv12ConvertsAsYuv420P(int width, int height, AVColorRange range)
    {
        Assert.True(fixture.IsBootstrapped);
        var samples = Samples.Create(width, height, bits: 8);

        var planar = ConvertFrame(samples, AVPixelFormat.AV_PIX_FMT_YUV420P, range);
        var semiPlanar = ConvertFrame(samples, AVPixelFormat.AV_PIX_FMT_NV12, range);

        Assert.Equal(planar, semiPlanar);
    }

    /// <summary>
    /// P010 converts as decoded. This holds that swscale still converts it as it converts
    /// yuv420p10le, which the software decoder produces from the same stream.
    /// </summary>
    [RequiresFfmpegTheory]
    [InlineData(64, 16, AVColorRange.AVCOL_RANGE_MPEG)]
    [InlineData(64, 16, AVColorRange.AVCOL_RANGE_JPEG)]
    [InlineData(854, 480, AVColorRange.AVCOL_RANGE_MPEG)]
    [InlineData(33, 17, AVColorRange.AVCOL_RANGE_UNSPECIFIED)]
    public void P010ConvertsAsYuv420P10(int width, int height, AVColorRange range)
    {
        Assert.True(fixture.IsBootstrapped);
        var samples = Samples.Create(width, height, bits: 10);

        var planar = ConvertFrame(samples, AVPixelFormat.AV_PIX_FMT_YUV420P10LE, range);
        var semiPlanar = ConvertFrame(samples, AVPixelFormat.AV_PIX_FMT_P010LE, range);

        Assert.Equal(planar, semiPlanar);
    }

    /// <summary>
    /// A full-range H.264 stream decodes in software as yuvj420p and on hardware as NV12 flagged
    /// full range. Both convert as full range.
    /// </summary>
    [RequiresFfmpegFact]
    public void FullRangeNv12ConvertsAsYuvj420P()
    {
        Assert.True(fixture.IsBootstrapped);
        var samples = Samples.Create(64, 16, bits: 8);

        var software = ConvertFrame(samples, AVPixelFormat.AV_PIX_FMT_YUVJ420P, AVColorRange.AVCOL_RANGE_UNSPECIFIED);
        var hardware = ConvertFrame(samples, AVPixelFormat.AV_PIX_FMT_NV12, AVColorRange.AVCOL_RANGE_JPEG);
        var limited = ConvertFrame(samples, AVPixelFormat.AV_PIX_FMT_NV12, AVColorRange.AVCOL_RANGE_MPEG);

        Assert.Equal(software, hardware);
        Assert.NotEqual(limited, hardware);
    }

    /// <summary>
    /// A limited-range yuv420p frame, the common software case, converts exactly as a plain
    /// <c>sws_getContext</c> context converts it, as it did before the range was set.
    /// </summary>
    [RequiresFfmpegFact]
    public void LimitedRangeYuv420PConvertsAsBefore()
    {
        Assert.True(fixture.IsBootstrapped);
        var samples = Samples.Create(64, 16, bits: 8);
        using var frame = samples.ToFrame(AVPixelFormat.AV_PIX_FMT_YUV420P, AVColorRange.AVCOL_RANGE_MPEG);

        nint plain = FFSwScale.sws_getContext(
            64,
            16,
            FFSwScale.AvPixFmtYuv420P,
            64,
            16,
            FFSwScale.AvPixFmtBgra,
            FFSwScale.SwsBilinear,
            nint.Zero,
            nint.Zero,
            nint.Zero
        );
        Assert.NotEqual(nint.Zero, plain);
        try
        {
            var expected = new byte[64 * 4 * 16];
            var input = new BgraConversionInput((int)AVPixelFormat.AV_PIX_FMT_YUV420P, false, false);
            Assert.Equal(16, BgraConversion.Convert(plain, input, frame.DangerousGetHandle(), expected, 64, 64 * 4));

            Assert.Equal(expected, ConvertFrame(samples, AVPixelFormat.AV_PIX_FMT_YUV420P, AVColorRange.AVCOL_RANGE_MPEG));
        }
        finally
        {
            FFSwScale.sws_freeContext(plain);
        }
    }

    private static byte[] ConvertFrame(Samples samples, AVPixelFormat format, AVColorRange range)
    {
        using var frame = samples.ToFrame(format, range);
        var input = BgraConversionInput.For((int)format, (int)range);
        nint sws = BgraConversion.CreateContext(samples.Width, samples.Height, input);
        Assert.NotEqual(nint.Zero, sws);
        try
        {
            int stride = samples.Width * 4;
            var bgra = new byte[stride * samples.Height];
            int rows = BgraConversion.Convert(sws, input, frame.DangerousGetHandle(), bgra, samples.Width, stride);
            Assert.Equal(samples.Height, rows);
            return bgra;
        }
        finally
        {
            FFSwScale.sws_freeContext(sws);
        }
    }

    /// <summary>One 4:2:0 picture's samples, written into an <c>AVFrame</c> in any layout.</summary>
    private sealed class Samples
    {
        private readonly int _bits;
        private readonly ushort[] _y;
        private readonly ushort[] _u;
        private readonly ushort[] _v;

        private Samples(int width, int height, int bits, ushort[] y, ushort[] u, ushort[] v)
        {
            Width = width;
            Height = height;
            _bits = bits;
            _y = y;
            _u = u;
            _v = v;
        }

        public int Width { get; }

        public int Height { get; }

        private int ChromaWidth => (Width + 1) / 2;

        private int ChromaHeight => (Height + 1) / 2;

        /// <summary>
        /// Luma noise, and chroma that is noise in the left half and in the right half alternates
        /// between opposite extremes every chroma row, so every chroma row boundary is an edge.
        /// </summary>
        public static Samples Create(int width, int height, int bits)
        {
            int scale = 1 << (bits - 8);
            int chromaWidth = (width + 1) / 2;
            int chromaHeight = (height + 1) / 2;
            var rng = new Random(792);
            var y = new ushort[width * height];
            var u = new ushort[chromaWidth * chromaHeight];
            var v = new ushort[chromaWidth * chromaHeight];

            for (int i = 0; i < y.Length; i++)
                y[i] = (ushort)rng.Next(16 * scale, 236 * scale);
            for (int row = 0; row < chromaHeight; row++)
            {
                for (int column = 0; column < chromaWidth; column++)
                {
                    int i = row * chromaWidth + column;
                    if (column < chromaWidth / 2)
                    {
                        u[i] = (ushort)rng.Next(16 * scale, 241 * scale);
                        v[i] = (ushort)rng.Next(16 * scale, 241 * scale);
                    }
                    else
                    {
                        bool even = row % 2 == 0;
                        u[i] = (ushort)((even ? 16 : 240) * scale);
                        v[i] = (ushort)((even ? 240 : 16) * scale);
                    }
                }
            }

            return new Samples(width, height, bits, y, u, v);
        }

        public FrameHandle ToFrame(AVPixelFormat format, AVColorRange range)
        {
            nint ptr = FFAvUtil.av_frame_alloc();
            Assert.NotEqual(nint.Zero, ptr);
            var handle = new FrameHandle(ptr);
            ref AVFrame f = ref Unsafe.AsRef<AVFrame>((void*)ptr);
            f.width = Width;
            f.height = Height;
            f.format = (int)format;
            f.color_range = range;
            Assert.Equal(0, FFAvUtil.av_frame_get_buffer(ptr, 0));

            bool semiPlanar = format is AVPixelFormat.AV_PIX_FMT_NV12 or AVPixelFormat.AV_PIX_FMT_P010LE;
            bool wide = _bits > 8;
            // P010 holds each sample in the top 10 bits.
            int shift = format == AVPixelFormat.AV_PIX_FMT_P010LE ? 6 : 0;

            for (int row = 0; row < Height; row++)
            {
                for (int column = 0; column < Width; column++)
                    Write(f.data[0], f.linesize[0], row, column, wide, (ushort)(_y[row * Width + column] << shift));
            }

            for (int row = 0; row < ChromaHeight; row++)
            {
                for (int column = 0; column < ChromaWidth; column++)
                {
                    int i = row * ChromaWidth + column;
                    ushort u = (ushort)(_u[i] << shift);
                    ushort v = (ushort)(_v[i] << shift);
                    if (semiPlanar)
                    {
                        Write(f.data[1], f.linesize[1], row, 2 * column, wide, u);
                        Write(f.data[1], f.linesize[1], row, 2 * column + 1, wide, v);
                    }
                    else
                    {
                        Write(f.data[1], f.linesize[1], row, column, wide, u);
                        Write(f.data[2], f.linesize[2], row, column, wide, v);
                    }
                }
            }

            return handle;
        }

        private static void Write(byte* plane, int stride, int row, int column, bool wide, ushort value)
        {
            if (wide)
                ((ushort*)(plane + row * stride))[column] = value;
            else
                plane[row * stride + column] = (byte)value;
        }
    }
}
