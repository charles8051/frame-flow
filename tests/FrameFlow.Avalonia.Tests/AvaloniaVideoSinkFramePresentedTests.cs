using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins <see cref="IFramePresentedSource"/> on the CPU surface: the event reports the frame
/// that reached the screen, at the moment it reached it.
/// </summary>
/// <remarks>
/// <para>
/// The event exists because work attached to the pipeline runs where the graph runs, which
/// is ahead of the display by whatever the pacer buffers. An overlay posting from there
/// draws over a picture that has not been shown. These tests assert the alternative: the
/// signal carries the PTS of the frame the swap just published, and stays silent for frames
/// that never drew.
/// </para>
/// <para>
/// Everything here is driven by pumping the dispatcher, so no test observes wall-clock time.
/// </para>
/// </remarks>
public sealed class AvaloniaVideoSinkFramePresentedTests
{
    private const int W = 32;
    private const int H = 16;

    [AvaloniaFact]
    public void TheEventCarriesThePtsOfTheFrameTheSwapPublished()
    {
        var (_, sink) = NewAttachedView();
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);

        // Allocates the buffers, and is drawn by the same posted callback (#287), so it is
        // reported like any other frame that reached the swap.
        PresentOffUiThread(sink, Pts(1));
        Dispatcher.UIThread.RunJobs();

        PresentOffUiThread(sink, Pts(2));
        Dispatcher.UIThread.RunJobs(); // the swap

        Assert.Equal([Pts(1), Pts(2)], presented);
    }

    [AvaloniaFact]
    public void TheEventIsNotRaisedBeforeTheSwapRuns()
    {
        var (_, sink) = NewAttachedView();
        var presented = new List<TimeSpan>();

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);

        // The copy has happened on the producer's thread, but nothing has drawn: the swap is
        // still queued. A consumer keyed off this event must not act on the frame yet.
        PresentOffUiThread(sink, Pts(2));
        Assert.Empty(presented);

        Dispatcher.UIThread.RunJobs();
        Assert.Equal([Pts(2)], presented);
    }

    [AvaloniaFact]
    public void ASupersededFrameIsNeverReported()
    {
        var (_, sink) = NewAttachedView();
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);

        PresentOffUiThread(sink, Pts(1)); // allocates, and draws with the allocation (#287)
        Dispatcher.UIThread.RunJobs();

        // Three copies with the UI thread never pumped: only the last survives to the swap.
        PresentOffUiThread(sink, Pts(2));
        PresentOffUiThread(sink, Pts(3));
        PresentOffUiThread(sink, Pts(4));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([Pts(1), Pts(4)], presented);
        Assert.Equal(presented.Count, sink.RenderedFrameCount);
    }

    [AvaloniaFact]
    public void EveryPresentedFrameIsReportedExactlyOnce_InOrder()
    {
        var (_, sink) = NewAttachedView();
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);

        for (int i = 1; i <= 6; i++)
        {
            PresentOffUiThread(sink, Pts(i)); // i == 1 allocates, and draws with it (#287)
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal([Pts(1), Pts(2), Pts(3), Pts(4), Pts(5), Pts(6)], presented);
        Assert.Equal(presented.Count, sink.RenderedFrameCount);
    }

    [AvaloniaFact]
    public void AFrameStrandedByDetachIsNotReported()
    {
        var (view, sink) = NewAttachedView();
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);

        PresentOffUiThread(sink, Pts(1)); // allocates, and draws with the allocation (#287)
        Dispatcher.UIThread.RunJobs();

        PresentOffUiThread(sink, Pts(2)); // copied, swap still queued
        ((Panel)view.Parent!).Children.Remove(view);
        Dispatcher.UIThread.RunJobs();

        // Pts(2) never drew, so it is a drop and goes unreported. Reporting it would tell an
        // overlay to draw over a picture nobody saw. Pts(1) is there because it did draw.
        Assert.Equal([Pts(1)], presented);
        Assert.Equal(presented.Count, sink.RenderedFrameCount);
    }

    [AvaloniaFact]
    public void AThrowingHandlerDoesNotStopThePresenter()
    {
        var (view, sink) = NewAttachedView();
        var seen = new List<TimeSpan>();

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();

        sink.FramePresented += (_, _) => throw new InvalidOperationException("handler failed");
        sink.FramePresented += (_, e) => seen.Add(e.PresentationTime);

        PresentOffUiThread(sink, Pts(2));
        Dispatcher.UIThread.RunJobs();

        // The swap counted the frame, and the surface still works afterwards. A consumer that
        // cannot process one present is not a reason to stop drawing. Two, because the
        // allocation frame drew as well (#287).
        Assert.Equal(2, view.RenderedFrameCount);
        Assert.Equal(Pts(2), sink.GetDiagnostics().LastPresentedPresentationTime);

        PresentOffUiThread(sink, Pts(3));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, view.RenderedFrameCount);

        // The handler that threw does not starve the one behind it: the second subscriber saw
        // both presents. A chain invocation would have abandoned it at the first throw.
        Assert.Equal([Pts(2), Pts(3)], seen);
    }

    [AvaloniaFact]
    public void TheEventAgreesWithTheDiagnosticsSnapshot()
    {
        var (_, sink) = NewAttachedView();
        TimeSpan? lastEvent = null;
        sink.FramePresented += (_, e) => lastEvent = e.PresentationTime;

        PresentOffUiThread(sink, Pts(1)); // allocates
        Dispatcher.UIThread.RunJobs();
        PresentOffUiThread(sink, Pts(9));
        Dispatcher.UIThread.RunJobs();

        // Both report the same present. The snapshot is polled; the event is pushed. They
        // must never disagree about which frame is on screen.
        Assert.Equal(sink.GetDiagnostics().LastPresentedPresentationTime, lastEvent);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static TimeSpan Pts(int n) => TimeSpan.FromMilliseconds(n * 16);

    private static AvaloniaVideoSink NewSink() =>
        new();

    private static (FrameFlowVideoView View, AvaloniaVideoSink Sink) NewAttachedView()
    {
        var sink = NewSink();
        var view = new FrameFlowVideoView { Sink = sink };

        var window = new Window { Width = 200, Height = 100, Content = new Panel { Children = { view } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (view, sink);
    }

    private static void PresentOffUiThread(AvaloniaVideoSink sink, TimeSpan pts) =>
        Task.Run(async () => await sink.PresentAsync(new BgraFrame(W, H, pts), CancellationToken.None))
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

        // Matches Media.CpuVideoFrame, which is what the view is handed in a real pipeline:
        // decoder-produced frames are one-shot, their buffer goes back to the pool when the
        // present call returns, and AddRef throws. A double that answered `this` let a fix
        // that retained the frame across a dispatcher post pass here and fail on the bench
        // (#287), so it answers the way the real frame does.
        public IVideoFrame AddRef() =>
            throw new NotSupportedException("One-shot frame: ref counting is not supported.");

        public void Dispose() { }

        public CpuFrameData? AsCpu() =>
            new(_pixels, default, default, width * 4, 0, 0, width, height);

        public CpuFrameData ToCpu() => AsCpu()!.Value;
    }
}
