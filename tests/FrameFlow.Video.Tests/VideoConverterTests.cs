using System.Buffers;
using FrameFlow.Media;
using FrameFlow.Video;

namespace FrameFlow.Video.Tests;

/// <summary>
/// Tests for <see cref="IVideoConverter"/> / the swscale-backed
/// <c>SwScaleVideoConverter</c>. Need real FFmpeg loaded — gated on
/// the <see cref="FfmpegBootstrapFixture"/>.
/// </summary>
public sealed class VideoConverterTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public VideoConverterTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    // ─────────────────────────────────────────────────────────────────
    // Factory contract
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_AllNullArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() => VideoConverter.Create());
    }

    [Fact]
    public void Create_NegativeWidth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoConverter.Create(targetWidth: 0, targetHeight: 16)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoConverter.Create(targetWidth: -1, targetHeight: 16)
        );
    }

    [Fact]
    public void Create_NegativeHeight_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoConverter.Create(targetWidth: 16, targetHeight: 0)
        );
    }

    [Fact]
    public void Create_UnsupportedTargetFormat_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            VideoConverter.Create(targetFormat: PixelFormat.Yuv420P)
        );
        Assert.Throws<NotSupportedException>(() =>
            VideoConverter.Create(targetFormat: PixelFormat.Nv12)
        );
    }

    [Fact]
    public void Create_SupportedTargetFormat_Succeeds()
    {
        using var c1 = VideoConverter.Create(targetFormat: PixelFormat.Bgra32);
        Assert.Null(c1.TargetWidth);
        Assert.Null(c1.TargetHeight);
        Assert.Equal(PixelFormat.Bgra32, c1.TargetFormat);

        using var c2 = VideoConverter.Create(targetFormat: PixelFormat.Rgba32);
        Assert.Equal(PixelFormat.Rgba32, c2.TargetFormat);
    }

    // ─────────────────────────────────────────────────────────────────
    // Conversion correctness — on synthetic BGRA input
    // ─────────────────────────────────────────────────────────────────

    [RequiresFfmpegFact]
    public void Process_BgraToRgba_SwapsByteOrder()
    {
        // 1x1 BGRA frame with B=10, G=20, R=30, A=255.
        // After Bgra32→Rgba32 conversion: R=30, G=20, B=10, A=255.
        using var src = MakeSolidBgraFrame(1, 1, b: 10, g: 20, r: 30, a: 255);
        using var converter = VideoConverter.Create(targetFormat: PixelFormat.Rgba32);

        using var dst = converter.Process(src);

        Assert.Equal(PixelFormat.Rgba32, dst.Format);
        Assert.Equal(src.Width, dst.Width);
        Assert.Equal(src.Height, dst.Height);
        var px = dst.PixelData.Memory.Span;
        Assert.Equal(30, px[0]); // R
        Assert.Equal(20, px[1]); // G
        Assert.Equal(10, px[2]); // B
        Assert.Equal(255, px[3]); // A
    }

    [RequiresFfmpegFact]
    public void Process_BgraToBgra_PassesThroughBytes()
    {
        // Same-format conversion is a useful regression case: ensure the
        // bytes are intact even when the conversion is a no-op (still
        // copied via swscale, but values should be identical).
        using var src = MakeSolidBgraFrame(2, 2, b: 5, g: 10, r: 15, a: 200);
        using var converter = VideoConverter.Create(targetFormat: PixelFormat.Bgra32);

        using var dst = converter.Process(src);

        Assert.Equal(PixelFormat.Bgra32, dst.Format);
        var px = dst.PixelData.Memory.Span;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(5, px[i * 4 + 0]);
            Assert.Equal(10, px[i * 4 + 1]);
            Assert.Equal(15, px[i * 4 + 2]);
            Assert.Equal(200, px[i * 4 + 3]);
        }
    }

    [RequiresFfmpegFact]
    public void Process_ResizeUp_PreservesSolidColor()
    {
        // Resizing a solid-color frame up shouldn't change the color —
        // every output pixel should match the input. Catches any
        // colorspace miscoding in the swscale wiring.
        using var src = MakeSolidBgraFrame(4, 4, b: 50, g: 100, r: 150, a: 255);
        using var converter = VideoConverter.Create(targetWidth: 16, targetHeight: 16);

        using var dst = converter.Process(src);

        Assert.Equal(16, dst.Width);
        Assert.Equal(16, dst.Height);
        Assert.Equal(PixelFormat.Bgra32, dst.Format);

        var px = dst.PixelData.Memory.Span;
        for (int row = 0; row < 16; row++)
        {
            for (int col = 0; col < 16; col++)
            {
                int o = row * dst.Stride + col * 4;
                Assert.Equal(50, px[o + 0]);
                Assert.Equal(100, px[o + 1]);
                Assert.Equal(150, px[o + 2]);
                Assert.Equal(255, px[o + 3]);
            }
        }
    }

    [RequiresFfmpegFact]
    public void Process_ResizeDown_PreservesSolidColor()
    {
        using var src = MakeSolidBgraFrame(16, 16, b: 64, g: 96, r: 128, a: 255);
        using var converter = VideoConverter.Create(targetWidth: 4, targetHeight: 4);

        using var dst = converter.Process(src);

        Assert.Equal(4, dst.Width);
        Assert.Equal(4, dst.Height);
        var px = dst.PixelData.Memory.Span;
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                int o = row * dst.Stride + col * 4;
                Assert.Equal(64, px[o + 0]);
                Assert.Equal(96, px[o + 1]);
                Assert.Equal(128, px[o + 2]);
                Assert.Equal(255, px[o + 3]);
            }
        }
    }

    [RequiresFfmpegFact]
    public void Process_ResizeAndConvert_AppliesBothTransforms()
    {
        using var src = MakeSolidBgraFrame(8, 8, b: 1, g: 2, r: 3, a: 255);
        using var converter = VideoConverter.Create(
            targetWidth: 32,
            targetHeight: 32,
            targetFormat: PixelFormat.Rgba32
        );

        using var dst = converter.Process(src);

        Assert.Equal(32, dst.Width);
        Assert.Equal(32, dst.Height);
        Assert.Equal(PixelFormat.Rgba32, dst.Format);

        // Sample a middle pixel — solid color so any pixel should do.
        var px = dst.PixelData.Memory.Span;
        int mid = (16 * dst.Stride) + (16 * 4);
        Assert.Equal(3, px[mid + 0]); // R
        Assert.Equal(2, px[mid + 1]); // G
        Assert.Equal(1, px[mid + 2]); // B
        Assert.Equal(255, px[mid + 3]);
    }

    [RequiresFfmpegFact]
    public void Process_PreservesPts()
    {
        var pts = TimeSpan.FromSeconds(2.5);
        using var src = MakeSolidBgraFrame(4, 4, b: 0, g: 0, r: 0, a: 255, pts: pts);
        using var converter = VideoConverter.Create(targetFormat: PixelFormat.Rgba32);

        using var dst = converter.Process(src);

        Assert.Equal(pts, dst.Pts);
    }

    [RequiresFfmpegFact]
    public void Process_AfterDispose_Throws()
    {
        using var src = MakeSolidBgraFrame(4, 4, b: 0, g: 0, r: 0, a: 255);
        var converter = VideoConverter.Create(targetFormat: PixelFormat.Rgba32);
        converter.Dispose();

        Assert.Throws<ObjectDisposedException>(() => converter.Process(src));
    }

    [RequiresFfmpegFact]
    public void Process_DimensionsChangeMidStream_RebuildsContext()
    {
        // Convert a 4x4 frame, then immediately a 16x16 frame. The
        // converter should silently rebuild its sws context.
        using var converter = VideoConverter.Create(targetFormat: PixelFormat.Rgba32);

        using (var small = MakeSolidBgraFrame(4, 4, 10, 20, 30, 255))
        using (var smallOut = converter.Process(small))
        {
            Assert.Equal(4, smallOut.Width);
        }

        using (var big = MakeSolidBgraFrame(16, 16, 10, 20, 30, 255))
        using (var bigOut = converter.Process(big))
        {
            Assert.Equal(16, bigOut.Width);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="CpuVideoFrame"/> filled with a solid BGRA
    /// colour. Stride is tight (width * 4) — no padding.
    /// </summary>
    private static CpuVideoFrame MakeSolidBgraFrame(
        int width,
        int height,
        byte b,
        byte g,
        byte r,
        byte a,
        TimeSpan? pts = null
    )
    {
        int stride = width * 4;
        var owner = MemoryPool<byte>.Shared.Rent(stride * height);
        var span = owner.Memory.Span;
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int o = row * stride + col * 4;
                span[o + 0] = b;
                span[o + 1] = g;
                span[o + 2] = r;
                span[o + 3] = a;
            }
        }

        return new CpuVideoFrame(
            pixelData: owner,
            width: width,
            height: height,
            stride: stride,
            format: PixelFormat.Bgra32,
            presentationTime: pts ?? TimeSpan.Zero
        );
    }
}
