using System.Buffers;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Tests.Shared;

namespace FrameFlow.MotionClip.Tests;

/// <summary>
/// The clip encoder hands its writer a reference of its own for each frame. The writer releases
/// what it is given, so the segment's references survive the write and are released once, by the
/// segment. The counters are process-wide, so this runs in the non-parallel ref-counting
/// collection.
/// </summary>
[Collection(RefCountingCollection.Name)]
public sealed class ClipEncoderSinkTests
{
    [Fact]
    public async Task WritingASegment_LeavesItsFramesToTheSegment_AndReleasesEachOnce()
    {
        long overReleasesBefore = RefCounting.OverReleases;
        var pool = new ReturnCountingPool();
        var segment = new ClipSegment(
            [Frame(pool), Frame(pool), Frame(pool)],
            DateTime.UnixEpoch,
            preRollCount: 0,
            ClipEndReason.Flushed
        );
        var counts = new List<int>();

        int wrote = await ClipEncoderSink.WriteFramesAsync(
            segment.Frames,
            WriteAndRelease,
            counts.Add,
            CancellationToken.None
        );

        Assert.Equal(3, wrote);
        Assert.Equal(new[] { 1, 2, 3 }, counts);
        Assert.Equal(0, pool.Returns);

        segment.Dispose();

        Assert.Equal(3, pool.Returns);
        Assert.Equal(overReleasesBefore, RefCounting.OverReleases);
    }

    /// <summary>What <c>Mp4VideoWriter.WriteAsync</c> does with a frame: read it, release it.</summary>
    private static ValueTask WriteAndRelease(IVideoFrame frame, CancellationToken ct)
    {
        Assert.NotNull(frame.AsCpu());
        frame.Dispose();
        return ValueTask.CompletedTask;
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
