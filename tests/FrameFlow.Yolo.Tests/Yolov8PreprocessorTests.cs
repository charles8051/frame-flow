using FrameFlow.Media;
using Xunit;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// The tensor <see cref="Yolov8Preprocessor"/> writes, and the scale factors it returns for
/// <see cref="Yolov8Postprocessor"/> to map boxes back with.
/// </summary>
public sealed class Yolov8PreprocessorTests
{
    [Theory]
    [InlineData(PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Rgba32)]
    public void Preprocess_WritesRgbPlanesInZeroToOne(PixelFormat format)
    {
        using var frame = Frame(format, 64, 64, (_, _) => (b: 51, g: 102, r: 255));
        var pre = new Yolov8Preprocessor(inputSize: 32);
        var tensor = new float[pre.InputElementCount];

        pre.Preprocess(frame, tensor);

        const int plane = 32 * 32;
        Assert.All(tensor[..plane], v => Assert.Equal(1f, v, 1e-6f));
        Assert.All(tensor[plane..(2 * plane)], v => Assert.Equal(0.4f, v, 1e-6f));
        Assert.All(tensor[(2 * plane)..], v => Assert.Equal(0.2f, v, 1e-6f));
    }

    /// <summary>
    /// The postprocessor maps a model position <c>p</c> to frame pixels as <c>p · scale</c>. That
    /// is right only if tensor pixel <c>i</c>, centred on <c>i + ½</c>, shows the frame at
    /// <c>(i + ½) · scale</c>. A lone lit pixel has to appear in the tensor pixel the scales map
    /// back onto it.
    /// </summary>
    [Fact]
    public void Preprocess_ReturnsTheScalesThatMapATensorPixelBackOntoWhatItShows()
    {
        // Three frame columns and two frame rows per tensor pixel. Tensor pixel (10, 10) is
        // centred on frame (31.5, 21), inside frame pixel (31, 21).
        using var frame = Frame(PixelFormat.Bgra32, 96, 64, (x, y) => x == 31 && y == 21 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));
        var pre = new Yolov8Preprocessor(inputSize: 32);
        var tensor = new float[pre.InputElementCount];

        var (scaleX, scaleY) = pre.Preprocess(frame, tensor);

        Assert.Equal((3f, 2f), (scaleX, scaleY));
        Assert.Equal(1f, tensor[10 * 32 + 10]);
        Assert.Equal(1, tensor[..(32 * 32)].Count(v => v != 0));
        Assert.Equal(31, (int)MathF.Floor(10.5f * scaleX));
        Assert.Equal(21, (int)MathF.Floor(10.5f * scaleY));
    }

    [Fact]
    public void Preprocess_RefusesAShortDestination()
    {
        using var frame = Frame(PixelFormat.Bgra32, 32, 32, (_, _) => (0, 0, 0));
        var pre = new Yolov8Preprocessor(inputSize: 32);

        Assert.Throws<ArgumentException>(() => pre.Preprocess(frame, new float[pre.InputElementCount - 1]));
    }

    private static CpuVideoFrame Frame(
        PixelFormat format,
        int width,
        int height,
        Func<int, int, (byte b, byte g, byte r)> paint)
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
                        var (b, g, r) = state.paint(x, y);
                        int o = y * planes.StrideY + x * 4;
                        planes.Y[o] = bgra ? b : r;
                        planes.Y[o + 1] = g;
                        planes.Y[o + 2] = bgra ? r : b;
                        planes.Y[o + 3] = 255;
                    }
                }
            });
}
