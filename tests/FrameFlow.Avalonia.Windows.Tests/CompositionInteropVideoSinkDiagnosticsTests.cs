using FrameFlow.Avalonia.Windows;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Pins the zero-copy GPU sink's diagnostics delegation.
/// </summary>
/// <remarks>
/// The bug was structural: <see cref="CompositionInteropVideoSink"/> never overrode
/// <c>GetDiagnostics()</c>, so the pipeline snapshot reported all-zero video counts on the
/// compositor path and any consumer polling <c>PlaybackDiagnosticsSnapshot</c> (a signage
/// diagnostics pump) was flying blind. The sink now delegates to a
/// view-supplied <c>DiagnosticsSource</c> (the view owns the presented/dropped counts);
/// these tests pin the override exists and both the wired and unwired cases behave.
/// </remarks>
public sealed class CompositionInteropVideoSinkDiagnosticsTests
{
    private static CpuFramePool NewPool() => new(NullLogger<CpuFramePool>.Instance);

    [Fact]
    public void GetDiagnostics_WithoutSource_ReturnsEmpty()
    {
        using var pool = NewPool();
        var sink = new CompositionInteropVideoSink(pool);

        Assert.Equal(VideoSinkDiagnosticsSnapshot.Empty, sink.GetDiagnostics());
    }

    [Fact]
    public void GetDiagnostics_WithSource_ReturnsTheViewSuppliedSnapshot()
    {
        using var pool = NewPool();
        var sink = new CompositionInteropVideoSink(pool);
        var expected = new VideoSinkDiagnosticsSnapshot(
            FramesPresented: 120,
            FramesDropped: 3,
            LastPresentedPresentationTime: TimeSpan.FromSeconds(4),
            LastPresentedAtUtc: new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc)
        );

        sink.DiagnosticsSource = () => expected;

        Assert.Equal(expected, sink.GetDiagnostics());
    }

    [Fact]
    public void GetDiagnostics_ReflectsLiveSourceChanges()
    {
        using var pool = NewPool();
        var sink = new CompositionInteropVideoSink(pool);
        long presented = 0;
        sink.DiagnosticsSource = () => new VideoSinkDiagnosticsSnapshot(presented, 0, null, null);

        Assert.Equal(0, sink.GetDiagnostics().FramesPresented);
        presented = 240;
        Assert.Equal(240, sink.GetDiagnostics().FramesPresented);
    }

    // ─── FramesSuperseded (#128) ────────────────────────────────────

    [Fact]
    public async Task FramesSuperseded_StartsAtZero()
    {
        using var pool = NewPool();
        var sink = new CompositionInteropVideoSink(pool);

        await sink.PresentAsync(new StubFrame(), CancellationToken.None);

        Assert.Equal(0, sink.FramesSuperseded);
    }

    [Fact]
    public async Task FramesSuperseded_CountsFramesReplacedBeforeTheRenderTickTookThem()
    {
        using var pool = NewPool();
        var sink = new CompositionInteropVideoSink(pool);
        var first = new StubFrame();

        await sink.PresentAsync(first, CancellationToken.None);
        await sink.PresentAsync(new StubFrame(), CancellationToken.None);
        await sink.PresentAsync(new StubFrame(), CancellationToken.None);

        // Two supersedes; only the newest frame survives in the slot.
        Assert.Equal(2, sink.FramesSuperseded);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task FramesSuperseded_DoesNotCountFramesTheRenderTickConsumed()
    {
        using var pool = NewPool();
        var sink = new CompositionInteropVideoSink(pool);

        for (int i = 0; i < 5; i++)
        {
            await sink.PresentAsync(new StubFrame(), CancellationToken.None);
            sink.TakePendingFrame()?.Dispose();
        }

        Assert.Equal(5, sink.FramesAccepted);
        Assert.Equal(0, sink.FramesSuperseded);
    }

    /// <summary>
    /// The accounting property #128 says this path violated: with the supersede count
    /// unreported, 307 accepted frames reconciled against 190 presented and 0 dropped.
    /// Every accepted frame is presented, superseded, or still pending — nothing vanishes.
    /// </summary>
    [Fact]
    public async Task EveryAcceptedFrameIsPresentedSupersededOrStillPending()
    {
        using var pool = NewPool();
        var sink = new CompositionInteropVideoSink(pool);

        // Feed 10, take every 4th: a render tick slower than the feed, the 1080p60 shape.
        const int fed = 10;
        long taken = 0;
        for (int i = 0; i < fed; i++)
        {
            await sink.PresentAsync(new StubFrame(), CancellationToken.None);
            if (i % 4 != 3)
                continue;
            if (sink.TakePendingFrame() is { } frame)
            {
                frame.Dispose();
                taken++;
            }
        }

        var pending = sink.TakePendingFrame();
        pending?.Dispose();

        Assert.Equal(fed, sink.FramesAccepted);
        Assert.Equal(fed, taken + sink.FramesSuperseded + (pending is null ? 0 : 1));
        Assert.True(sink.FramesSuperseded > 0, "the feed outran the tick, so some frames must be counted lost");
    }

    // ─── Fakes ──────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IVideoFrame"/> stub. Every <see cref="Dispose"/> bumps the counter —
    /// the sink's slot never AddRef's, so a second dispose would be a real double-dispose.
    /// </summary>
    private sealed class StubFrame : IVideoFrame
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int Width => 1920;
        public int Height => 1080;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
