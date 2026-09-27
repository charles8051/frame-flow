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
    /// same operations in the same order, so they agree bit for bit. Widths that are not a
    /// multiple of the vector width exercise the scalar tail after the vector loop.
    /// </summary>
    [Theory]
    [InlineData(ImageSampling.Nearest, ImageFit.Stretch, TensorLayout.Nchw)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Stretch, TensorLayout.Nchw)]
    [InlineData(ImageSampling.Nearest, ImageFit.Letterbox, TensorLayout.Nhwc)]
    [InlineData(ImageSampling.Bilinear, ImageFit.Letterbox, TensorLayout.Nhwc)]
    public void ThePaths_AgreeBitForBit(ImageSampling sampling, ImageFit fit, TensorLayout layout)
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
        };

        foreach (int tensorWidth in new[] { 1, 29, 64, 67 })
        {
            var options = new ImageToTensorOptions(tensorWidth, 19)
            {
                Sampling = sampling,
                Fit = fit,
                Layout = layout,
                PadValue = 77,
                Normalization = TensorNormalization.MeanStd((0.485f, 0.456f, 0.406f), (0.229f, 0.224f, 0.225f)),
            };

            foreach (var crop in crops)
            {
                var auto = Run(ImageToTensorPath.Auto);
                Assert.Equal(Bits(Run(ImageToTensorPath.General)), Bits(auto));
                Assert.Equal(Bits(Run(ImageToTensorPath.AxisAlignedScalar)), Bits(auto));

                float[] Run(ImageToTensorPath path)
                {
                    var tensor = new float[options.ElementCount];
                    ImageToTensor.Write(pixels, width, height, stride, bgra: true, crop, options, tensor, path);
                    return tensor;
                }
            }
        }

        static int[] Bits(float[] values) => Array.ConvertAll(values, BitConverter.SingleToInt32Bits);
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

    [Fact]
    public void Options_RefuseATensorNoArrayCanHold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageToTensorOptions(int.MaxValue, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageToTensorOptions(Array.MaxLength / 3 + 1, 1));
        Assert.Equal(3 * 640 * 640, new ImageToTensorOptions(640, 640).ElementCount);
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
