using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Media.Tests;

// Moved from FrameFlow.Playback.Tests during Phase 4 prep (ADR-0014) —
// CpuFramePool now lives in FrameFlow.Media so sinks / examples don't need
// to transitively reference the soon-to-be-deleted FrameFlow.Playback
// assembly. Test surface is unchanged; only the host project differs.
public sealed class CpuFramePoolTests
{
    private static CpuFramePool CreatePool(int capacity = 3) =>
        new(NullLogger<CpuFramePool>.Instance, capacity);

    // ── Basic rent ────────────────────────────────────

    [Fact]
    public async Task RentAsync_ReturnsFrame_WithCorrectDimensions()
    {
        using var pool = CreatePool();

        var frame = await pool.RentAsync(1920, 1080, PixelFormat.Bgra32, CancellationToken.None);
        try
        {
            Assert.Equal(1920, frame.Width);
            Assert.Equal(1080, frame.Height);
            Assert.Equal(PixelFormat.Bgra32, frame.Format);
        }
        finally
        {
            frame.Dispose();
        }
    }

    // ── Backpressure ──────────────────────────────────

    [Fact]
    public async Task RentAsync_BlocksWhenPoolExhausted_UnblocksOnReturn()
    {
        using var pool = CreatePool(capacity: 2);

        var frame1 = await pool.RentAsync(64, 64, PixelFormat.Bgra32, CancellationToken.None);
        var frame2 = await pool.RentAsync(64, 64, PixelFormat.Bgra32, CancellationToken.None);

        // Third rent should block because both frames are in-flight.
        var thirdRentTask = pool.RentAsync(64, 64, PixelFormat.Bgra32, CancellationToken.None)
            .AsTask();

        // Verify it does NOT complete within 200ms.
        var completed = await Task.WhenAny(
            thirdRentTask,
            Task.Delay(TimeSpan.FromMilliseconds(200))
        );
        Assert.NotSame(thirdRentTask, completed);

        // Return one frame — third rent should unblock.
        frame1.Dispose();

        var frame3 = await thirdRentTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(frame3);

        // Cleanup.
        frame2.Dispose();
        frame3.Dispose();
    }

    // ── Ref counting ──────────────────────────────────

    [Fact]
    public async Task RefCount_AddRefPreventsReturnToPool()
    {
        using var pool = CreatePool(capacity: 1);

        var frame = await pool.RentAsync(64, 64, PixelFormat.Bgra32, CancellationToken.None);

        // AddRef bumps ref count to 2.
        frame.AddRef();

        // First dispose decrements to 1 — should NOT release the semaphore.
        frame.Dispose();

        // Attempting another rent should block (semaphore still at 0).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            pool.RentAsync(64, 64, PixelFormat.Bgra32, cts.Token).AsTask()
        );

        // Second dispose decrements to 0 — should release the semaphore.
        frame.Dispose();

        // Now a rent should succeed promptly.
        var frame2 = await pool.RentAsync(64, 64, PixelFormat.Bgra32, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(frame2);
        frame2.Dispose();
    }

    // ── Dispose idempotency ───────────────────────────

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        using var pool = CreatePool();

        var frame = await pool.RentAsync(64, 64, PixelFormat.Bgra32, CancellationToken.None);

        // Dispose twice — no exception expected.
        frame.Dispose();
        frame.Dispose();
    }

    // ── Cancellation ──────────────────────────────────

    [Fact]
    public async Task RentAsync_ThrowsWhenCancelled()
    {
        using var pool = CreatePool();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pool.RentAsync(64, 64, PixelFormat.Bgra32, cts.Token).AsTask()
        );
    }

    // ── AsCpu accessor ────────────────────────────────

    [Fact]
    public async Task AsCpu_ReturnsValidCpuFrameData()
    {
        using var pool = CreatePool();

        var frame = await pool.RentAsync(320, 240, PixelFormat.Bgra32, CancellationToken.None);
        try
        {
            var cpuData = frame.AsCpu();

            Assert.NotNull(cpuData);
            Assert.Equal(320, cpuData.Value.Width);
            Assert.Equal(240, cpuData.Value.Height);
            Assert.Equal(320 * 4, cpuData.Value.StrideY);
        }
        finally
        {
            frame.Dispose();
        }
    }
}
