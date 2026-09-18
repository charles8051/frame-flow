using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #287: the frame that triggers a bitmap allocation is drawn, not discarded.
/// </summary>
/// <remarks>
/// <para>
/// The <c>WriteableBitmap</c> pair is created on the UI thread, so a frame whose dimensions
/// do not match the current pair posts the allocation and returns. It used to return without
/// the frame: one frame lost on the first frame and on each resize, which a clip covers 33 ms
/// later and never notices.
/// </para>
/// <para>
/// A still image decodes to exactly one frame. There is no frame 33 ms later, so the item the
/// player was told to show never reached the screen at all and the view kept displaying
/// whatever was there before, for the whole dwell. These tests drive that shape directly: one
/// frame, then nothing.
/// </para>
/// </remarks>
public sealed class FrameFlowVideoViewSingleFrameItemTests
{
    [AvaloniaFact]
    public void AnItemWithOneFrame_ReachesTheScreen()
    {
        var (view, sink) = NewAttachedView();

        // The whole item: one frame, at a size no bitmap has been allocated for yet.
        PresentOffUiThread(sink, 64, 48, Pts(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(Pts(1), sink.GetDiagnostics().LastPresentedPresentationTime);
        Assert.Equal(0, sink.DroppedFrameCount);
    }

    [AvaloniaFact]
    public void AOneFrameItemAfterAClipOfAnotherSize_ReplacesWhatIsOnScreen()
    {
        var (view, sink) = NewAttachedView();

        // A clip at one size, playing normally.
        foreach (var n in new[] { 1, 2, 3 })
        {
            PresentOffUiThread(sink, 32, 16, Pts(n));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(Pts(3), sink.GetDiagnostics().LastPresentedPresentationTime);

        // Then a still at a different size, which is one frame and nothing after it. The
        // size mismatch is what reallocates, and is exactly the frame that used to be lost.
        PresentOffUiThread(sink, 64, 48, Pts(4));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, view.RenderedFrameCount);
        Assert.Equal(Pts(4), sink.GetDiagnostics().LastPresentedPresentationTime);
        Assert.Equal(0, sink.DroppedFrameCount);
    }

    [AvaloniaFact]
    public void TwoSingleFrameItemsInARow_EachReachTheScreen()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, 64, 48, Pts(1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Pts(1), sink.GetDiagnostics().LastPresentedPresentationTime);

        PresentOffUiThread(sink, 80, 60, Pts(2));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Pts(2), sink.GetDiagnostics().LastPresentedPresentationTime);

        Assert.Equal(2, view.RenderedFrameCount);
        Assert.Equal(0, sink.DroppedFrameCount);
    }

    [AvaloniaFact]
    public void ASecondSizeArrivingBeforeTheAllocation_SupersedesTheFirstAndIsCountedOnce()
    {
        var (view, sink) = NewAttachedView();

        // Both land while the UI thread is unpumped, so the second replaces pixels that are
        // still waiting for their buffers. Latest wins, and the superseded one is a drop.
        PresentOffUiThread(sink, 64, 48, Pts(1));
        PresentOffUiThread(sink, 80, 60, Pts(2));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(Pts(2), sink.GetDiagnostics().LastPresentedPresentationTime);

        var snapshot = sink.GetDiagnostics();
        Assert.Equal(2, snapshot.FramesPresented + snapshot.FramesDropped);
    }

    [AvaloniaFact]
    public void DetachingBeforeTheAllocationLands_ChargesTheWaitingPixelsOnce()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, 64, 48, Pts(1));
        ((Panel)view.Parent!).Children.Remove(view);
        Dispatcher.UIThread.RunJobs();

        // Nothing drew, so the frame is owed a drop and exactly one accounting entry. The
        // staging slot must not leave it counted by nobody, nor counted twice.
        Assert.Equal(0, view.RenderedFrameCount);
        var snapshot = sink.GetDiagnostics();
        Assert.Equal(0, snapshot.FramesPresented);
        Assert.Equal(1, snapshot.FramesDropped);
    }

    [AvaloniaFact]
    public void DetachingWithBothACopyAndPixelsWaiting_ChargesTwoDrops()
    {
        var (view, sink) = NewAttachedView();

        // Buffers exist at the clip's size.
        PresentOffUiThread(sink, 32, 16, Pts(1));
        Dispatcher.UIThread.RunJobs();

        // Now two frames are in flight at once, in two different places: frame 2 copied into
        // the back buffer with its swap not yet pumped, frame 3 staged behind an allocation
        // that has not run. They are two frames, and a detach strands both.
        PresentOffUiThread(sink, 32, 16, Pts(2));
        PresentOffUiThread(sink, 64, 48, Pts(3));

        ((Panel)view.Parent!).Children.Remove(view);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.RenderedFrameCount);

        var snapshot = sink.GetDiagnostics();
        Assert.Equal(2, snapshot.FramesDropped);
        Assert.Equal(3, snapshot.FramesPresented + snapshot.FramesDropped);
    }

    [AvaloniaFact]
    public void AFrameWithNoSize_IsChargedRatherThanStaged()
    {
        var (view, sink) = NewAttachedView();

        // No bitmap can be allocated at this size, so it must not be staged behind an
        // allocation that would fail on the UI thread. It still owes exactly one accounting
        // entry, which an empty-slot sentinel used to swallow.
        PresentOffUiThread(sink, 0, 0, Pts(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, view.RenderedFrameCount);

        var snapshot = sink.GetDiagnostics();
        Assert.Equal(0, snapshot.FramesPresented);
        Assert.Equal(1, snapshot.FramesDropped);

        // And the surface still works: a real frame after it draws.
        PresentOffUiThread(sink, 64, 48, Pts(2));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(Pts(2), sink.GetDiagnostics().LastPresentedPresentationTime);
    }

    [AvaloniaFact]
    public void AResizeBehindAQueuedSwap_DrawsBothFrames()
    {
        var (view, sink) = NewAttachedView();

        PresentOffUiThread(sink, 32, 16, Pts(1));
        Dispatcher.UIThread.RunJobs();

        // Frame 2 is copied into the back buffer and queues its swap. Frame 3 is a different
        // size, so it stages and queues an allocation behind that swap.
        PresentOffUiThread(sink, 32, 16, Pts(2));
        PresentOffUiThread(sink, 64, 48, Pts(3));
        Dispatcher.UIThread.RunJobs();

        // Both drew. AllocateBuffers discards whatever is in the back buffer when it
        // replaces it, and does so without charging a drop; the reason that is not a hole is
        // this ordering. Both callbacks are posted at DispatcherPriority.Render, so they run
        // in the order they were queued and the swap publishes frame 2 before the allocation
        // can throw its buffer away. If that ever stops holding, this goes red and the
        // accounting in AllocateBuffers becomes a live question.
        Assert.Equal(3, view.RenderedFrameCount);
        Assert.Equal(Pts(3), sink.GetDiagnostics().LastPresentedPresentationTime);
        Assert.Equal(0, sink.DroppedFrameCount);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static TimeSpan Pts(int n) => TimeSpan.FromMilliseconds(n * 16);

    private static (FrameFlowVideoView View, AvaloniaVideoSink Sink) NewAttachedView()
    {
        var sink = new AvaloniaVideoSink(new CpuFramePool(NullLogger<CpuFramePool>.Instance));
        var view = new FrameFlowVideoView { Sink = sink };

        var window = new Window { Width = 200, Height = 100, Content = new Panel { Children = { view } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (view, sink);
    }

    private static void PresentOffUiThread(AvaloniaVideoSink sink, int width, int height, TimeSpan pts) =>
        Task.Run(async () =>
                await sink.PresentAsync(new BgraFrame(width, height, pts), CancellationToken.None))
            .GetAwaiter()
            .GetResult();

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

        // One-shot, like the Media.CpuVideoFrame the view is handed in a real pipeline.
        public IVideoFrame AddRef() =>
            throw new NotSupportedException("One-shot frame: ref counting is not supported.");

        public void Dispose() { }

        public CpuFrameData? AsCpu() =>
            new(_pixels, default, default, width * 4, 0, 0, width, height);

        public CpuFrameData ToCpu() => AsCpu()!.Value;
    }
}
