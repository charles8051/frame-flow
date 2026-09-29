using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using FrameFlow.Media;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// What <see cref="TensorToImage"/> writes: the inverse normalization, clamping and rounding,
/// layout, channel order and format, single-channel tensors, and agreement between the kernel's
/// paths.
/// </summary>
public sealed class TensorToImageTests
{
    /// <summary>The identity normalization: a tensor value is its own sample.</summary>
    private static readonly TensorNormalization Identity = TensorNormalization.Range(0, 255);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Samples_AreClampedAndRoundedToNearestWithTiesToEven(bool scalar)
    {
        // Enough values that the vector path runs its loop as well as its tail.
        float[] tensor =
        [
            -3f, -0f, 0f, 0.5f, 1.5f, 2.5f, 3.49f, 127.5f, 128.5f, 254.5f, 254.51f, 255.4f,
            300f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e30f, -1e30f,
        ];
        byte[] expected = [0, 0, 0, 0, 2, 2, 3, 128, 128, 254, 255, 255, 255, 0, 255, 0, 255, 0];
        var options = new TensorToImageOptions(tensor.Length, 1) { Channels = 1, Normalization = Identity };
        var image = new byte[4 * tensor.Length];

        TensorToImage.Write(
            tensor, options, image, image.Length, scalar ? TensorToImagePath.Scalar : TensorToImagePath.Auto);

        for (int i = 0; i < tensor.Length; i++)
        {
            Assert.Equal(new[] { expected[i], expected[i], expected[i], (byte)255 }, image[(4 * i)..(4 * i + 4)]);
        }
    }

    [Theory]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nchw, TensorChannelOrder.Rgb, new[] { 30f, 30f, 20f, 20f, 10f, 10f })]
    [InlineData(PixelFormat.Rgba32, TensorLayout.Nchw, TensorChannelOrder.Rgb, new[] { 30f, 30f, 20f, 20f, 10f, 10f })]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nchw, TensorChannelOrder.Bgr, new[] { 10f, 10f, 20f, 20f, 30f, 30f })]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nhwc, TensorChannelOrder.Rgb, new[] { 30f, 20f, 10f, 30f, 20f, 10f })]
    [InlineData(PixelFormat.Rgba32, TensorLayout.Nhwc, TensorChannelOrder.Bgr, new[] { 10f, 20f, 30f, 10f, 20f, 30f })]
    public void LayoutChannelOrderAndFormat_PlaceEachColour(
        PixelFormat format, TensorLayout layout, TensorChannelOrder order, float[] tensor)
    {
        // Red 30, green 20, blue 10, whichever order the tensor and the image store them in.
        var options = new TensorToImageOptions(2, 1)
        {
            Layout = layout,
            ChannelOrder = order,
            Format = format,
            Normalization = Identity,
        };
        var image = new byte[8];

        TensorToImage.Write(tensor, options, image, 8);

        byte[] pixel = format == PixelFormat.Bgra32 ? [10, 20, 30, 255] : [30, 20, 10, 255];
        Assert.Equal(pixel.Concat(pixel).ToArray(), image);
    }

    [Fact]
    public void Normalization_IsUndoneAtTheEndsOfTheRange()
    {
        var image = new byte[8];

        TensorToImage.Write(
            [0f, 1f, 0f, 1f, 0f, 1f],
            new TensorToImageOptions(2, 1) { Format = PixelFormat.Rgba32 },
            image,
            8);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, image);

        TensorToImage.Write(
            [-1f, 1f, -1f, 1f, -1f, 1f],
            new TensorToImageOptions(2, 1) { Format = PixelFormat.Rgba32, Normalization = TensorNormalization.MinusOneToOne },
            image,
            8);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, image);

        // sample = (value · std + mean) · 255, per channel.
        TensorToImage.Write(
            [-2f, 2f, -0.5f, 1.5f, 0f, 1f],
            new TensorToImageOptions(2, 1)
            {
                Format = PixelFormat.Rgba32,
                Normalization = TensorNormalization.MeanStd((0.5f, 0.25f, 0f), (0.25f, 0.5f, 1f)),
            },
            image,
            8);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, image);
    }

    /// <summary>
    /// A range narrow beside its offset: 0 to 255 is 1 to 1 + 21 ulps. The offset is subtracted
    /// before the scale is applied, so the ulp above 1 is sample 255 / 21 ≈ 12.1. Scaling first and
    /// adding a scaled offset loses it: the scaled value is near 1e8, where floats are 8 apart, and
    /// that path writes 16.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AValueBesideALargeOffset_KeepsItsPrecision(bool scalar)
    {
        float top = 1f + 21 * (MathF.BitIncrement(1f) - 1f);
        float[] tensor = [1f, MathF.BitIncrement(1f), top, 1f, 1f, 1f, 1f, 1f, 1f];
        var options = new TensorToImageOptions(tensor.Length, 1)
        {
            Channels = 1,
            Normalization = TensorNormalization.Range(1f, top),
        };
        var image = new byte[4 * tensor.Length];

        TensorToImage.Write(
            tensor, options, image, image.Length, scalar ? TensorToImagePath.Scalar : TensorToImagePath.Auto);

        Assert.Equal(new byte[] { 0, 12, 255 }, new[] { image[0], image[4], image[8] });
    }

    /// <summary>
    /// <see cref="ImageToTensor"/> followed by <see cref="TensorToImage"/> with the same layout,
    /// channel order and normalization gives back every byte of the frame.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nchw, TensorChannelOrder.Rgb, 0)]
    [InlineData(PixelFormat.Rgba32, TensorLayout.Nhwc, TensorChannelOrder.Bgr, 0)]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nhwc, TensorChannelOrder.Rgb, 1)]
    [InlineData(PixelFormat.Rgba32, TensorLayout.Nchw, TensorChannelOrder.Bgr, 1)]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nchw, TensorChannelOrder.Bgr, 2)]
    [InlineData(PixelFormat.Bgra32, TensorLayout.Nhwc, TensorChannelOrder.Rgb, 2)]
    public void ARoundTripThroughImageToTensor_GivesBackEveryByte(
        PixelFormat format, TensorLayout layout, TensorChannelOrder order, int normalizationIndex)
    {
        var normalization = normalizationIndex switch
        {
            0 => TensorNormalization.ZeroToOne,
            1 => TensorNormalization.MinusOneToOne,
            _ => TensorNormalization.MeanStd((0.485f, 0.456f, 0.406f), (0.229f, 0.224f, 0.225f)),
        };

        // Every byte value in every channel, in a different order per channel.
        using var frame = CpuVideoFrame.Create(
            format, 16, 16, TimeSpan.Zero, TimeSpan.Zero, 0,
            static (planes, _) =>
            {
                for (int i = 0; i < 256; i++)
                {
                    int o = (i / 16) * planes.StrideY + (i % 16) * 4;
                    planes.Y[o] = (byte)i;
                    planes.Y[o + 1] = (byte)(255 - i);
                    planes.Y[o + 2] = (byte)(7 * i);
                    planes.Y[o + 3] = 255;
                }
            });

        using var pool = new CpuTensorPool();
        using var tensor = pool.Rent<float>(layout == TensorLayout.Nchw ? new TensorShape(1, 3, 16, 16) : new TensorShape(1, 16, 16, 3));
        ImageToTensor.Write(
            frame,
            RotatedRect.Whole(frame),
            new ImageToTensorOptions(16, 16)
            {
                Sampling = ImageSampling.Nearest,
                Layout = layout,
                ChannelOrder = order,
                Normalization = normalization,
            },
            tensor.Span);

        using var image = TensorToImage.ToFrame(
            tensor,
            new TensorToImageOptions(16, 16)
            {
                Layout = layout,
                ChannelOrder = order,
                Format = format,
                Normalization = normalization,
            },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(40));

        Assert.Equal((format, 16, 16), (image.Format, image.Width, image.Height));
        Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(40)), (image.Pts, image.Duration));
        Assert.Equal(frame.ToCpu().PlaneY.ToArray(), image.ToCpu().PlaneY.ToArray());
    }

    [Theory]
    [InlineData(TensorLayout.Nchw)]
    [InlineData(TensorLayout.Nhwc)]
    public void ASingleChannelTensor_IsWrittenAsGrey(TensorLayout layout)
    {
        var options = new TensorToImageOptions(3, 1) { Channels = 1, Layout = layout, ChannelOrder = TensorChannelOrder.Bgr };
        var image = new byte[12];

        TensorToImage.Write([0f, 0.2f, 1f], options, image, 12);

        Assert.Equal(3, options.ElementCount);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 51, 51, 51, 255, 255, 255, 255, 255 }, image);
    }

    [Fact]
    public void Write_LeavesTheBytesPastEachRowAlone()
    {
        const int stride = 3 * 4 + 5;
        var options = new TensorToImageOptions(3, 2) { Normalization = Identity };
        var image = new byte[stride + 12];
        Array.Fill(image, (byte)0xAB);

        TensorToImage.Write(Enumerable.Range(0, options.ElementCount).Select(i => (float)i).ToArray(), options, image, stride);

        Assert.All(image[12..stride], b => Assert.Equal((byte)0xAB, b));
        // Pixel (0, 1): red plane element 3, green 9, blue 15.
        Assert.Equal(new byte[] { 15, 9, 3, 255 }, image[stride..(stride + 4)]);
    }

    /// <summary>
    /// The vector path and the scalar path do the same float operations in the same order, so they
    /// write the same bytes. Widths that are not a multiple of the vector width exercise the scalar
    /// tail after the vector loop; values below 0, above 255, on a tie, NaN and infinite exercise
    /// the clamp and the rounding.
    /// </summary>
    [Theory]
    [InlineData(TensorLayout.Nchw, TensorChannelOrder.Rgb, PixelFormat.Bgra32, 3)]
    [InlineData(TensorLayout.Nchw, TensorChannelOrder.Bgr, PixelFormat.Rgba32, 3)]
    [InlineData(TensorLayout.Nhwc, TensorChannelOrder.Rgb, PixelFormat.Rgba32, 3)]
    [InlineData(TensorLayout.Nhwc, TensorChannelOrder.Bgr, PixelFormat.Bgra32, 3)]
    [InlineData(TensorLayout.Nchw, TensorChannelOrder.Rgb, PixelFormat.Bgra32, 1)]
    public void ThePaths_AgreeByteForByte(TensorLayout layout, TensorChannelOrder order, PixelFormat format, int channels)
    {
        var random = new Random(470);
        var normalizations = new[]
        {
            Identity,
            TensorNormalization.ZeroToOne,
            TensorNormalization.MinusOneToOne,
            TensorNormalization.MeanStd((0.485f, 0.456f, 0.406f), (0.229f, 0.224f, 0.225f)),
        };

        foreach (int width in new[] { 1, 7, 8, 16, 37, 67, 131 })
        {
            foreach (var normalization in channels == 3 ? normalizations : normalizations[..3])
            {
                var options = new TensorToImageOptions(width, 19)
                {
                    Channels = channels,
                    Layout = layout,
                    ChannelOrder = order,
                    Format = format,
                    Normalization = normalization,
                };
                var tensor = Tensor(random, options);
                int stride = 4 * width + 12;

                var scalar = Run(TensorToImagePath.Scalar);
                Assert.Equal(scalar, Run(TensorToImagePath.Auto));

                byte[] Run(TensorToImagePath path)
                {
                    var image = new byte[stride * options.Height];
                    TensorToImage.Write(tensor, options, image, stride, path);
                    return image;
                }
            }
        }
    }

    [Fact]
    public void Write_RefusesWhatItCannotWrite()
    {
        var options = new TensorToImageOptions(4, 4);
        var tensor = new float[options.ElementCount];
        var image = new byte[64];

        Assert.Throws<ArgumentException>(() => TensorToImage.Write(new float[options.ElementCount - 1], options, image, 16));
        Assert.Throws<ArgumentException>(() => TensorToImage.Write(new float[options.ElementCount + 1], options, image, 16));
        Assert.Throws<ArgumentException>(() => TensorToImage.Write(tensor, options, new byte[63], 16));
        Assert.Throws<ArgumentException>(() => TensorToImage.Write(tensor, options, new byte[64], 15));
        Assert.Throws<ArgumentException>(() => TensorToImage.Write(tensor, options, new byte[64], 17));

        Assert.Throws<NotSupportedException>(() => TensorToImage.Write(tensor, options with { Format = PixelFormat.Nv12 }, image, 16));
        Assert.Throws<ArgumentException>(() => TensorToImage.Write(tensor, options with { Layout = (TensorLayout)7 }, image, 16));
        Assert.Throws<ArgumentException>(() => TensorToImage.Write(tensor, options with { ChannelOrder = (TensorChannelOrder)7 }, image, 16));

        // No inverse: a zero scale, from the default value or from an empty range.
        Assert.Throws<ArgumentException>(() => TensorToImage.Write(tensor, options with { Normalization = default }, image, 16));
        Assert.Throws<ArgumentException>(
            () => TensorToImage.Write(tensor, options with { Normalization = TensorNormalization.Range(1, 1) }, image, 16));

        // Grey needs one normalization for all three channels.
        var grey = options with { Channels = 1 };
        Assert.Throws<ArgumentException>(
            () => TensorToImage.Write(
                new float[grey.ElementCount],
                grey with { Normalization = TensorNormalization.MeanStd((0.485f, 0.456f, 0.406f), (0.229f, 0.224f, 0.225f)) },
                image,
                16));
    }

    [Fact]
    public void ToFrame_RefusesATensorItCannotRead()
    {
        var options = new TensorToImageOptions(4, 4);
        using var pool = new CpuTensorPool();

        using var bytes = pool.Rent<byte>(new TensorShape(1, 3, 4, 4));
        Assert.Throws<ArgumentException>(() => TensorToImage.ToFrame(bytes, options, TimeSpan.Zero, TimeSpan.Zero));

        using var small = pool.Rent<float>(new TensorShape(1, 3, 4, 3));
        Assert.Throws<ArgumentException>(() => TensorToImage.ToFrame(small, options, TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public void Options_RefuseAnImageNoArrayCanHold_AndAChannelCountOtherThanOneOrThree()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorToImageOptions(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorToImageOptions(4, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorToImageOptions(int.MaxValue, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorToImageOptions(Array.MaxLength / 4 + 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorToImageOptions(4, 4) { Channels = 2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorToImageOptions(4, 4) { Channels = 4 });

        var options = new TensorToImageOptions(1920, 1080);
        Assert.Equal(3 * 1920 * 1080, options.ElementCount);
        Assert.Equal(1920 * 1080, (options with { Channels = 1 }).ElementCount);
        Assert.Equal(
            (3, TensorNormalization.ZeroToOne, TensorLayout.Nchw, TensorChannelOrder.Rgb, PixelFormat.Bgra32),
            (options.Channels, options.Normalization, options.Layout, options.ChannelOrder, options.Format));
    }

    /// <summary>
    /// A tensor whose values, un-normalized, spread from below 0 to above 255, with ties, NaN,
    /// infinities and signed zeros mixed in.
    /// </summary>
    private static float[] Tensor(Random random, TensorToImageOptions options)
    {
        var tensor = new float[options.ElementCount];
        int planeLength = options.Width * options.Height;
        for (int i = 0; i < tensor.Length; i++)
        {
            float sample = random.Next(16) switch
            {
                0 => float.NaN,
                1 => random.Next(2) == 0 ? float.PositiveInfinity : float.NegativeInfinity,
                2 => random.Next(2) == 0 ? 0f : -0f,
                3 or 4 or 5 => random.Next(-40, 300) + 0.5f,
                _ => (float)(random.NextDouble() * 360 - 50),
            };

            int channel = options.Channels == 1 ? 0
                : options.Layout == TensorLayout.Nchw ? i / planeLength
                : i % 3;
            if (options.Channels == 3 && options.ChannelOrder == TensorChannelOrder.Bgr)
            {
                channel = 2 - channel;
            }

            var (scale, offset) = channel switch
            {
                0 => options.Normalization.Red,
                1 => options.Normalization.Green,
                _ => options.Normalization.Blue,
            };
            tensor[i] = sample * scale + offset;
        }

        return tensor;
    }
}
