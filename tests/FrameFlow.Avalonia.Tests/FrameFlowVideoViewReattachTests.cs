using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0085 decisions 2 and 5 on <see cref="FrameFlowVideoView"/>: the view keeps its sink,
/// its own or a caller's, for its whole life and presents it again after it leaves the visual tree
/// and rejoins it; what the sink keeps while the view is away; that the picture the view drew last
/// is drawn again; and that a callback raised under a binding that has ended copies nothing.
/// </summary>
/// <remarks>
/// <para>
/// #663 was the caller's-sink half of this: a detach ended the binding, and the attach bound nothing
/// because the view still held the sink, so the picture froze and nothing was logged. The view's own
/// sink went the other way, disposed on every detach, which left a player built with it feeding a
/// disposed sink.
/// </para>
/// <para>
/// A player holding the sink is played by a claim taken through <see cref="IClaimableSink"/>, which
/// is what the player builder does at build. A late callback is driven by holding on to it: the
/// arrival hook a producer thread had already read, called after the transition that ended it.
/// </para>
/// </remarks>
public sealed class FrameFlowVideoViewReattachTests
{
    [AvaloniaFact]
    public void TheViewsOwnSink_OutlivesADetach_AndIsTheOneTheReattachPresents()
    {
        // The player built with it keeps working: the view hands out no other sink.
        var (view, window, sink) = Show(owned: true);
        var log = new RecordingLoggerFactory();

        Detach(window);

        Assert.Same(sink, view.Sink);
        Assert.Null(sink.FrameArrived); // the producer does not call into a view out of the tree
        ((IClaimableSink)sink).TryClaim("a player")!.Dispose(); // not disposed: a disposed sink throws

        Reattach(window, view);

        Assert.Same(sink, view.Sink);
        Assert.Same(sink, view.EnsureSink());
        Assert.Same(sink, ((IVideoSurface)view).AttachSink(log));
        Assert.NotNull(sink.FrameArrived);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFrameGivenAfterTheReattach_IsPresented(bool owned)
    {
        // With a caller's sink this is #663's sequence: assign, show, detach, reattach, present.
        var (view, window, sink) = Show(owned);
        Detach(window);
        Reattach(window, view);

        await sink.PresentAsync(new TrackedFrame(Pts(1)), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(1, sink.RenderedFrameCount);
        Assert.Equal(Pts(1), sink.GetDiagnostics().LastPresentedPresentationTime);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFrameKeptWhileDetached_IsPresentedOnReattach(bool owned)
    {
        // A player that pauses or ends while the view is away sends no other: the frame it sent
        // last is the one meant to be on screen when the view comes back.
        var (view, window, sink) = Show(owned);
        using var player = ((IClaimableSink)sink).TryClaim("a player built for 'still.png'");
        Detach(window);
        var replaced = new TrackedFrame(Pts(1));
        var kept = new TrackedFrame(Pts(2));
        await sink.PresentAsync(replaced, CancellationToken.None);
        await sink.PresentAsync(kept, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, replaced.DisposeCount);
        Assert.Equal(0, kept.DisposeCount);
        Assert.Equal(0, view.RenderedFrameCount);

        Reattach(window, view);

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(Pts(2), sink.GetDiagnostics().LastPresentedPresentationTime);
        Assert.Equal(1, kept.DisposeCount); // copied, then released
        Assert.Equal(0, sink.DroppedFrameCount);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnclaimedSink_KeepsNothingWhileDetached(bool owned)
    {
        // Fed by a graph built by hand, which takes no claim: nothing would say when to let a kept
        // frame go.
        var (view, window, sink) = Show(owned);
        Detach(window);
        var late = new TrackedFrame(Pts(1));

        await sink.PresentAsync(late, CancellationToken.None);

        Assert.Equal(1, late.DisposeCount);
        Assert.False(sink.HasPendingFrame);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
        Assert.Equal(0, sink.DroppedFrameCount);

        Reattach(window, view);
        Assert.Equal(0, view.RenderedFrameCount); // nothing to present
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReleasingTheClaim_WhileDetached_DisposesTheKeptFrame(bool owned)
    {
        // A view off screen pins no frame for a player that is gone.
        var (view, window, sink) = Show(owned);
        var player = ((IClaimableSink)sink).TryClaim("a player")!;
        Detach(window);
        var kept = new TrackedFrame(Pts(1));
        await sink.PresentAsync(kept, CancellationToken.None);
        Assert.Equal(0, kept.DisposeCount);

        player.Dispose();

        Assert.Equal(1, kept.DisposeCount);
        Assert.False(sink.HasPendingFrame);
        Reattach(window, view);
        Assert.Equal(0, view.RenderedFrameCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ThePausedPicture_IsStillDrawnAfterAReattach()
    {
        // The view copies each frame into bitmaps of its own, which hold nothing of the player's,
        // so it keeps the picture across the trip whether or not anyone holds the sink. A paused
        // player sends nothing more.
        var (view, window, sink) = Show(owned: true);
        await sink.PresentAsync(new TrackedFrame(Pts(1)), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        var picture = view.Picture;
        Assert.NotNull(picture);

        Detach(window);
        Reattach(window, view);

        // The bitmap the frame was swapped into, and no swap since: the same picture.
        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Same(picture, view.Picture);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ALateCallback_AfterADetach_CopiesNothing()
    {
        // Not a witness for the binding number: a view out of the tree does not present, and the
        // copy refuses on that alone. ALateCallback_FromASinkTheViewReplaced_CopiesNothing is the
        // one where only the number tells the callback apart.
        var (view, window, sink) = Show(owned: true);
        using var player = ((IClaimableSink)sink).TryClaim("a player");

        // As the producer thread holds it: read before the detach, called after it.
        var arrived = sink.FrameArrived!;
        Detach(window);
        var kept = new TrackedFrame(Pts(1));
        await sink.PresentAsync(kept, CancellationToken.None);

        arrived();
        Dispatcher.UIThread.RunJobs();

        Assert.True(sink.HasPendingFrame); // left with the sink, which keeps it for the player
        Assert.Equal(0, kept.DisposeCount);
        Assert.Equal(0, view.RenderedFrameCount);

        Reattach(window, view); // under a binding of its own
        Assert.Equal(1, view.RenderedFrameCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ALateCallback_FromASinkTheViewReplaced_CopiesNothing()
    {
        // The view presents again, for the replacement, so only the binding number tells the old
        // sink's callback apart from a current one.
        var view = new FrameFlowVideoView();
        var first = new AvaloniaVideoSink();
        view.Sink = first;
        var window = ShowWindow(view);
        var arrived = first.FrameArrived!;
        view.Sink = new AvaloniaVideoSink();
        using var player = ((IClaimableSink)first).TryClaim("a player still feeding the first sink");
        var late = new TrackedFrame(Pts(1));
        await first.PresentAsync(late, CancellationToken.None);

        arrived();
        Dispatcher.UIThread.RunJobs();

        Assert.True(first.HasPendingFrame);
        Assert.Equal(0, view.RenderedFrameCount);
        Assert.Equal(0, first.RenderedFrameCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ASinkAssignedWhileDetached_IsBoundAndPresentedOnReattach()
    {
        // Out of the tree the view takes the sink up and binds nothing; the reattach binds it and
        // presents the frame it kept for its player.
        var (view, window, _) = Show(owned: true);
        Detach(window);
        var assigned = new AvaloniaVideoSink();
        using var player = ((IClaimableSink)assigned).TryClaim("a player built for 'still.png'");

        view.Sink = assigned;
        var kept = new TrackedFrame(Pts(1));
        await assigned.PresentAsync(kept, CancellationToken.None);

        Assert.Same(assigned, view.Sink);
        Assert.Null(assigned.FrameArrived);
        Assert.Equal(0, kept.DisposeCount);

        Reattach(window, view);

        Assert.NotNull(assigned.FrameArrived);
        Assert.Equal(1, assigned.RenderedFrameCount);
        await assigned.PresentAsync(new TrackedFrame(Pts(2)), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, assigned.RenderedFrameCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClearingTheSink_WhileAttached_StopsPresenting_AndKeepsThePicture()
    {
        // Presenting needs a sink. The picture is the view's own bitmaps, so it stays.
        var view = new FrameFlowVideoView();
        var callers = new AvaloniaVideoSink();
        view.Sink = callers;
        var window = ShowWindow(view);
        await callers.PresentAsync(new TrackedFrame(Pts(1)), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        var picture = view.Picture;
        Assert.NotNull(picture);

        view.Sink = null;

        Assert.Null(view.Sink);
        Assert.Null(callers.FrameArrived);
        Assert.Same(picture, view.Picture);
        var late = new TrackedFrame(Pts(2));
        await callers.PresentAsync(late, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, late.DisposeCount); // no view presents it, and no player holds it
        Assert.Equal(1, view.RenderedFrameCount);
        window.Close();
    }

    // ─── Helpers ────────────────────────────────────────────────────

    internal const int W = 32;
    internal const int H = 16;

    internal static TimeSpan Pts(int n) => TimeSpan.FromMilliseconds(n * 16);

    /// <summary>
    /// A view on screen with its own sink, made as it attached, or with a caller's sink assigned
    /// before it attached.
    /// </summary>
    private static (FrameFlowVideoView View, Window Window, AvaloniaVideoSink Sink) Show(bool owned)
    {
        var view = new FrameFlowVideoView();
        if (!owned)
            view.Sink = new AvaloniaVideoSink();

        var window = ShowWindow(view);
        return (view, window, view.Sink!);
    }

    internal static Window ShowWindow(Control view)
    {
        var window = new Window { Width = 200, Height = 100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static void Detach(Window window)
    {
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Reattach(Window window, Control view)
    {
        window.Content = view;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Packed BGRA32 frame over a real buffer, so the copy has something to copy, that counts its
    /// disposals. One-shot, like the <c>CpuVideoFrame</c> the view is handed in a real pipeline.
    /// </summary>
    internal sealed class TrackedFrame(TimeSpan pts, int width = W, int height = H) : IVideoFrame
    {
        private readonly byte[] _pixels = new byte[width * height * 4];
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int Width => width;
        public int Height => height;
        public TimeSpan Pts => pts;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() =>
            throw new NotSupportedException("One-shot frame: ref counting is not supported.");

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        public CpuFrameData? AsCpu() =>
            new(_pixels, default, default, width * 4, 0, 0, width, height);

        public CpuFrameData ToCpu() => AsCpu()!.Value;
    }
}
