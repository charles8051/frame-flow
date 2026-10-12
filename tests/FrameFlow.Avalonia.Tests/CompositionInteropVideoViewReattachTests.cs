using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #650: <see cref="CompositionInteropVideoView"/> presents again after it leaves the visual
/// tree and rejoins it, as <see cref="FrameFlowVideoView"/> does, and each detach releases what
/// its attachment made.
/// </summary>
/// <remarks>
/// <para>
/// The view treated its first detach as a dispose: it set its disposed and torn-down flags, kept
/// its composition-ready flag, and nothing cleared them. A view that rejoined the tree, in a tab
/// control or a docking layout, set no surface up, held every frame, and showed a black rectangle.
/// A second detach returned early, so a sink made after the first was never disposed.
/// </para>
/// <para>
/// The surface visual is the observation <see cref="CompositionInteropVideoViewSetupTests"/>
/// uses: setup builds it on any platform. Presenting a frame needs the GPU interop, which the
/// headless backend does not have, so nothing here watches a frame reach the compositor.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoViewReattachTests
{
    private const string TeardownCompleted = "Presenter teardown completed cleanly";

    [AvaloniaFact]
    public void ARejoinedView_SetsItsSurfaceUpAgain()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        Assert.NotNull(ElementComposition.GetElementChildVisual(view));

        Detach(window);
        Assert.Null(ElementComposition.GetElementChildVisual(view));

        Reattach(window, view);

        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        window.Close();
    }

    [AvaloniaFact]
    public void ARejoinedView_WatchesForStallsAgain()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        Assert.True(view.IsWatchingForStalls);

        Detach(window);
        Assert.False(view.IsWatchingForStalls); // a view out of the tree is not kept alive by a timer

        Reattach(window, view);

        Assert.True(view.IsWatchingForStalls);
        window.Close();
        Assert.False(view.IsWatchingForStalls);
    }

    [AvaloniaFact]
    public async Task ASecondDetach_ReleasesTheSinkTheViewMadeAfterTheFirst()
    {
        var view = new CompositionInteropVideoView();
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        var first = ((IVideoSurface)view).AttachSink(log);

        Detach(window);
        Reattach(window, view);
        var second = view.EnsureSink();
        Assert.NotSame(first, second); // the first went with the detach, as on FrameFlowVideoView
        var held = new TrackedFrame();
        await second.PresentAsync(held, CancellationToken.None);

        Detach(window);

        Assert.True(held.Disposed); // the second sink was disposed, and the frame it held with it
        Assert.Null(view.Sink);
        Assert.Equal(2, log.MessagesFrom<CompositionInteropVideoView>().Count(m => m.Contains(TeardownCompleted, StringComparison.Ordinal)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisposeAfterADetach_ReleasesASinkMadeSince_WithoutASecondTeardown()
    {
        var view = new CompositionInteropVideoView();
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(log);
        Detach(window);

        var made = view.EnsureSink();
        var held = new TrackedFrame();
        await made.PresentAsync(held, CancellationToken.None);
        await view.DisposeAsync();

        Assert.True(held.Disposed);
        Assert.Null(view.Sink);
        Assert.Single(log.MessagesFrom<CompositionInteropVideoView>(), m => m.Contains(TeardownCompleted, StringComparison.Ordinal));

        Reattach(window, view); // disposed is final
        Assert.Null(ElementComposition.GetElementChildVisual(view));
        Assert.False(view.IsWatchingForStalls);
        window.Close();
    }

    [AvaloniaFact]
    public void ADetach_KeepsACallersSink_AndTheReattachBindsItAgain()
    {
        var view = new CompositionInteropVideoView();
        var sink = new CompositionInteropVideoSink();
        view.Sink = sink;
        view.Initialize(new RecordingLoggerFactory());
        var window = Show(view);
        Assert.NotNull(sink.FrameArrived);

        Detach(window);
        Assert.Same(sink, view.Sink);
        Assert.Null(sink.FrameArrived); // the producer does not call into a view out of the tree

        Reattach(window, view);

        Assert.Same(sink, view.Sink);
        Assert.NotNull(sink.FrameArrived);
        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        window.Close();
    }

    [AvaloniaFact]
    public void ASinkMadeAfterADetach_EndsTheDisposedSinksDiagnostics()
    {
        // The player built with the first sink may still poll it. Once the view presents for a
        // successor, those presents are not the first sink's.
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var first = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());

        Detach(window);
        Assert.NotSame(VideoSinkDiagnosticsSnapshot.Empty, first.GetDiagnostics()); // its final counts

        Reattach(window, view);
        var second = view.EnsureSink();

        Assert.Same(VideoSinkDiagnosticsSnapshot.Empty, first.GetDiagnostics());
        Assert.NotSame(VideoSinkDiagnosticsSnapshot.Empty, second.GetDiagnostics());
        window.Close();
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

    /// <summary>A frame that records its disposal. Nothing here reads its pixels.</summary>
    private sealed class TrackedFrame : IVideoFrame
    {
        public bool Disposed { get; private set; }
        public int Width => 16;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() =>
            throw new NotSupportedException("One-shot frame: ref counting is not supported.");

        public void Dispose() => Disposed = true;

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
