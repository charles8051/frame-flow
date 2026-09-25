using System.Buffers;
using FrameFlow.Media;

namespace FrameFlow.MotionClip.Tests;

/// <summary>
/// The pre-roll ring keeps recent frames alive by holding a reference on each (ADR-0080), not by
/// copying them.
/// </summary>
public sealed class PreRollBufferTests
{
    [Fact]
    public void Add_HoldsTheFrameItself_PastTheCallersRelease()
    {
        var pool = new ReturnCountingPool();
        var frame = Frame(pool);
        using var ring = new PreRollBuffer(capacityFrames: 4);

        ring.Add(frame);
        frame.Dispose();

        Assert.Equal(0, pool.Returns);
        var snapshot = ring.SnapshotAndClear();
        Assert.Same(frame, Assert.Single(snapshot));

        snapshot[0].Dispose();
        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public void AFullRing_ReleasesTheOldestFrame()
    {
        var pool = new ReturnCountingPool();
        using var ring = new PreRollBuffer(capacityFrames: 1);

        using (var first = Frame(pool))
            ring.Add(first);
        using (var second = Frame(pool))
            ring.Add(second);

        Assert.Equal(1, pool.Returns);
        Assert.Equal(1, ring.Count);
    }

    private static CpuVideoFrame Frame(ArrayPool<byte> pool) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            2,
            2,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            static (_, _) => { },
            pool
        );

    /// <summary>Hands out fresh arrays and counts how many come back.</summary>
    private sealed class ReturnCountingPool : ArrayPool<byte>
    {
        private int _returns;

        public int Returns => Volatile.Read(ref _returns);

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false) =>
            Interlocked.Increment(ref _returns);
    }
}
