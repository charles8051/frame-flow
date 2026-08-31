using FrameFlow.Avalonia.Windows;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Pins the arrival hook the zero-copy presenter uses to schedule presents (issue #128).
/// </summary>
/// <remarks>
/// <para>
/// The presenter used to pull on a <c>DispatcherTimer</c>. Avalonia's dispatcher timer is a
/// message-queue timer quantized to the ~15.625 ms platform tick, so a 16 ms request was
/// delivered at ~26 ms and capped the presenter near 38 fps against a 60 fps source — with
/// the present itself measured at 0.3 ms, so the cadence was the whole ceiling.
/// </para>
/// <para>
/// The view now schedules from <c>FrameArrived</c> instead. The view itself needs a live
/// compositor and cannot be unit tested here; these cover the sink-side contract it depends
/// on.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoSinkArrivalTests
{
    private static CompositionInteropVideoSink NewSink() =>
        new(new CpuFramePool(NullLogger<CpuFramePool>.Instance));

    [Fact]
    public async Task PresentAsync_RaisesFrameArrived()
    {
        var sink = NewSink();
        int raised = 0;
        sink.FrameArrived = () => raised++;

        await sink.PresentAsync(new StubFrame(), CancellationToken.None);
        await sink.PresentAsync(new StubFrame(), CancellationToken.None);

        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task FrameArrived_RunsAfterTheFrameIsInstalled()
    {
        // The handler posts a present that will take the frame, so the frame has to be in the
        // slot by the time it runs — otherwise the present finds nothing and the arrival is
        // wasted.
        var sink = NewSink();
        bool pendingWhenRaised = false;
        sink.FrameArrived = () => pendingWhenRaised = sink.HasPendingFrame;

        await sink.PresentAsync(new StubFrame(), CancellationToken.None);

        Assert.True(pendingWhenRaised);
    }

    [Fact]
    public async Task AThrowingHandler_DoesNotFaultTheProducer()
    {
        // FrameArrived runs inline on the pacing chain. A presenter that cannot schedule one
        // frame — a dispatcher in shutdown, say — must not fault the graph's delivery task
        // and stop playback.
        var sink = NewSink();
        sink.FrameArrived = () => throw new InvalidOperationException("dispatcher gone");

        await sink.PresentAsync(new StubFrame(), CancellationToken.None);

        Assert.Equal(1, sink.FramesAccepted);
        Assert.True(sink.HasPendingFrame);
    }

    [Fact]
    public async Task ClearingTheHook_StopsTheCallbacks()
    {
        var sink = NewSink();
        int raised = 0;
        sink.FrameArrived = () => raised++;

        await sink.PresentAsync(new StubFrame(), CancellationToken.None);
        sink.FrameArrived = null;
        await sink.PresentAsync(new StubFrame(), CancellationToken.None);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task WithNoHook_FramesStillInstall()
    {
        // A sink driven without a view (headless, or a host pulling on its own tick) must
        // keep working — the hook is an optimisation for the view, not a requirement.
        var sink = NewSink();

        await sink.PresentAsync(new StubFrame(), CancellationToken.None);

        Assert.True(sink.HasPendingFrame);
        Assert.NotNull(sink.TakePendingFrame());
    }

    [Fact]
    public async Task HasPendingFrame_ClearsOnTake()
    {
        var sink = NewSink();
        Assert.False(sink.HasPendingFrame);

        await sink.PresentAsync(new StubFrame(), CancellationToken.None);
        Assert.True(sink.HasPendingFrame);

        sink.TakePendingFrame()?.Dispose();
        Assert.False(sink.HasPendingFrame);
    }

    private sealed class StubFrame : IVideoFrame
    {
        public int Width => 1920;
        public int Height => 1080;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() { }

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
