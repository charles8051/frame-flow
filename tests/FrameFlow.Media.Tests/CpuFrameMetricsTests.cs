using System.Buffers;
using FrameFlow.Media.Diagnostics;
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

        var frame = new CpuVideoFrame(new Owner(1000), 10, 25, 40, PixelFormat.Bgra32, TimeSpan.Zero);

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
    public void AFrameWhoseBufferOwnerThrowsOnDispose_IsStillCountedOut()
    {
        long bytesBefore = CpuFrameMetrics.OutstandingBytes;
        int framesBefore = CpuFrameMetrics.OutstandingFrames;
        var frame = new CpuVideoFrame(new ThrowingOwner(500), 5, 25, 20, PixelFormat.Bgra32, TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => frame.Dispose());

        Assert.Equal(bytesBefore, CpuFrameMetrics.OutstandingBytes);
        Assert.Equal(framesBefore, CpuFrameMetrics.OutstandingFrames);
    }

    private sealed class ThrowingOwner(int length) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[length];

        public void Dispose() => throw new InvalidOperationException("owner failed");
    }

    private sealed class Owner(int length) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[length];

        public void Dispose() { }
    }
}
