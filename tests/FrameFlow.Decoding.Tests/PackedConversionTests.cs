using FrameFlow.Decoding.Core;
using FrameFlow.Decoding.Internal;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// <see cref="PackedConversion"/> converts yuv420p into tightly packed Bgra32 rows whole at every
/// width (#724). Every pixel holds the picture, not what the storage held, and nothing past the
/// plane is written.
/// </summary>
/// <remarks>
/// The widths cover both ways swscale's x86 converter gets tightly packed rows wrong.
/// <c>width % 16</c> from 1 to 7 leaves the end of every row unwritten (36, 52, 854). From 8 to
/// 15 every row runs into the next and the last runs past the plane (40, 44, 856, 1080). 48 is a
/// whole number of passes and converts in place.
/// </remarks>
public sealed unsafe class PackedConversionTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    // Even, so swscale takes its unscaled YUV-to-RGB converter.
    private const int Height = 4;
    private const int Guard = 64;
    private const byte Fill = 0xAB;

    [RequiresFfmpegTheory]
    [InlineData(36)]
    [InlineData(40)]
    [InlineData(44)]
    [InlineData(48)]
    [InlineData(52)]
    [InlineData(854)]
    [InlineData(856)]
    [InlineData(1080)]
    public void EveryPixelIsThePictureAndNothingPastThePlaneIsWritten(int width)
    {
        Assert.True(fixture.IsBootstrapped);
        var source = new Yuv420PSource(width, Height);
        int stride = width * 4;
        var storage = new byte[stride * Height + Guard];
        storage.AsSpan().Fill(Fill);

        nint sws = FFSwScale.sws_getContext(
            width,
            Height,
            FFSwScale.AvPixFmtYuv420P,
            width,
            Height,
            FFSwScale.AvPixFmtBgra,
            FFSwScale.SwsBilinear,
            nint.Zero,
            nint.Zero,
            nint.Zero
        );
        Assert.NotEqual(nint.Zero, sws);
        try
        {
            var expected = source.ConvertWithRoom(sws);

            int rows = source.Convert(sws, storage.AsSpan(0, stride * Height), width, stride);

            Assert.Equal(Height, rows);
            Assert.Equal(expected, storage[..(stride * Height)]);
            Assert.All(storage[(stride * Height)..], b => Assert.Equal(Fill, b));
        }
        finally
        {
            FFSwScale.sws_freeContext(sws);
        }
    }

    [Theory]
    [InlineData(1, 4, 64)]
    [InlineData(16, 4, 64)]
    [InlineData(17, 4, 128)]
    [InlineData(854, 4, 3456)]
    [InlineData(856, 4, 3456)]
    [InlineData(864, 4, 3456)]
    [InlineData(1080, 4, 4352)]
    [InlineData(1920, 4, 7680)]
    [InlineData(854, 3, 2592)]
    public void ThePitchIsTheWidthInWholePasses(int width, int bytesPerPixel, int pitch) =>
        Assert.Equal(pitch, ConversionPitch.For(width, bytesPerPixel));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    [InlineData(16, 0)]
    public void ThePitchOfNothingIsRefused(int width, int bytesPerPixel) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversionPitch.For(width, bytesPerPixel));

    /// <summary>
    /// A yuv420p picture in planes with room past every row and every plane, as FFmpeg allocates
    /// its own frames, so swscale can read a whole pass beyond the width.
    /// </summary>
    private sealed class Yuv420PSource
    {
        private const int Room = 64;

        private readonly int _width;
        private readonly int _height;
        private readonly int _lumaStride;
        private readonly int _chromaStride;
        private readonly byte[] _y;
        private readonly byte[] _u;
        private readonly byte[] _v;

        public Yuv420PSource(int width, int height)
        {
            _width = width;
            _height = height;
            int chromaWidth = (width + 1) / 2;
            _lumaStride = width + Room;
            _chromaStride = chromaWidth + Room;
            _y = new byte[_lumaStride * height + Room];
            _u = new byte[_chromaStride * (height / 2) + Room];
            _v = new byte[_chromaStride * (height / 2) + Room];

            // Every column differs, so a pixel left alone or taken from the wrong place shows.
            for (int row = 0; row < height; row++)
            {
                for (int x = 0; x < width; x++)
                    _y[row * _lumaStride + x] = (byte)(16 + (x * 3 + row * 5) % 220);
            }

            for (int row = 0; row < height / 2; row++)
            {
                for (int x = 0; x < chromaWidth; x++)
                {
                    _u[row * _chromaStride + x] = (byte)(16 + (x * 11 + row) % 224);
                    _v[row * _chromaStride + x] = (byte)(16 + (x * 17 + row * 3) % 224);
                }
            }
        }

        /// <summary>Converts through <see cref="PackedConversion"/>.</summary>
        public int Convert(nint sws, Span<byte> destination, int width, int stride)
        {
            fixed (byte* y = _y)
            fixed (byte* u = _u)
            fixed (byte* v = _v)
            {
                byte** planes = stackalloc byte*[4];
                int* strides = stackalloc int[4];
                Describe(planes, strides, y, u, v);
                return PackedConversion.Scale(sws, planes, strides, _height, destination, width, stride);
            }
        }

        /// <summary>
        /// Converts with sws_scale directly into rows with a pass to spare, and returns the
        /// tightly packed picture.
        /// </summary>
        public byte[] ConvertWithRoom(nint sws)
        {
            int stride = _width * 4;
            int pitch = ConversionPitch.For(_width, 4) + Room;
            var padded = new byte[pitch * _height];

            fixed (byte* y = _y)
            fixed (byte* u = _u)
            fixed (byte* v = _v)
            fixed (byte* destination = padded)
            {
                byte** planes = stackalloc byte*[4];
                int* strides = stackalloc int[4];
                Describe(planes, strides, y, u, v);

                byte** destinationPlanes = stackalloc byte*[4];
                int* destinationStrides = stackalloc int[4];
                destinationPlanes[0] = destination;
                destinationPlanes[1] = destinationPlanes[2] = destinationPlanes[3] = null;
                destinationStrides[0] = pitch;
                destinationStrides[1] = destinationStrides[2] = destinationStrides[3] = 0;

                int rows = FFSwScale.sws_scale(
                    sws,
                    planes,
                    strides,
                    0,
                    _height,
                    destinationPlanes,
                    destinationStrides
                );
                Assert.Equal(_height, rows);
            }

            var packed = new byte[stride * _height];
            for (int row = 0; row < _height; row++)
                padded.AsSpan(row * pitch, stride).CopyTo(packed.AsSpan(row * stride));
            return packed;
        }

        private void Describe(byte** planes, int* strides, byte* y, byte* u, byte* v)
        {
            planes[0] = y;
            planes[1] = u;
            planes[2] = v;
            planes[3] = null;
            strides[0] = _lumaStride;
            strides[1] = _chromaStride;
            strides[2] = _chromaStride;
            strides[3] = 0;
        }
    }
}
