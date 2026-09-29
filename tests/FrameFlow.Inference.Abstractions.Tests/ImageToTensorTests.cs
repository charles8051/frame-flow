using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using FrameFlow.Media;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// What <see cref="ImageToTensor"/> writes: sampling, fit, normalization, layout and channel
/// order on small hand-painted frames, and agreement between the kernel's paths.
/// </summary>
public sealed class ImageToTensorTests
{
    private const float Tolerance = 1e-6f;

    [Fact]
    public void NearestAtTheFramesOwnSize_CopiesEveryPixel()
    {
        using var frame = Frame(PixelFormat.Bgra32, 5, 3, (x, y) => ((byte)(10 * x), (byte)(20 * y), (byte)(x + y), 255));
        var options = new ImageToTensorOptions(5, 3) { Sampling = ImageSampling.Nearest };
        var tensor = new float[options.ElementCount];

        ImageToTensor.Write(frame, RotatedRect.Whole(frame), options, tensor);

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                int i = y * 5 + x;
                Assert.Equal((x + y) / 255f, tensor[i], Tolerance);      // R plane
                Assert.Equal(20 * y / 255f, tensor[15 + i], Tolerance);  // G plane
                Assert.Equal(10 * x / 255f, tensor[30 + i], Tolerance);  // B plane
            }
        }
    }

    [Theory]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nchw, TensorChannelOrder.Rgb, new[] { 30f, 30f, 20f, 20f, 10f, 10f })]
    [InlineData(PixelFormat.Rgba32, TensorLayout.Nchw, TensorChannelOrder.Rgb, new[] { 30f, 30f, 20f, 20f, 10f, 10f })]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nchw, TensorChannelOrder.Bgr, new[] { 10f, 10f, 20f, 20f, 30f, 30f })]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nhwc, TensorChannelOrder.Rgb, new[] { 30f, 20f, 10f, 30f, 20f, 10f })]
    [InlineData(PixelFormat.Rgba32, TensorLayout.Nhwc, TensorChannelOrder.Bgr, new[] { 10f, 20f, 30f, 10f, 20f, 30f })]
    public void LayoutAndChannelOrder_PlaceEachColour(
        PixelFormat format, TensorLayout layout, TensorChannelOrder order, float[] expected)
    {
        // Red 30, green 20, blue 10, whichever byte order the frame stores them in.
        using var frame = Frame(format, 2, 1, (_, _) => (10, 20, 30, 255));
        var options = new ImageToTensorOptions(2, 1)
        {
            Layout = layout,
            ChannelOrder = order,
            Normalization = TensorNormalization.Range(0, 255),
        };
        var tensor = new float[options.ElementCount];

        ImageToTensor.Write(frame, RotatedRect.Whole(frame), options, tensor);

        Assert.Equal(expected, tensor, (a, b) => MathF.Abs(a - b) < 1e-4f);
    }

    [Fact]
    public void Normalization_MapsTheEndsOfTheByteRange()
    {
        using var frame = Frame(PixelFormat.Bgra32, 2, 1, (x, _) => x == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        var tensor = new float[6];

        ImageToTensor.Write(
            frame, RotatedRect.Whole(frame),
            new ImageToTensorOptions(2, 1) { Sampling = ImageSampling.Nearest, Normalization = TensorNormalization.MinusOneToOne },
            tensor);
        Assert.Equal(new[] { -1f, 1f, -1f, 1f, -1f, 1f }, tensor, (a, b) => MathF.Abs(a - b) < Tolerance);

        ImageToTensor.Write(
            frame, RotatedRect.Whole(frame),
            new ImageToTensorOptions(2, 1)
            {
                Sampling = ImageSampling.Nearest,
                Normalization = TensorNormalization.MeanStd((0.5f, 0.25f, 0f), (0.25f, 0.5f, 1f)),
            },
            tensor);
        // (sample / 255 - mean) / std.
        Assert.Equal(new[] { -2f, 2f, -0.5f, 1.5f, 0f, 1f }, tensor, (a, b) => MathF.Abs(a - b) < Tolerance);
    }

    [Fact]
    public void Bilinear_WeighsThePixelsWhoseCentresStraddleTheSample()
    {
        // Red 0 then 255. Four tensor pixels sample at frame x 0.25, 0.75, 1.25 and 1.75; the
        // pixel centres are at 0.5 and 1.5, and positions outside them repeat the edge.
        using var frame = Frame(PixelFormat.Bgra32, 2, 1, (x, _) => (0, 0, (byte)(255 * x), 255));
        var options = new ImageToTensorOptions(4, 1) { Normalization = TensorNormalization.Range(0, 255) };
        var tensor = new float[options.ElementCount];

        ImageToTensor.Write(frame, RotatedRect.Whole(frame), options, tensor);

        Assert.Equal(new[] { 0f, 63.75f, 191.25f, 255f }, tensor[..4], (a, b) => MathF.Abs(a - b) < 1e-3f);
    }

    [Fact]
    public void Letterbox_FillsTheBarsWithThePadValue()
    {
        // A 4 x 2 frame in a 4 x 4 tensor: rows 1 and 2 are the frame, rows 0 and 3 are bars.
        using var frame = Frame(PixelFormat.Bgra32, 4, 2, (_, _) => (0, 0, 255, 255));
        var options = new ImageToTensorOptions(4, 4)
        {
            Fit = ImageFit.Letterbox,
            PadValue = 51,
            Normalization = TensorNormalization.Range(0, 255),
        };
        var tensor = new float[options.ElementCount];

        ImageToTensor.Write(frame, RotatedRect.Whole(frame), options, tensor);

        var red = tensor.AsSpan(0, 16).ToArray();
        Assert.All(red[..4], v => Assert.Equal(51f, v, 1e-3f));
        Assert.All(red[4..12], v => Assert.Equal(255f, v, 1e-3f));
        Assert.All(red[12..], v => Assert.Equal(51f, v, 1e-3f));
    }

    [Fact]
    public void ACropPastTheEdge_RepeatsTheEdgePixel()
    {
        // Red is the column index. The crop starts at column 2 and runs two columns past the edge.
        using var frame = Frame(PixelFormat.Bgra32, 4, 1, (x, _) => (0, 0, (byte)x, 255));
        var options = new ImageToTensorOptions(4, 1)
        {
            Sampling = ImageSampling.Nearest,
            Normalization = TensorNormalization.Range(0, 255),
        };
        var tensor = new float[options.ElementCount];

        ImageToTensor.Write(frame, RotatedRect.FromBounds(2, 0, 4, 1), options, tensor);

        Assert.Equal(new[] { 2f, 3f, 3f, 3f }, tensor[..4], (a, b) => MathF.Abs(a - b) < 1e-3f);
    }

    [Theory]
    [InlineData(ImageSampling.Nearest)]
    [InlineData(ImageSampling.Bilinear)]
    public void ACropPastTheEdge_WithPad_WritesThePadValueThere(ImageSampling sampling)
    {
        // Red is the column index. The crop starts at column 2 and runs two columns past the edge;
        // the last two tensor pixels' centres, x = 4.5 and 5.5, are outside the frame.
        using var frame = Frame(PixelFormat.Bgra32, 4, 1, (x, _) => (0, 0, (byte)x, 255));
        var options = new ImageToTensorOptions(4, 1)
        {
            Sampling = sampling,
            Border = ImageBorder.Pad,
            PadValue = 9,
            Normalization = TensorNormalization.Range(0, 255),
        };
        var tensor = new float[options.ElementCount];

        ImageToTensor.Write(frame, RotatedRect.FromBounds(2, 0, 4, 1), options, tensor);

        Assert.Equal(new[] { 2f, 3f, 9f, 9f }, tensor[..4], (a, b) => MathF.Abs(a - b) < 1e-3f);
    }

    [Fact]
    public void ARotatedCropPastTheEdge_WithPad_PadsEachPixelOutside()
    {
        // A quarter-turned 4 x 4 crop centred on the frame's top-left corner: only the tensor
        // pixels whose centres land in the frame's top-left 2 x 2 read it.
        using var frame = Frame(PixelFormat.Bgra32, 4, 4, (x, y) => (0, 0, (byte)(100 + 4 * y + x), 255));
        var options = new ImageToTensorOptions(4, 4)
        {
            Sampling = ImageSampling.Nearest,
            Border = ImageBorder.Pad,
            Normalization = TensorNormalization.Range(0, 255),
        };
        var tensor = new float[options.ElementCount];

        var transform = ImageToTensor.Write(frame, new RotatedRect(0, 0, 4, 4, MathF.PI / 2), options, tensor);

        for (int ty = 0; ty < 4; ty++)
        {
            for (int tx = 0; tx < 4; tx++)
            {
                var (x, y) = transform.ToFrame((tx + 0.5f) / 4, (ty + 0.5f) / 4);
                float expected = x is >= 0 and < 4 && y is >= 0 and < 4
                    ? 100 + 4 * (int)MathF.Floor(y) + (int)MathF.Floor(x)
                    : 0;
                Assert.Equal(expected, tensor[4 * ty + tx], 1e-3f);
            }
        }

        Assert.Equal(4, tensor[..16].Count(v => v != 0));
    }

    [Fact]
    public void AQuarterTurnCrop_WritesTheCropUpright()
    {
        // Red is a distinct value per pixel. Turning the crop a quarter clockwise turns its
        // content a quarter anticlockwise: tensor (x, y) reads frame (3 - y, x).
        using var frame = Frame(PixelFormat.Bgra32, 4, 4, (x, y) => (0, 0, (byte)(4 * y + x), 255));
        var options = new ImageToTensorOptions(4, 4)
        {
            Sampling = ImageSampling.Nearest,
            Normalization = TensorNormalization.Range(0, 255),
        };
        var tensor = new float[options.ElementCount];

        ImageToTensor.Write(frame, new RotatedRect(2, 2, 4, 4, MathF.PI / 2), options, tensor);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(4 * x + (3 - y), tensor[4 * y + x], 1e-3f);
            }
        }
    }

    [Fact]
    public void Transform_IsWhatWriteReturns()
    {
        using var frame = Frame(PixelFormat.Bgra32, 64, 48, (_, _) => (0, 0, 0, 255));
        var options = new ImageToTensorOptions(16, 16) { Fit = ImageFit.Letterbox };
        var crop = new RotatedRect(20, 15, 30, 12, 0.4f);

        var written = ImageToTensor.Write(frame, crop, options, new float[options.ElementCount]);

        Assert.Equal(written, ImageToTensor.Transform(crop, options));
        Assert.Throws<ArgumentException>(() => ImageToTensor.Transform(RotatedRect.FromBounds(0, 0, 0, 4), options));
    }

    [Fact]
    public void TheReturnedTransform_MapsTheTensorOntoTheCrop()
    {
        using var frame = Frame(PixelFormat.Bgra32, 64, 48, (_, _) => (0, 0, 0, 255));
        var options = new ImageToTensorOptions(16, 16);

        var transform = ImageToTensor.Write(
            frame, RotatedRect.FromBounds(8, 4, 32, 16), options, new float[options.ElementCount]);

        Assert.Equal((8f, 4f), transform.ToFrame(0, 0));
        Assert.Equal((40f, 20f), transform.ToFrame(1, 1));
    }

    /// <summary>
    /// The vector path, the scalar table path and the per-pixel path compute each value with the
    /// same operations in the same order, and convert it to a half or a byte the same way, so they
    /// agree bit for bit. Widths that are not a multiple of the vector width exercise the scalar
    /// tail after the vector loop.
    /// </summary>
    [Theory]
    [InlineData(ImageSampling.Nearest, ImageFit.Stretch, TensorLayout.Nchw, ImageBorder.Replicate, DType.Float32)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Stretch, TensorLayout.Nchw, ImageBorder.Replicate, DType.Float32)]
    [InlineData(ImageSampling.Nearest, ImageFit.Letterbox, TensorLayout.Nhwc, ImageBorder.Replicate, DType.Float32)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Letterbox, TensorLayout.Nhwc, ImageBorder.Replicate, DType.Float32)]
    [InlineData(ImageSampling.Nearest, ImageFit.Stretch, TensorLayout.Nchw, ImageBorder.Pad, DType.Float32)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Letterbox, TensorLayout.Nhwc, ImageBorder.Pad, DType.Float32)]
    [InlineData(ImageSampling.Nearest, ImageFit.Stretch, TensorLayout.Nchw, ImageBorder.Replicate, DType.Float16)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Stretch, TensorLayout.Nchw, ImageBorder.Replicate, DType.Float16)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Letterbox, TensorLayout.Nhwc, ImageBorder.Pad, DType.Float16)]
    [InlineData(ImageSampling.Nearest, ImageFit.Stretch, TensorLayout.Nchw, ImageBorder.Pad, DType.UInt8)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Stretch, TensorLayout.Nchw, ImageBorder.Replicate, DType.UInt8)]
    [InlineData(ImageSampling.Nearest, ImageFit.Letterbox, TensorLayout.Nhwc, ImageBorder.Replicate, DType.UInt8)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Letterbox, TensorLayout.Nhwc, ImageBorder.Pad, DType.UInt8)]
    public void ThePaths_AgreeBitForBit(
        ImageSampling sampling, ImageFit fit, TensorLayout layout, ImageBorder border, DType dtype)
    {
        var random = new Random(363);
        const int width = 37, height = 23, stride = width * 4 + 12;
        var pixels = new byte[stride * height];
        random.NextBytes(pixels);

        var crops = new[]
        {
            RotatedRect.FromBounds(0, 0, width, height),
            RotatedRect.FromBounds(3.3f, 1.7f, 20.9f, 11.2f),
            RotatedRect.FromBounds(-5f, 10f, 50f, 30f),
            RotatedRect.FromBounds(30f, 20f, 2.5f, 1.5f),
            RotatedRect.FromBounds(-12.3f, -7.9f, 20f, 15f),
            new RotatedRect(18.5f, 11.5f, 30f, 17f, 0.3f),
            new RotatedRect(2f, 20f, 24f, 24f, -2.1f),
        };

        foreach (int tensorWidth in new[] { 1, 29, 64, 67 })
        {
            var options = new ImageToTensorOptions(tensorWidth, 19)
            {
                Sampling = sampling,
                Fit = fit,
                Layout = layout,
                Border = border,
                PadValue = 77,
                Dtype = dtype,
                Normalization = dtype == DType.UInt8
                    ? TensorNormalization.Range(0, 255)
                    : TensorNormalization.MeanStd((0.485f, 0.456f, 0.406f), (0.229f, 0.224f, 0.225f)),
            };

            foreach (var crop in crops)
            {
                var auto = Run(ImageToTensorPath.Auto);
                Assert.Equal(Run(ImageToTensorPath.General), auto);
                Assert.Equal(Run(ImageToTensorPath.Scalar), auto);

                byte[] Run(ImageToTensorPath path) => dtype switch
                {
                    DType.Float16 => Bits<Half>(path),
                    DType.UInt8 => Bits<byte>(path),
                    _ => Bits<float>(path),
                };

                byte[] Bits<T>(ImageToTensorPath path)
                    where T : unmanaged
                {
                    var tensor = new T[options.ElementCount];
                    ImageToTensor.Write<T>(pixels, width, height, stride, bgra: true, crop, options, tensor, path);
                    return MemoryMarshal.AsBytes(tensor.AsSpan()).ToArray();
                }
            }
        }
    }

    /// <summary>
    /// A UInt8 tensor at the frame's own size, sampled nearest, holds the frame's bytes, each in the
    /// plane or position its colour takes.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nchw, TensorChannelOrder.Rgb)]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nchw, TensorChannelOrder.Bgr)]
    [InlineData(PixelFormat.Rgba32, TensorLayout.Nchw, TensorChannelOrder.Rgb)]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nhwc, TensorChannelOrder.Rgb)]
    [InlineData(PixelFormat.Rgba32, TensorLayout.Nhwc, TensorChannelOrder.Bgr)]
    public void UInt8Nearest_HoldsTheFramesBytes(PixelFormat format, TensorLayout layout, TensorChannelOrder order)
    {
        // Wider than a vector of bytes, and not a multiple of one.
        const int width = 45, height = 7;
        var random = new Random(480);
        var colours = new (byte B, byte G, byte R, byte A)[width * height];
        for (int i = 0; i < colours.Length; i++)
        {
            colours[i] = ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
        }

        using var frame = Frame(format, width, height, (x, y) => colours[y * width + x]);
        var options = new ImageToTensorOptions(width, height)
        {
            Sampling = ImageSampling.Nearest,
            Normalization = TensorNormalization.Range(0, 255),
            Dtype = DType.UInt8,
            Layout = layout,
            ChannelOrder = order,
        };
        var tensor = new byte[options.ElementCount];

        ImageToTensor.Write(frame, RotatedRect.Whole(frame), options, tensor);

        bool rgb = order == TensorChannelOrder.Rgb;
        int plane = width * height;
        for (int i = 0; i < plane; i++)
        {
            var (b, g, r, _) = colours[i];
            var (first, second, third) = rgb ? (r, g, b) : (b, g, r);
            if (layout == TensorLayout.Nchw)
            {
                Assert.Equal((first, second, third), (tensor[i], tensor[plane + i], tensor[2 * plane + i]));
            }
            else
            {
                Assert.Equal((first, second, third), (tensor[3 * i], tensor[3 * i + 1], tensor[3 * i + 2]));
            }
        }
    }

    /// <summary>
    /// A UInt8 element is the Float32 value, with samples as they are, rounded to nearest with ties
    /// to even. Nearest sampling gives whole values, so it copies the frame's byte; bilinear gives
    /// fractions, which round.
    /// </summary>
    [Theory]
    [InlineData(ImageSampling.Nearest, TensorLayout.Nchw)]
    [InlineData(ImageSampling.Bilinear, TensorLayout.Nchw)]
    [InlineData(ImageSampling.Bilinear, TensorLayout.Nhwc)]
    public void UInt8_IsTheFloatValueRounded(ImageSampling sampling, TensorLayout layout)
    {
        using var frame = RandomFrame(61, 43, seed: 481);
        var floatOptions = new ImageToTensorOptions(67, 29)
        {
            Sampling = sampling,
            Layout = layout,
            Border = ImageBorder.Pad,
            PadValue = 114,
            Normalization = TensorNormalization.Range(0, 255),
        };
        var byteOptions = floatOptions with { Dtype = DType.UInt8 };
        int fractions = 0;

        foreach (var crop in Crops)
        {
            var floats = new float[floatOptions.ElementCount];
            var bytes = new byte[byteOptions.ElementCount];
            ImageToTensor.Write(frame, crop, floatOptions, floats);
            ImageToTensor.Write(frame, crop, byteOptions, bytes);

            Assert.Equal(Array.ConvertAll(floats, v => (byte)MathF.Round(v, MidpointRounding.ToEven)), bytes);
            fractions += floats.Count(v => v != MathF.Floor(v));
        }

        if (sampling == ImageSampling.Nearest)
        {
            Assert.Equal(0, fractions);
        }
        else
        {
            Assert.True(fractions > 1000, $"{fractions} fractional values");
        }
    }

    /// <summary>A Float16 element is the Float32 value converted to <see cref="Half"/>.</summary>
    [Theory]
    [InlineData(ImageSampling.Nearest, TensorLayout.Nhwc)]
    [InlineData(ImageSampling.Bilinear, TensorLayout.Nchw)]
    public void Float16_IsTheFloatValueConverted(ImageSampling sampling, TensorLayout layout)
    {
        using var frame = RandomFrame(61, 43, seed: 482);
        var floatOptions = new ImageToTensorOptions(67, 29)
        {
            Sampling = sampling,
            Layout = layout,
            Fit = ImageFit.Letterbox,
            PadValue = 114,
            Normalization = TensorNormalization.MeanStd((0.485f, 0.456f, 0.406f), (0.229f, 0.224f, 0.225f)),
        };
        var halfOptions = floatOptions with { Dtype = DType.Float16 };

        foreach (var crop in Crops)
        {
            var floats = new float[floatOptions.ElementCount];
            var halves = new Half[halfOptions.ElementCount];
            ImageToTensor.Write(frame, crop, floatOptions, floats);
            ImageToTensor.Write(frame, crop, halfOptions, halves);

            Assert.Equal(
                Array.ConvertAll(floats, v => BitConverter.HalfToUInt16Bits((Half)v)),
                Array.ConvertAll(halves, BitConverter.HalfToUInt16Bits));
        }
    }

    /// <summary>
    /// Both of <see cref="ImageToTensorKernel.ToBytes"/>'s loops clamp to 0 to 255 and round to
    /// nearest, ties to even. Each value is repeated so that it lands in a vector lane and in the
    /// scalar tail.
    /// </summary>
    [Fact]
    public void ToBytes_ClampsAndRoundsHalfToEven_OnBothLoops()
    {
        (float Value, byte Expected)[] cases =
        [
            (float.NegativeInfinity, 0), (-3f, 0), (-0.5f, 0), (0f, 0), (0.49999997f, 0), (0.5f, 0),
            (1.5f, 2), (2.5f, 2), (3.5f, 4), (127.5f, 128), (128.5f, 128), (200.50002f, 201),
            (254.5f, 254), (255f, 255), (255.5f, 255), (256f, 255), (1e9f, 255), (float.PositiveInfinity, 255),
        ];
        var values = new float[5 * cases.Length + 3];
        var expected = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            (values[i], expected[i]) = cases[i % cases.Length];
        }

        foreach (bool vector in new[] { false, true })
        {
            var bytes = new byte[values.Length];
            ImageToTensorKernel.ToBytes(values, bytes, vector);
            Assert.Equal(expected, bytes);
        }
    }

    /// <summary>
    /// Both of <see cref="ImageToTensorKernel.ToHalves"/>'s loops give <c>(Half)value</c>'s bits:
    /// for random bit patterns, which cover NaNs and subnormals, and for the edges of a half's
    /// range and precision.
    /// </summary>
    [Fact]
    public void ToHalves_IsTheHalfCast_OnBothLoops()
    {
        float ulp = MathF.Pow(2, -11);
        float tiny = MathF.Pow(2, -25);
        var values = new List<float>
        {
            0f, -0f, 1f, 1f + ulp, 1f + 3 * ulp, -(1f + ulp), 2049f, 2051f, 65504f, 65519.996f, 65520f,
            -65520f, 1e10f, float.MaxValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN,
            -float.NaN, 2 * tiny, tiny, 3 * tiny, tiny / 2, MathF.Pow(2, -14), 6.097555e-5f, float.Epsilon,
        };
        var random = new Random(483);
        while (values.Count < 20_003)
        {
            values.Add(BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue)));
        }

        var expected = values.Select(v => BitConverter.HalfToUInt16Bits((Half)v)).ToArray();
        foreach (bool vector in new[] { false, true })
        {
            var halves = new Half[values.Count];
            ImageToTensorKernel.ToHalves(values.ToArray(), halves, vector);
            Assert.Equal(expected, Array.ConvertAll(halves, BitConverter.HalfToUInt16Bits));
        }
    }

    [Fact]
    public void Write_RefusesADestinationOfAnotherElementType()
    {
        using var frame = Frame(PixelFormat.Bgra32, 4, 4, (_, _) => (0, 0, 0, 255));
        var options = new ImageToTensorOptions(4, 4);
        var bytes = options with { Dtype = DType.UInt8, Normalization = TensorNormalization.Range(0, 255) };
        var crop = RotatedRect.Whole(frame);

        Assert.Equal("destination", Assert.Throws<ArgumentException>(
            () => ImageToTensor.Write(frame, crop, options, new byte[options.ElementCount])).ParamName);
        Assert.Equal("destination", Assert.Throws<ArgumentException>(
            () => ImageToTensor.Write(frame, crop, bytes, new float[options.ElementCount])).ParamName);
        Assert.Equal("destination", Assert.Throws<ArgumentException>(
            () => ImageToTensor.Write(frame, crop, bytes, new Half[options.ElementCount])).ParamName);
        Assert.Equal("destination", Assert.Throws<ArgumentException>(
            () => ImageToTensor.Write(frame, crop, bytes, new byte[options.ElementCount - 1])).ParamName);
    }

    [Fact]
    public void Options_DefaultToFloat32_AndRefuseAnElementTypeTheyCannotWrite()
    {
        var options = new ImageToTensorOptions(4, 4);

        Assert.Equal(DType.Float32, options.Dtype);
        Assert.Throws<ArgumentException>(() => ImageToTensor.ValidateOptions(options with { Dtype = DType.Int32 }));
        Assert.Throws<ArgumentException>(() => ImageToTensor.ValidateOptions(options with { Dtype = (DType)99 }));
        ImageToTensor.ValidateOptions(options with { Dtype = DType.Float16 });
    }

    /// <summary>
    /// A UInt8 tensor holds samples, so the default normalization, which maps them to 0 to 1, is
    /// refused rather than rounded to zeros and ones.
    /// </summary>
    [Fact]
    public void Options_ForUInt8_TakeTheIdentityNormalizationOnly()
    {
        var options = new ImageToTensorOptions(4, 4) { Dtype = DType.UInt8 };

        var error = Assert.Throws<ArgumentException>(() => ImageToTensor.ValidateOptions(options));
        Assert.Contains("Range(0, 255)", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(
            () => ImageToTensor.ValidateOptions(options with { Normalization = TensorNormalization.MeanStd((0, 0, 0), (1, 1, 1)) }));
        ImageToTensor.ValidateOptions(options with { Normalization = TensorNormalization.Range(0, 255) });
    }

    private static readonly RotatedRect[] Crops =
    [
        RotatedRect.FromBounds(0, 0, 61, 43),
        RotatedRect.FromBounds(3.3f, 1.7f, 20.9f, 11.2f),
        RotatedRect.FromBounds(-12.3f, -7.9f, 40f, 25f),
        new RotatedRect(30f, 20f, 50f, 30f, 0.3f),
        new RotatedRect(2f, 40f, 24f, 24f, -2.1f),
    ];

    private static CpuVideoFrame RandomFrame(int width, int height, int seed)
    {
        var random = new Random(seed);
        var colours = new (byte, byte, byte, byte)[width * height];
        for (int i = 0; i < colours.Length; i++)
        {
            colours[i] = ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
        }

        return Frame(PixelFormat.Bgra32, width, height, (x, y) => colours[y * width + x]);
    }

    [Fact]
    public void Write_RefusesWhatItCannotRead()
    {
        using var frame = Frame(PixelFormat.Bgra32, 4, 4, (_, _) => (0, 0, 0, 255));
        var options = new ImageToTensorOptions(4, 4);
        var tensor = new float[options.ElementCount];

        Assert.Throws<ArgumentException>(
            () => ImageToTensor.Write(frame, RotatedRect.Whole(frame), options, new float[options.ElementCount - 1]));
        Assert.Throws<ArgumentException>(
            () => ImageToTensor.Write(frame, RotatedRect.FromBounds(0, 0, 0, 4), options, tensor));
        Assert.Throws<ArgumentException>(
            () => ImageToTensor.Write(frame, new RotatedRect(float.NaN, 2, 4, 4), options, tensor));

        using var nv12 = CpuVideoFrame.Create(
            PixelFormat.Nv12, 4, 4, TimeSpan.Zero, TimeSpan.Zero, 0, static (_, _) => { });
        Assert.Throws<NotSupportedException>(
            () => ImageToTensor.Write(nv12, RotatedRect.Whole(nv12), options, tensor));

        Assert.Throws<ArgumentException>(
            () => ImageToTensor.Write(new byte[10], 4, 4, 16, true, RotatedRect.FromBounds(0, 0, 4, 4), options, tensor, ImageToTensorPath.Auto));
    }

    /// <summary>
    /// A GPU frame's format is its surface's, so the refusal names the domain rather than the
    /// format (#430).
    /// </summary>
    [Fact]
    public void AGpuFrame_IsRefusedForItsDomain_NotItsFormat()
    {
        var options = new ImageToTensorOptions(4, 4);
        var frame = new GpuFrame();

        var ex = Assert.Throws<InvalidOperationException>(
            () => ImageToTensor.Write(frame, RotatedRect.Whole(frame), options, new float[options.ElementCount]));

        Assert.Contains("Gpu frame", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ToCpu", ex.Message, StringComparison.Ordinal);
    }

    private sealed class GpuFrame : IVideoFrame
    {
        public int Width => 4;

        public int Height => 4;

        public TimeSpan Pts => TimeSpan.Zero;

        public TimeSpan Duration => TimeSpan.FromMilliseconds(40);

        public PixelFormat Format => PixelFormat.Nv12;

        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Gpu;

        public IVideoFrame AddRef() => this;

        public CpuFrameData ToCpu() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Options_RefuseATensorNoArrayCanHold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageToTensorOptions(int.MaxValue, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageToTensorOptions(Array.MaxLength / 3 + 1, 1));
        Assert.Equal(3 * 640 * 640, new ImageToTensorOptions(640, 640).ElementCount);
    }

    [Fact]
    public void Options_DefaultToBt601Limited_AndRefuseAnUndefinedYuvMode()
    {
        var options = new ImageToTensorOptions(4, 4);

        Assert.Equal((YuvMatrix.Bt601, YuvRange.Limited), (options.YuvMatrix, options.YuvRange));
        Assert.Throws<ArgumentException>(() => ImageToTensor.ValidateOptions(options with { YuvMatrix = (YuvMatrix)7 }));
        Assert.Throws<ArgumentException>(() => ImageToTensor.ValidateOptions(options with { YuvRange = (YuvRange)7 }));
    }

    [Fact]
    public void Options_DefaultToReplicate_AndRefuseAnUndefinedBorder()
    {
        var options = new ImageToTensorOptions(4, 4);

        Assert.Equal(ImageBorder.Replicate, options.Border);
        Assert.Throws<ArgumentException>(() => ImageToTensor.ValidateOptions(options with { Border = (ImageBorder)7 }));
    }

    private static CpuVideoFrame Frame(
        PixelFormat format,
        int width,
        int height,
        Func<int, int, (byte B, byte G, byte R, byte A)> paint)
        => CpuVideoFrame.Create(
            format,
            width,
            height,
            TimeSpan.Zero,
            TimeSpan.Zero,
            (format, paint),
            static (planes, state) =>
            {
                bool bgra = state.format == PixelFormat.Bgra32;
                for (int y = 0; y < planes.Height; y++)
                {
                    for (int x = 0; x < planes.Width; x++)
                    {
                        var (b, g, r, a) = state.paint(x, y);
                        int o = y * planes.StrideY + x * 4;
                        planes.Y[o] = bgra ? b : r;
                        planes.Y[o + 1] = g;
                        planes.Y[o + 2] = bgra ? r : b;
                        planes.Y[o + 3] = a;
                    }
                }
            });
}
