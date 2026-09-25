using FrameFlow.Media.Diagnostics;
using FrameFlow.Media.Tests.Doubles;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Media.Tests;

/// <summary>
/// <see cref="CpuFrameMetrics"/> counts a CPU frame's buffer from construction to its final
/// release, once, however many holders share it. The counters are process-wide, so this runs in
/// the non-parallel ref-counting collection.
/// </summary>
[Collection(RefCountingCollection.Name)]
public sealed class CpuFrameMetricsTests
{
    [Fact]
    public void AFrame_IsCountedUntilItsFinalRelease_AndSharingItAddsNothing()
    {
        long bytesBefore = CpuFrameMetrics.OutstandingBytes;
        int framesBefore = CpuFrameMetrics.OutstandingFrames;

        // 10 x 25 Bgra32 is 1000 bytes, and the counting pool rents exactly that.
        var frame = Frame(10, 25, new CountingArrayPool<byte>());

        Assert.Equal(bytesBefore + 1000, CpuFrameMetrics.OutstandingBytes);
        Assert.Equal(framesBefore + 1, CpuFrameMetrics.OutstandingFrames);
        Assert.True(CpuFrameMetrics.HighWaterBytes >= bytesBefore + 1000);

        var shared = frame.AddRef();
        frame.Dispose();

        Assert.Equal(bytesBefore + 1000, CpuFrameMetrics.OutstandingBytes);

        shared.Dispose();

        Assert.Equal(bytesBefore, CpuFrameMetrics.OutstandingBytes);
        Assert.Equal(framesBefore, CpuFrameMetrics.OutstandingFrames);

        // An over-release frees nothing, so it must not count the buffer out twice.
        shared.Dispose();

        Assert.Equal(bytesBefore, CpuFrameMetrics.OutstandingBytes);
    }

    [Fact]
    public void AFrameWhosePoolThrowsOnReturn_IsStillCountedOut()
    {
        long bytesBefore = CpuFrameMetrics.OutstandingBytes;
        int framesBefore = CpuFrameMetrics.OutstandingFrames;
        var frame = Frame(5, 25, new CountingArrayPool<byte> { ThrowOnReturn = true });

        Assert.Throws<InvalidOperationException>(() => frame.Dispose());

        Assert.Equal(bytesBefore, CpuFrameMetrics.OutstandingBytes);
        Assert.Equal(framesBefore, CpuFrameMetrics.OutstandingFrames);
    }

    private static CpuVideoFrame Frame(int width, int height, CountingArrayPool<byte> pool) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            width,
            height,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            static (_, _) => { },
            pool
        );
}
