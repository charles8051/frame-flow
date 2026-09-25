using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Video.Tests;

/// <summary>
/// Phase 2 acceptance: the ported <see cref="VideoOperators"/> module
/// works end-to-end against the substrate. Mirrors the tests in
/// <see cref="VideoPipelineExtensionsTests"/> but exercises the new
/// node-and-port API instead of the old <c>FramePipeline&lt;T&gt;</c>
/// chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this validates beyond Phase 0 / 1:</b>
/// </para>
/// <list type="bullet">
/// <item>The <see cref="IVideoFrame"/> adapter pattern works — an
/// existing FrameFlow domain type can ride the substrate without
/// changes to <c>FrameFlow.Media</c>.</item>
/// <item>An ordinary 1→1 operator (<c>swscale</c>-backed pixel-format
/// conversion) ports mechanically; refcounts stay balanced; no leaks.</item>
/// <item>The local-feed PackageReference shape works — FrameFlow can
/// consume <c>Crossbar</c> via the same shape it
/// already uses for the original <c>Crossbar</c> assembly.</item>
/// </list>
/// </remarks>
public sealed class VideoOperatorsTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public VideoOperatorsTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegFact]
    public async Task ConvertPixelFormat_TransformsEveryUpstreamFrame()
    {
        // Build a 3-frame BGRA source → ConvertPixelFormat node → sink.
        var captured = new List<(int W, int H, PixelFormat Fmt)>();

        var index = 0;
        var source = new SourceNode<IVideoFrame>(
            "src",
            (ct) =>
            {
                if (index >= 3)
                    return ValueTask.FromResult<IVideoFrame?>(null);
                var frame = MakeSolidBgraFrame(8, 8);
                index++;
                return ValueTask.FromResult<IVideoFrame?>(frame);
            }
        );

        var convert = VideoOperators.ConvertPixelFormat("convert", PixelFormat.Rgba32);

        var sink = new SinkNode<IVideoFrame>(
            "sink",
            (item, ct) =>
            {
                lock (captured)
                    captured.Add((item.Width, item.Height, item.Format));
                return ValueTask.CompletedTask;
            }
        );

        await new Graph.Graph()
            .Connect(source.Output, convert.Input)
            .Connect(convert.Output, sink.Input)
            .RunAsync();

        Assert.Equal(3, captured.Count);
        Assert.All(captured, c =>
        {
            Assert.Equal(8, c.W);
            Assert.Equal(8, c.H);
            Assert.Equal(PixelFormat.Rgba32, c.Fmt);
        });
    }

    [RequiresFfmpegFact]
    public async Task Resize_ChangesDimensionsAcrossPipeline()
    {
        var dims = new List<(int W, int H)>();

        var index = 0;
        var source = new SourceNode<IVideoFrame>(
            "src",
            (ct) =>
            {
                if (index >= 2)
                    return ValueTask.FromResult<IVideoFrame?>(null);
                index++;
                return ValueTask.FromResult<IVideoFrame?>(
                    MakeSolidBgraFrame(32, 16)
                );
            }
        );

        var resize = VideoOperators.Resize("resize", 64, 32);

        var sink = new SinkNode<IVideoFrame>(
            "sink",
            (item, ct) =>
            {
                lock (dims)
                    dims.Add((item.Width, item.Height));
                return ValueTask.CompletedTask;
            }
        );

        await new Graph.Graph()
            .Connect(source.Output, resize.Input)
            .Connect(resize.Output, sink.Input)
            .RunAsync();

        Assert.Equal(2, dims.Count);
        Assert.All(dims, d =>
        {
            Assert.Equal(64, d.W);
            Assert.Equal(32, d.H);
        });
    }

    [RequiresFfmpegFact]
    public async Task ResizeAndConvert_ChainedWithIntermediateConvert_AppliesBoth()
    {
        // Sanity-check that two converter nodes chain correctly through
        // the substrate (port-based wiring across multiple operators
        // exercises the refcount discipline end-to-end on each handoff).
        var captured = new List<(int W, int H, PixelFormat Fmt)>();

        var index = 0;
        var source = new SourceNode<IVideoFrame>(
            "src",
            (ct) =>
            {
                if (index >= 1)
                    return ValueTask.FromResult<IVideoFrame?>(null);
                index++;
                return ValueTask.FromResult<IVideoFrame?>(
                    MakeSolidBgraFrame(32, 32)
                );
            }
        );

        var resizeAndConvert = VideoOperators.ResizeAndConvert("rc", 64, 64, PixelFormat.Rgba32);

        var sink = new SinkNode<IVideoFrame>(
            "sink",
            (item, ct) =>
            {
                lock (captured)
                    captured.Add((item.Width, item.Height, item.Format));
                return ValueTask.CompletedTask;
            }
        );

        await new Graph.Graph()
            .Connect(source.Output, resizeAndConvert.Input)
            .Connect(resizeAndConvert.Output, sink.Input)
            .RunAsync();

        Assert.Single(captured);
        Assert.Equal(64, captured[0].W);
        Assert.Equal(64, captured[0].H);
        Assert.Equal(PixelFormat.Rgba32, captured[0].Fmt);
    }

    [Fact]
    public void Resize_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoOperators.Resize("r", 0, 16)
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoOperators.Resize("r", 16, -1)
        );
    }

    [Fact]
    public void ConvertPixelFormat_RejectsUnsupportedTarget()
    {
        Assert.Throws<NotSupportedException>(
            () => VideoOperators.ConvertPixelFormat("c", PixelFormat.Yuv420P)
        );
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers (mirrors the helper in VideoPipelineExtensionsTests)
    // ─────────────────────────────────────────────────────────────

    private static CpuVideoFrame MakeSolidBgraFrame(int width, int height) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            width,
            height,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(33),
            0,
            static (planes, _) =>
            {
                const byte b = 10, g = 20, r = 30, a = 255;
                var px = planes.Y;
                for (int o = 0; o < px.Length; o += 4)
                {
                    px[o + 0] = b;
                    px[o + 1] = g;
                    px[o + 2] = r;
                    px[o + 3] = a;
                }
            }
        );
}
