using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0016 Decision 1 for the CPU surface: the BGRA copy happens on the producer's
/// thread, and the UI thread only swaps and draws.
/// </summary>
/// <remarks>
/// <para>
/// The defect (#123) was that <c>FrameFlowVideoView.Render</c> did the whole thing on the
/// Avalonia UI thread — take the frame, lock the <c>WriteableBitmap</c>, memcpy, swap, draw.
/// At 1080p60 that is ~8.3 MB per frame on the one thread that has to hit a ~16 ms present,
/// so the view missed ticks under load and the loss propagated back through the sink to the
/// decoder as shed packets.
/// </para>
/// <para>
/// These tests assert the copy is observable without the UI thread ever running a render
/// pass, which is only true if the producer did it.
/// </para>
/// </remarks>
public sealed class FrameFlowVideoViewCopyThreadTests
{
    private const int W = 32;
    private const int H = 16;

    [AvaloniaFact]
    public void CopyHappensOnTheProducerThread_WithoutAnyRenderPass()
    {
        var (view, sink) = NewAttachedView();

        // First frame allocates: the WriteableBitmaps are created on the UI thread, so this
        // one is discarded and counted, and the allocation is posted.
        PresentOffUiThread(sink, Pts(1));
        Assert.Equal(0, view.RenderedFrameCount);
        Assert.Equal(1, sink.DroppedFrameCount);

        Dispatcher.UIThread.RunJobs(); // run the posted allocation

        // Second frame lands in the back buffer — on the presenting thread, while the UI
        // thread is blocked inside PresentOffUiThread. Nothing is counted presented yet: the
        // swap has not been pumped. The proof the copy already happened is that pumping now
        // publishes it without any further frame arriving.
        PresentOffUiThread(sink, Pts(2));
        Assert.Equal(0, view.RenderedFrameCount);

        Dispatcher.UIThread.RunJobs(); // the swap only

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(1, sink.DroppedFrameCount);
        Assert.Equal(1, sink.GetDiagnostics().FramesPresented);
        Assert.Equal(Pts(2), sink.GetDiagnostics().LastPresentedPresentationTime);
    }

    [AvaloniaFact]
    public void SupersedingAnUnswappedBackBuffer_CountsADrop()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();

        // Three frames with the UI thread never pumped: the first fills the back buffer, the
        // next two each overwrite a copy that never got swapped to the front.
        PresentOffUiThread(sink, Pts(2));
        PresentOffUiThread(sink, Pts(3));
        PresentOffUiThread(sink, Pts(4));

        Assert.Equal(0, view.RenderedFrameCount);
        Assert.Equal(3, sink.DroppedFrameCount); // 1 allocation + 2 superseded in the back buffer

        Dispatcher.UIThread.RunJobs(); // publishes the survivor, frame 4

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(Pts(4), sink.GetDiagnostics().LastPresentedPresentationTime);

        // Every frame is accounted for exactly once: presented or dropped, never both.
        var snapshot = sink.GetDiagnostics();
        Assert.Equal(4, snapshot.FramesPresented + snapshot.FramesDropped);
    }

    [AvaloniaFact]
    public void PumpingTheUiThreadBetweenFrames_DropsNothing()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();

        for (int i = 2; i <= 5; i++)
        {
            PresentOffUiThread(sink, Pts(i));
            Dispatcher.UIThread.RunJobs(); // the swap
        }

        Assert.Equal(4, view.RenderedFrameCount);
        Assert.Equal(1, sink.DroppedFrameCount); // the allocation frame only
    }

    [AvaloniaFact]
    public void LastPresentedPtsIsStamped_ByTheProducer()
    {
        var (_, sink) = NewAttachedView();

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();
        PresentOffUiThread(sink, Pts(7));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Pts(7), sink.GetDiagnostics().LastPresentedPresentationTime);
    }

    [AvaloniaFact]
    public void DetachingTheView_StopsTheProducerCallingIntoIt()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, Pts(1));
        Dispatcher.UIThread.RunJobs();
        PresentOffUiThread(sink, Pts(2));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, view.RenderedFrameCount);

        // Externally-owned sink, so detach unhooks but does not dispose it.
        ((Panel)view.Parent!).Children.Remove(view);
        Dispatcher.UIThread.RunJobs();

        PresentOffUiThread(sink, Pts(3));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, view.RenderedFrameCount);
    }

    // ─── Attachment lifecycle (review on #135) ──────────────────────

    [AvaloniaFact]
    public void ReplacingTheSink_ChargesAnUnswappedFrameToTheSinkThatProducedIt()
    {
        var (view, first) = NewAttachedView();
        var second = NewSink();

        PresentOffUiThread(first, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();
        PresentOffUiThread(first, Pts(2)); // copied, not yet swapped

        view.Sink = second;
        Dispatcher.UIThread.RunJobs(); // the queued swap runs after the replacement

        // The stranded frame is the first sink's loss. It must not be published as the
        // replacement's, and it must not vanish from the accounting.
        Assert.Equal(0, first.RenderedFrameCount);
        Assert.Equal(2, first.DroppedFrameCount); // allocation frame + the stranded one
        Assert.Equal(2, first.GetDiagnostics().FramesPresented + first.GetDiagnostics().FramesDropped);

        Assert.Equal(0, second.RenderedFrameCount);
        Assert.Equal(0, second.DroppedFrameCount);
    }

    [AvaloniaFact]
    public void ReplacingTheSink_LeavesAnAlreadySwappedFrameCreditedToTheOldSink()
    {
        var (view, first) = NewAttachedView();
        var second = NewSink();

        PresentOffUiThread(first, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();
        PresentOffUiThread(first, Pts(2));
        Dispatcher.UIThread.RunJobs(); // swapped while still bound to the first sink

        view.Sink = second;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, first.RenderedFrameCount);
        Assert.Equal(0, second.RenderedFrameCount);
    }

    [AvaloniaFact]
    public void ReplacingTheSinkWithASwapStillQueued_DoesNotStrandTheReplacementsFirstFrame()
    {
        var (view, first) = NewAttachedView();
        var second = NewSink();

        PresentOffUiThread(first, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();
        PresentOffUiThread(first, Pts(2)); // copied; its swap is queued but has not run

        view.Sink = second; // the old binding ends with that swap still on the dispatcher

        // The replacement must be able to claim a swap of its own. When the coalescing flag
        // was per-view rather than per-binding, it saw the stale claim and posted nothing;
        // the stale swap then released the flag, found itself detached, and returned — and
        // this frame sat in the back buffer forever.
        PresentOffUiThread(second, Pts(3));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(1, second.RenderedFrameCount);
        Assert.Equal(Pts(3), second.GetDiagnostics().LastPresentedPresentationTime);
    }

    [AvaloniaFact]
    public void DetachingWithAFrameInTheBackBuffer_CountsItDropped()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();
        PresentOffUiThread(sink, Pts(2)); // copied, not yet swapped

        ((Panel)view.Parent!).Children.Remove(view);
        Dispatcher.UIThread.RunJobs();

        // Two frames in, neither drawn, both accounted. Before the binding carried the
        // detach, the queued swap ran anyway and the frame was counted by nobody.
        Assert.Equal(0, view.RenderedFrameCount);
        var snapshot = sink.GetDiagnostics();
        Assert.Equal(0, snapshot.FramesPresented);
        Assert.Equal(2, snapshot.FramesDropped);
    }

    [AvaloniaFact]
    public async Task AFrameThatThrowsOnCopy_DoesNotFaultTheProducer()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();

        // PresentAsync must complete: the presenting thread is the pacing chain, and a
        // presenter that cannot draw one frame is not a reason to stop playback.
        await Task.Run(async () =>
            await sink.PresentAsync(new ThrowingFrame(Pts(2)), CancellationToken.None));

        Assert.Equal(0, view.RenderedFrameCount);
        Assert.Equal(2, sink.DroppedFrameCount); // allocation frame + the throwing one

        // And the surface still works afterwards.
        PresentOffUiThread(sink, Pts(3));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, view.RenderedFrameCount);
    }

    [AvaloniaFact]
    public void ManyCopiesBehindAStalledUiThread_NeedOnlyOneSwapToCatchUp()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();

        for (int i = 2; i <= 21; i++)
            PresentOffUiThread(sink, Pts(i));

        // Swaps are coalesced, so the whole backlog resolves to the newest frame. If each
        // copy queued its own delegate the dispatcher would hold 20 of them here.
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(Pts(21), sink.GetDiagnostics().LastPresentedPresentationTime);

        var snapshot = sink.GetDiagnostics();
        Assert.Equal(21, snapshot.FramesPresented + snapshot.FramesDropped);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static TimeSpan Pts(int n) => TimeSpan.FromMilliseconds(n * 16);

    /// <summary>
    /// A view in a live visual tree with a caller-owned sink, which is the wiring
    /// <c>FrameFlowPlayerView</c> and the DI extensions produce.
    /// </summary>
    private static AvaloniaVideoSink NewSink() =>
        new(new CpuFramePool(NullLogger<CpuFramePool>.Instance));

    private static (FrameFlowVideoView View, AvaloniaVideoSink Sink) NewAttachedView()
    {
        var sink = NewSink();
        var view = new FrameFlowVideoView { Sink = sink };

        var window = new Window { Width = 200, Height = 100, Content = new Panel { Children = { view } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (view, sink);
    }

    /// <summary>
    /// Presents from a pool thread and waits for it, so the copy provably did not happen on
    /// the UI thread — the UI thread is blocked inside this call.
    /// </summary>
    private static void PresentOffUiThread(AvaloniaVideoSink sink, TimeSpan pts) =>
        Task.Run(async () => await sink.PresentAsync(new BgraFrame(W, H, pts), CancellationToken.None))
            .GetAwaiter()
            .GetResult();

    /// <summary>A frame whose pixel access throws, standing in for a bitmap/platform failure.</summary>
    private sealed class ThrowingFrame(TimeSpan pts) : IVideoFrame
    {
        public int Width => W;
        public int Height => H;
        public TimeSpan Pts => pts;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() { }

        public CpuFrameData? AsCpu() => throw new InvalidOperationException("pixel access failed");

        public CpuFrameData ToCpu() => throw new InvalidOperationException("pixel access failed");
    }

    /// <summary>Packed BGRA32 frame over a real buffer, so the memcpy has something to copy.</summary>
    private sealed class BgraFrame(int width, int height, TimeSpan pts) : IVideoFrame
    {
        private readonly byte[] _pixels = new byte[width * height * 4];

        public int Width => width;
        public int Height => height;
        public TimeSpan Pts => pts;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() { }

        public CpuFrameData? AsCpu() =>
            new(_pixels, default, default, width * 4, 0, 0, width, height);

        public CpuFrameData ToCpu() => AsCpu()!.Value;
    }
}
