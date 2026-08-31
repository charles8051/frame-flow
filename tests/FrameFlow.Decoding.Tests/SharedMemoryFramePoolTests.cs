using System.Buffers;
using FrameFlow.Decoding.Internal;

namespace FrameFlow.Decoding.Tests;

public sealed class SharedMemoryFramePoolTests : IClassFixture<FfmpegBootstrapFixture>
{
    private static readonly SharedMemoryFramePool Pool = new();

    // -----------------------------------------------------------------------
    // RentVideoBuffer — basic allocation contract
    // -----------------------------------------------------------------------

    [Fact]
    public void RentVideoBuffer_ReturnsNonNull()
    {
        using var owner = Pool.RentVideoBuffer(320, 240, 4);
        Assert.NotNull(owner);
    }

    [Fact]
    public void RentVideoBuffer_MemoryIsAtLeastRequestedSize()
    {
        int width = 320,
            height = 240,
            bpp = 4;
        int minSize = width * height * bpp;

        using var owner = Pool.RentVideoBuffer(width, height, bpp);

        Assert.True(
            owner.Memory.Length >= minSize,
            $"Expected Memory.Length >= {minSize}, got {owner.Memory.Length}"
        );
    }

    [Fact]
    public void RentVideoBuffer_LargeFrame_ReturnsAtLeastRequestedSize()
    {
        // 1080p BGRA
        int width = 1920,
            height = 1080,
            bpp = 4;
        int minSize = width * height * bpp;

        using var owner = Pool.RentVideoBuffer(width, height, bpp);

        Assert.True(
            owner.Memory.Length >= minSize,
            $"1080p: expected >= {minSize}, got {owner.Memory.Length}"
        );
    }

    [Fact]
    public void RentVideoBuffer_Dispose_DoesNotThrow()
    {
        var owner = Pool.RentVideoBuffer(64, 64, 4);
        var ex = Record.Exception(() => owner.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void RentVideoBuffer_DisposeTwice_DoesNotThrow()
    {
        // MemoryPool<T>.Shared returns ArrayPool-backed owners which are safe to dispose once.
        // Document that the caller is expected to dispose exactly once.
        var owner = Pool.RentVideoBuffer(64, 64, 4);
        owner.Dispose();
        // Second dispose — behaviour depends on implementation but must not crash
        var ex = Record.Exception(() => owner.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void RentVideoBuffer_WritableSpan_CanBeWritten()
    {
        using var owner = Pool.RentVideoBuffer(4, 4, 4);
        var span = owner.Memory.Span;
        span[0] = 0xFF;
        Assert.Equal(0xFF, span[0]);
    }

    [Fact]
    public void RentVideoBuffer_ZeroSize_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
        {
            using var owner = Pool.RentVideoBuffer(0, 0, 4);
            _ = owner;
        });
        Assert.Null(ex);
    }

    [Fact]
    public void RentVideoBuffer_BytesPerPixelOne_CorrectMinSize()
    {
        int width = 100,
            height = 100,
            bpp = 1;
        int minSize = width * height * bpp;

        using var owner = Pool.RentVideoBuffer(width, height, bpp);

        Assert.True(owner.Memory.Length >= minSize);
    }

    // -----------------------------------------------------------------------
    // IFrameBufferPool interface contract
    // -----------------------------------------------------------------------

    [Fact]
    public void ImplementsIFrameBufferPool()
    {
        Assert.IsAssignableFrom<IFrameBufferPool>(Pool);
    }

    [Fact]
    public void IFrameBufferPool_RentVideoBuffer_ReturnsIMemoryOwnerOfByte()
    {
        IFrameBufferPool pool = Pool;
        using IMemoryOwner<byte> owner = pool.RentVideoBuffer(16, 16, 4);
        Assert.NotNull(owner);
    }
}
