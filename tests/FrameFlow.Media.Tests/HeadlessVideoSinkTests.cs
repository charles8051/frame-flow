// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Tests for <see cref="HeadlessVideoSink"/>, the counting sink a headless bench run uses in
/// place of <see cref="NullVideoSink"/>.
/// </summary>
/// <remarks>
/// The cost is driven through a <see cref="FakeTimeProvider"/> rather than real time, so the
/// ordering that matters — the frame is still alive while the cost is being paid — is asserted
/// directly instead of inferred from a stopwatch.
/// </remarks>
public sealed class HeadlessVideoSinkTests
{
    private static TimeSpan Pts(int n) => TimeSpan.FromMilliseconds(n * 40);

    // ── Counting ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FreshSink_ReportsNothingPresented()
    {
        await using var sink = new HeadlessVideoSink();

        var snapshot = sink.GetDiagnostics();

        Assert.Equal(0, snapshot.FramesPresented);
        Assert.Equal(0, snapshot.FramesDropped);
        Assert.Null(snapshot.LastPresentedPresentationTime);
    }

    [Fact]
    public async Task PresentAsync_CountsAndStamps_UnlikeNullVideoSink()
    {
        // The whole reason this type exists: NullVideoSink reports Empty forever.
        await using var sink = new HeadlessVideoSink();

        await sink.PresentAsync(new StubFrame(Pts(1)), default);
        await sink.PresentAsync(new StubFrame(Pts(2)), default);

        var snapshot = sink.GetDiagnostics();

        Assert.Equal(2, snapshot.FramesPresented);
        Assert.Equal(Pts(2), snapshot.LastPresentedPresentationTime);
        Assert.NotNull(snapshot.LastPresentedAtUtc);

        // GetDiagnostics is a default interface method, so NullVideoSink answers only
        // through IVideoSink -- and answers Empty, which is the gap this type fills.
        await using IVideoSink nullSink = new NullVideoSink();
        await nullSink.PresentAsync(new StubFrame(Pts(1)), default);
        Assert.Equal(0, nullSink.GetDiagnostics().FramesPresented);
    }

    [Fact]
    public async Task PresentAsync_DisposesEveryFrame()
    {
        await using var sink = new HeadlessVideoSink();
        var frames = new[] { new StubFrame(Pts(1)), new StubFrame(Pts(2)) };

        foreach (var f in frames)
            await sink.PresentAsync(f, default);

        Assert.All(frames, f => Assert.Equal(1, f.DisposeCount));
    }

    [Fact]
    public async Task NeverDrops_BecauseThereIsNoRenderTickToFallBehind()
    {
        await using var sink = new HeadlessVideoSink();

        for (var i = 0; i < 20; i++)
            await sink.PresentAsync(new StubFrame(Pts(i)), default);

        Assert.Equal(20, sink.GetDiagnostics().FramesPresented);
        Assert.Equal(0, sink.GetDiagnostics().FramesDropped);
    }

    // ── The synthetic present cost ────────────────────────────────────────────────────────

    [Fact]
    public async Task PresentCost_HoldsTheFrameForTheWholeCost()
    {
        // The ordering the sink exists for. If the frame were disposed first and the cost paid
        // after, the pool slot would free immediately and the cost would create no backpressure
        // — which measures nothing.
        var time = new FakeTimeProvider();
        await using var sink = new HeadlessVideoSink(
            presentCost: TimeSpan.FromMilliseconds(10),
            timeProvider: time
        );

        var frame = new StubFrame(Pts(1));
        var present = sink.PresentAsync(frame, default);

        Assert.False(present.IsCompleted);
        Assert.Equal(0, frame.DisposeCount); // still held
        Assert.Equal(0, sink.GetDiagnostics().FramesPresented); // not yet presented

        time.Advance(TimeSpan.FromMilliseconds(10));
        await present;

        Assert.Equal(1, frame.DisposeCount);
        Assert.Equal(1, sink.GetDiagnostics().FramesPresented);
    }

    [Fact]
    public async Task ZeroCost_CompletesSynchronously()
    {
        await using var sink = new HeadlessVideoSink(presentCost: TimeSpan.Zero);

        var present = sink.PresentAsync(new StubFrame(Pts(1)), default);

        Assert.True(present.IsCompleted);
        await present;
    }

    [Fact]
    public async Task CancelledDuringCost_DoesNotCountThePresent_ButStillDisposesTheFrame()
    {
        // A frame abandoned to cancellation did not reach a surface. Counting it would be the
        // one lie a measurement sink cannot afford.
        var time = new FakeTimeProvider();
        await using var sink = new HeadlessVideoSink(
            presentCost: TimeSpan.FromMilliseconds(10),
            timeProvider: time
        );

        using var cts = new CancellationTokenSource();
        var frame = new StubFrame(Pts(1));
        var present = sink.PresentAsync(frame, cts.Token).AsTask();

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => present);

        Assert.Equal(1, frame.DisposeCount);
        Assert.Equal(0, sink.GetDiagnostics().FramesPresented);
    }

    [Fact]
    public void NegativeCost_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HeadlessVideoSink(presentCost: TimeSpan.FromMilliseconds(-1))
        );
    }

    // ── Pool ownership ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultPool_IsBounded_NotTheUnboundedNullPool()
    {
        // A headless run behind an unbounded pool never blocks the decoder, which is another
        // way the numbers come back better than the machine can do.
        await using var sink = new HeadlessVideoSink();

        Assert.IsType<CpuFramePool>(sink.FramePool);
    }

    [Fact]
    public async Task InjectedPool_IsNotDisposedBySink()
    {
        var pool = new TrackingPool();

        await using (var sink = new HeadlessVideoSink(framePool: pool))
        {
            Assert.Same(pool, sink.FramePool);
        }

        Assert.False(pool.Disposed);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var sink = new HeadlessVideoSink();

        await sink.DisposeAsync();
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task PresentAfterDispose_DisposesTheFrameAndCountsNothing()
    {
        var sink = new HeadlessVideoSink();
        await sink.DisposeAsync();

        var frame = new StubFrame(Pts(1));
        await sink.PresentAsync(frame, default);

        Assert.Equal(1, frame.DisposeCount);
        Assert.Equal(0, sink.GetDiagnostics().FramesPresented);
    }

    // ── Doubles ───────────────────────────────────────────────────────────────────────────

    private sealed class StubFrame(TimeSpan pts) : IVideoFrame
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int Width => 4;
        public int Height => 4;
        public TimeSpan Pts { get; } = pts;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(40);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }

    private sealed class TrackingPool : IFramePool
    {
        public bool Disposed { get; private set; }

        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public ValueTask<IVideoFrame> RentAsync(
            int width,
            int height,
            PixelFormat format,
            CancellationToken ct
        ) => new(new StubFrame(TimeSpan.Zero));

        public void Return(IVideoFrame frame) { }

        public void Dispose() => Disposed = true;
    }
}
