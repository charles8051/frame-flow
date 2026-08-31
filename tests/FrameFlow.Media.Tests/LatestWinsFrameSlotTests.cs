using FrameFlow.Graph;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Tests for <see cref="LatestWinsFrameSlot"/> — the single-element latest-wins frame
/// buffer extracted from the three render-tick video sinks. FFmpeg-free: uses a stub
/// <see cref="IVideoFrame"/> that records its <see cref="IDisposable.Dispose"/> calls so
/// the dispose-the-superseded-exactly-once invariant can be asserted directly.
/// </summary>
public sealed class LatestWinsFrameSlotTests
{
    [Fact]
    public void Take_OnEmpty_ReturnsNull()
    {
        var slot = new LatestWinsFrameSlot();

        Assert.Null(slot.Take());
        Assert.Equal(0, slot.Dropped);
        Assert.False(slot.HasPending);
    }

    [Fact]
    public void TrySet_ThenTake_ReturnsNewest_NoDrop_NoDispose()
    {
        var slot = new LatestWinsFrameSlot();
        var frame = new RecordingFrame();

        Assert.False(slot.TrySet(frame)); // nothing superseded
        Assert.True(slot.HasPending);

        var taken = slot.Take();

        Assert.Same(frame, taken);
        Assert.Equal(0, frame.DisposeCount); // taken frame is owned by the caller, not disposed
        Assert.Equal(0, slot.Dropped);
        Assert.False(slot.HasPending);
    }

    [Fact]
    public void TrySet_OverUnconsumed_DisposesSupersededExactlyOnce_AndCountsDrop()
    {
        var slot = new LatestWinsFrameSlot();
        var first = new RecordingFrame();
        var second = new RecordingFrame();

        Assert.False(slot.TrySet(first));
        Assert.True(slot.TrySet(second)); // supersedes `first`

        Assert.Equal(1, first.DisposeCount); // superseded frame disposed exactly once
        Assert.Equal(0, second.DisposeCount); // newest frame survives in the slot
        Assert.Equal(1, slot.Dropped);

        var taken = slot.Take();
        Assert.Same(second, taken); // the taken frame is the newest
        Assert.Equal(0, second.DisposeCount); // and ownership transferred to the caller
    }

    [Fact]
    public void TrySet_RepeatedSupersede_CountsEveryDrop_DisposesEachOnce()
    {
        var slot = new LatestWinsFrameSlot();
        var frames = new List<RecordingFrame>();

        const int n = 10;
        for (int i = 0; i < n; i++)
        {
            var f = new RecordingFrame();
            frames.Add(f);
            slot.TrySet(f);
        }

        // The first n-1 frames were each superseded before any take → dropped once apiece.
        Assert.Equal(n - 1, slot.Dropped);
        for (int i = 0; i < n - 1; i++)
            Assert.Equal(1, frames[i].DisposeCount);

        // Only the last survives, undisposed, and is the one Take returns.
        Assert.Equal(0, frames[n - 1].DisposeCount);
        Assert.Same(frames[n - 1], slot.Take());
    }

    [Fact]
    public void Take_RunsOnTakenHook_WithTakenFrame_OnlyWhenNonEmpty()
    {
        var slot = new LatestWinsFrameSlot();

        // Empty: the hook must not run.
        IVideoFrame? observed = null;
        var hookRuns = 0;
        var resultEmpty = slot.Take(f =>
        {
            hookRuns++;
            observed = f;
        });
        Assert.Null(resultEmpty);
        Assert.Equal(0, hookRuns);
        Assert.Null(observed);

        // Non-empty: the hook runs exactly once with the frame being returned.
        var frame = new RecordingFrame();
        slot.TrySet(frame);
        var taken = slot.Take(f =>
        {
            hookRuns++;
            observed = f;
        });
        Assert.Same(frame, taken);
        Assert.Equal(1, hookRuns);
        Assert.Same(frame, observed);
        Assert.Equal(0, frame.DisposeCount); // the stamp hook does not dispose
    }

    [Fact]
    public void TrySet_Null_Throws()
    {
        var slot = new LatestWinsFrameSlot();
        Assert.Throws<ArgumentNullException>(() => slot.TrySet(null!));
    }

    /// <summary>
    /// Concurrency stress: many producers race TrySet against a consumer racing Take,
    /// then a final drain. The invariant proven is conservation — every frame that ever
    /// entered the slot is disposed exactly once across (drops + taken-then-disposed),
    /// with no double-dispose and no leak — and that the slot's own drop count agrees
    /// with the number of frames the slot itself disposed.
    /// </summary>
    [Fact]
    public void Concurrent_TrySetAndTake_NoDoubleDispose_NoLeak()
    {
        var slot = new LatestWinsFrameSlot();

        const int producers = 4;
        const int perProducer = 25_000;
        const int total = producers * perProducer;

        var allFrames = new RecordingFrame[total];
        for (int i = 0; i < total; i++)
            allFrames[i] = new RecordingFrame();

        var takenAndDisposed = 0;
        using var startGate = new ManualResetEventSlim(false);
        var stopConsumer = false;

        // Consumer: continuously takes and disposes (modelling a render tick that owns
        // whatever it took). Take's superseded-disposal happens inside TrySet on the
        // producer threads; here we only ever dispose what we successfully took.
        var consumer = new Thread(() =>
        {
            startGate.Wait();
            while (!Volatile.Read(ref stopConsumer))
            {
                var f = slot.Take();
                if (f is not null)
                {
                    f.Dispose();
                    Interlocked.Increment(ref takenAndDisposed);
                }
            }
        });
        consumer.Start();

        var producerThreads = new Thread[producers];
        for (int p = 0; p < producers; p++)
        {
            int start = p * perProducer;
            producerThreads[p] = new Thread(() =>
            {
                startGate.Wait();
                for (int i = 0; i < perProducer; i++)
                    slot.TrySet(allFrames[start + i]);
            });
            producerThreads[p].Start();
        }

        startGate.Set();
        foreach (var t in producerThreads)
            t.Join();

        // Let the consumer drain whatever it can, then stop and final-drain on this thread.
        Thread.Sleep(20);
        Volatile.Write(ref stopConsumer, true);
        consumer.Join();

        var residual = slot.Take();
        if (residual is not null)
        {
            residual.Dispose();
            Interlocked.Increment(ref takenAndDisposed);
        }

        // Conservation: every frame disposed exactly once, none twice, none leaked.
        foreach (var f in allFrames)
            Assert.Equal(1, f.DisposeCount);

        // The slot disposed exactly the dropped ones; the consumer/drain disposed the rest.
        Assert.Equal(total, slot.Dropped + takenAndDisposed);
        Assert.False(slot.HasPending);
    }

    // ─── Fakes ──────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IVideoFrame"/> stub that records how many times it was disposed,
    /// so tests can assert dispose-exactly-once. Unlike the refcounting test frame, every
    /// <see cref="Dispose"/> call bumps the counter (the slot never AddRef's, so a second
    /// dispose would be a real double-dispose bug, which is exactly what we want to catch).
    /// </summary>
    private sealed class RecordingFrame : IVideoFrame
    {
        private int _disposeCount;

        /// <summary>Number of times <see cref="Dispose"/> has been called on this frame.</summary>
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int Width => 4;
        public int Height => 4;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
