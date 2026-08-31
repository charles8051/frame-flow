using FrameFlow.Media.Tests.Doubles;

namespace FrameFlow.Media.Tests;

public sealed class CpuVideoFrameTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static CpuVideoFrame MakeFrame(
        FakeMemoryOwner<byte>? owner = null,
        int width = 320,
        int height = 240,
        int stride = 1280,
        PixelFormat format = PixelFormat.Bgra32,
        TimeSpan presentationTime = default
    )
    {
        owner ??= FakeMemoryOwner<byte>.OfLength(stride * height);
        return new CpuVideoFrame(owner, width, height, stride, format, presentationTime);
    }

    // -----------------------------------------------------------------------
    // Constructor — property storage
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_StoresWidth()
    {
        var frame = MakeFrame(width: 1920);
        Assert.Equal(1920, frame.Width);
    }

    [Fact]
    public void Constructor_StoresHeight()
    {
        var frame = MakeFrame(height: 1080);
        Assert.Equal(1080, frame.Height);
    }

    [Fact]
    public void Constructor_StoresStride()
    {
        var frame = MakeFrame(stride: 7680);
        Assert.Equal(7680, frame.Stride);
    }

    [Fact]
    public void Constructor_StoresFormat()
    {
        var frame = MakeFrame(format: PixelFormat.Rgba32);
        Assert.Equal(PixelFormat.Rgba32, frame.Format);
    }

    [Fact]
    public void Constructor_StoresPresentationTime()
    {
        var pts = TimeSpan.FromSeconds(3.5);
        var frame = MakeFrame(presentationTime: pts);
        Assert.Equal(pts, frame.PresentationTime);
    }

    [Fact]
    public void Constructor_StoresPixelData()
    {
        var owner = FakeMemoryOwner<byte>.OfLength(1280);
        var frame = MakeFrame(owner: owner);
        Assert.Same(owner, frame.PixelData);
    }

    // -----------------------------------------------------------------------
    // Interface implementation
    // -----------------------------------------------------------------------

    [Fact]
    public void ImplementsIVideoFrame()
    {
        var frame = MakeFrame();
        Assert.IsAssignableFrom<IVideoFrame>(frame);
    }

    [Fact]
    public void ImplementsIDisposable()
    {
        var frame = MakeFrame();
        Assert.IsAssignableFrom<IDisposable>(frame);
    }

    // -----------------------------------------------------------------------
    // Dispose — delegates to PixelData
    // -----------------------------------------------------------------------

    [Fact]
    public void Dispose_CallsPixelDataDispose()
    {
        var owner = FakeMemoryOwner<byte>.OfLength(1280);
        var frame = MakeFrame(owner: owner);

        Assert.False(owner.IsDisposed);
        frame.Dispose();
        Assert.True(owner.IsDisposed);
    }

    [Fact]
    public void Dispose_PixelDataDisposeCallCount_IsOne_AfterSingleDispose()
    {
        var owner = FakeMemoryOwner<byte>.OfLength(1280);
        var frame = MakeFrame(owner: owner);

        frame.Dispose();

        Assert.Equal(1, owner.DisposeCallCount);
    }

    // -----------------------------------------------------------------------
    // Dispose idempotency (ADR-0012: safe to dispose multiple times)
    // -----------------------------------------------------------------------

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var owner = FakeMemoryOwner<byte>.OfLength(1280);
        var frame = MakeFrame(owner: owner);

        var ex = Record.Exception(() =>
        {
            frame.Dispose();
            frame.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CalledTwice_ForwardsToPixelDataTwice()
    {
        // CpuVideoFrame itself delegates directly to PixelData.Dispose().
        // FakeMemoryOwner is safe to dispose multiple times, so both calls go through.
        var owner = FakeMemoryOwner<byte>.OfLength(1280);
        var frame = MakeFrame(owner: owner);

        frame.Dispose();
        frame.Dispose();

        Assert.Equal(2, owner.DisposeCallCount);
    }

    // -----------------------------------------------------------------------
    // Zero-size frames are edge cases that should construct without error
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_ZeroWidthAndHeight_DoesNotThrow()
    {
        var owner = FakeMemoryOwner<byte>.OfLength(0);
        var ex = Record.Exception(() =>
            new CpuVideoFrame(owner, 0, 0, 0, PixelFormat.Bgra32, TimeSpan.Zero)
        );
        Assert.Null(ex);
    }

    // -----------------------------------------------------------------------
    // Negative / zero presentation time is valid (pre-roll)
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_NegativePresentationTime_StoresCorrectly()
    {
        var pts = TimeSpan.FromMilliseconds(-100);
        var frame = MakeFrame(presentationTime: pts);
        Assert.Equal(pts, frame.PresentationTime);
    }
}
