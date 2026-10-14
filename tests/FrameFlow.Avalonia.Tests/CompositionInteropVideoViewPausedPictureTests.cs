using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0085 decision 2's paused picture on <see cref="CompositionInteropVideoView"/>: the view
/// holds the frame it presented last, hands it back to its sink when it leaves the visual tree, and
/// presents it again when it comes back, so a paused, ended or single-frame item is on screen at once
/// with nothing new from the player. The frame never outlives the view's hold on its sink.
/// </summary>
/// <remarks>
/// The headless backend has no GPU interop, so no present reaches the compositor. Each test plays the
/// present's last step itself: it takes the frame from the sink as the present does, and hands it to
/// <c>KeepPresented</c>, which the present calls once a frame's hand-off has reached the compositor.
/// A paused player is one that gives the sink nothing more. Every frame ends with no reference left.
/// </remarks>
public sealed class CompositionInteropVideoViewPausedPictureTests
{
    [AvaloniaFact]
    public async Task APausedPicture_IsPresentedAgainOnReattach()
    {
        var (view, window, sink) = ShowWithItsOwnSink();
        var player = ((IClaimableSink)sink).TryClaim("a paused player")!;
        var frame = await PresentAsync(view, sink);

        Detach(window);
        Assert.Equal(1, frame.References);
        var posted = view.PresentsPosted;

        Reattach(window, view);

        Assert.True(view.PresentsPosted > posted); // posted for present again
        Assert.Equal(1, sink.FramesAccepted); // and nothing new was given
        var again = sink.TakePendingFrame();
        Assert.Same(frame, again);
        view.KeepPresented(again!);
        await view.DisposeAsync();
        player.Dispose();
        Assert.Equal(0, frame.References);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithNoClaim_ThePausedPictureIsReleasedAtTheDetach_AndDoesNotComeBack()
    {
        // A graph built by hand takes no claim, so nothing would say when to let the frame go.
        var (view, window, sink) = ShowWithItsOwnSink();
        var frame = await PresentAsync(view, sink);

        Detach(window);
        Assert.Equal(0, frame.References);
        var posted = view.PresentsPosted;

        Reattach(window, view);

        Assert.Equal(posted, view.PresentsPosted);
        Assert.False(sink.HasPendingFrame);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReleasingTheClaim_WhileDetached_ReleasesThePausedPicture()
    {
        var (view, window, sink) = ShowWithItsOwnSink();
        var player = ((IClaimableSink)sink).TryClaim("a paused player")!;
        var frame = await PresentAsync(view, sink);
        Detach(window);
        Assert.Equal(1, frame.References);

        player.Dispose();

        Assert.Equal(0, frame.References);
        Assert.False(sink.HasPendingFrame);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ANewerFrameWhileDetached_ReplacesThePausedPicture()
    {
        var (view, window, sink) = ShowWithItsOwnSink();
        using var player = ((IClaimableSink)sink).TryClaim("a player");
        var frame = await PresentAsync(view, sink);
        Detach(window);

        var newer = new CountedFrame();
        await sink.PresentAsync(newer, CancellationToken.None);

        Assert.Equal(0, frame.References);
        Reattach(window, view);
        Assert.Same(newer, sink.TakePendingFrame());
        newer.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheNextPresent_ReleasesTheFramePresentedBefore()
    {
        var (view, window, sink) = ShowWithItsOwnSink();
        var first = await PresentAsync(view, sink);

        var second = await PresentAsync(view, sink);

        Assert.Equal(0, first.References);
        Assert.Equal(1, second.References);
        await view.DisposeAsync();
        Assert.Equal(0, second.References);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OneFrameSentTwice_LeavesOneReferenceHeld()
    {
        // A producer that yields one frame twice, as an operator passing input.AddRef() on twice
        // does, has sent two references to the same instance. The view holds one of them.
        var (view, window, sink) = ShowWithItsOwnSink();
        var frame = await PresentAsync(view, sink);
        frame.AddRef();

        await PresentAsync(view, sink, frame);

        Assert.Equal(1, frame.References);
        await view.DisposeAsync();
        Assert.Equal(0, frame.References);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReleasingTheClaim_WhilePresenting_ReleasesTheFramePresentedLast()
    {
        // The player is gone: its decoder surface is let go of, and the picture stays up as the
        // compositor's copy.
        var (view, window, sink) = ShowWithItsOwnSink();
        var player = ((IClaimableSink)sink).TryClaim("a player")!;
        var frame = await PresentAsync(view, sink);

        player.Dispose();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, frame.References);
        Assert.NotNull(sink.FrameArrived); // still presenting
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheReleaseAtAClaimsEnd_LeavesAFrameANewPlayerPresentedSince()
    {
        var (view, window, sink) = ShowWithItsOwnSink();
        var first = ((IClaimableSink)sink).TryClaim("the first player")!;
        var old = await PresentAsync(view, sink);
        first.Dispose(); // posts the release; it has not run yet
        using var second = ((IClaimableSink)sink).TryClaim("the second player");
        var fresh = new CountedFrame();
        await sink.PresentAsync(fresh, CancellationToken.None);
        view.KeepPresented(sink.TakePendingFrame()!); // presented before the posted release runs

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, old.References); // released by the present that took over
        Assert.Equal(1, fresh.References);
        await view.DisposeAsync();
        Assert.Equal(0, fresh.References);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisposeAsync_ReleasesTheFramePresentedLast_EvenFromACallersSinkAPlayerHolds()
    {
        // The caller's sink outlives the view, and the player holding it keeps what it is given.
        // The frame the view presented goes with the view.
        var view = new CompositionInteropVideoView();
        var callers = new CompositionInteropVideoSink();
        view.Sink = callers;
        view.Initialize(new RecordingLoggerFactory());
        var window = Show(view);
        using var player = ((IClaimableSink)callers).TryClaim("a player");
        var frame = await PresentAsync(view, callers);

        await view.DisposeAsync();

        Assert.Equal(0, frame.References);
        Assert.False(callers.HasPendingFrame);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReplacingTheSink_ReleasesTheFramePresentedFromIt()
    {
        var view = new CompositionInteropVideoView();
        var callers = new CompositionInteropVideoSink();
        view.Sink = callers;
        view.Initialize(new RecordingLoggerFactory());
        var window = Show(view);
        var frame = await PresentAsync(view, callers);

        view.Sink = new CompositionInteropVideoSink();

        Assert.Equal(0, frame.References);
        Assert.False(callers.HasPendingFrame);
        window.Close();
    }

    /// <summary>
    /// Gives the sink a frame and plays the present: takes it as the present does, and keeps it as
    /// presented.
    /// </summary>
    private static Task<CountedFrame> PresentAsync(CompositionInteropVideoView view, CompositionInteropVideoSink sink) =>
        PresentAsync(view, sink, new CountedFrame());

    private static async Task<CountedFrame> PresentAsync(
        CompositionInteropVideoView view,
        CompositionInteropVideoSink sink,
        CountedFrame frame
    )
    {
        await sink.PresentAsync(frame, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        var taken = sink.TakePendingFrame();
        Assert.Same(frame, taken);
        view.KeepPresented(taken!);
        return frame;
    }

    private static (CompositionInteropVideoView View, Window Window, CompositionInteropVideoSink Sink) ShowWithItsOwnSink()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        return (view, window, sink);
    }

    private static Window Show(Control view)
    {
        var window = new Window { Width = 200, Height = 100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Detach(Window window)
    {
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Reattach(Window window, Control view)
    {
        window.Content = view;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A frame that counts its references, so a test sees the last one released.</summary>
    private sealed class CountedFrame : IVideoFrame
    {
        private int _references = 1;

        public int References => Volatile.Read(ref _references);

        public int Width => 16;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef()
        {
            Interlocked.Increment(ref _references);
            return this;
        }

        public void Dispose() => Interlocked.Decrement(ref _references);

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
