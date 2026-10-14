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
/// Pins ADR-0085 decisions 2, 3 and 5 on <see cref="CompositionInteropVideoView"/>: the view keeps
/// its sink for its whole life and presents again after it leaves the visual tree and rejoins it,
/// what the sink keeps while the view does not present, and that work begun under a binding that
/// has ended commits nothing.
/// </summary>
/// <remarks>
/// <para>
/// The surface visual is the observation <see cref="CompositionInteropVideoViewSetupTests"/> uses:
/// setup builds it on any platform. Presenting a frame needs the GPU interop, which the headless
/// backend does not have, so a present stops short of the compositor here. What these watch is the
/// view's side of it: the frame still in the sink, and a present posted for it while the view
/// presents (<c>PresentsPosted</c>). The compositor's half of a present is the same code it was.
/// </para>
/// <para>
/// A late callback is driven by holding on to it: the arrival hook a producer thread had already
/// read, and the binding a hand-off in flight was presented under, called after the transition
/// that ended them.
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
    public void TheViewsOwnSink_OutlivesADetach_AndIsTheOneTheReattachPresents()
    {
        // The player built with it keeps working: the view hands out no other sink.
        var view = new CompositionInteropVideoView();
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(log);

        Detach(window);

        Assert.Same(sink, view.Sink);
        Assert.Null(sink.FrameArrived); // the producer does not call into a view out of the tree
        ((IClaimableSink)sink).TryClaim("a player")!.Dispose(); // not disposed: a disposed sink throws

        Reattach(window, view);

        Assert.Same(sink, view.EnsureSink());
        Assert.Same(sink, ((IVideoSurface)view).AttachSink(log));
        Assert.NotNull(sink.FrameArrived);
        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        Assert.Single(log.MessagesFrom<CompositionInteropVideoView>(), m => m.Contains(TeardownCompleted, StringComparison.Ordinal));
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
    public async Task AFrameGivenAfterTheReattach_IsPresented()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        Detach(window);
        Reattach(window, view);
        var posted = view.PresentsPosted;
        var frame = new TrackedFrame();

        await sink.PresentAsync(frame, CancellationToken.None);

        Assert.Equal(posted + 1, view.PresentsPosted);
        Assert.False(frame.Disposed);
        Assert.True(sink.HasPendingFrame); // waiting for the interop, which the headless backend lacks
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFrameKeptWhileDetached_IsPresentedOnReattach()
    {
        // A player that pauses or ends while the view is away sends no other: the frame it sent
        // last is the one meant to be on screen when the view comes back.
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        using var player = ((IClaimableSink)sink).TryClaim("a player built for 'still.png'");
        Detach(window);
        var kept = new TrackedFrame();
        await sink.PresentAsync(kept, CancellationToken.None);
        var posted = view.PresentsPosted;
        Assert.False(kept.Disposed);
        Assert.Equal(0, sink.FramesSuperseded);

        Reattach(window, view);

        Assert.True(view.PresentsPosted > posted);
        Assert.False(kept.Disposed);
        Assert.True(sink.HasPendingFrame);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnUnclaimedSink_KeepsNothingWhileDetached()
    {
        // Fed by a graph built by hand, which takes no claim: nothing would say when to let a kept
        // frame go.
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        var pending = new TrackedFrame();
        await sink.PresentAsync(pending, CancellationToken.None);

        Detach(window);
        var late = new TrackedFrame();
        await sink.PresentAsync(late, CancellationToken.None);

        Assert.True(pending.Disposed); // let go of as the view stopped presenting
        Assert.True(late.Disposed);
        Assert.False(sink.HasPendingFrame);
        Assert.Equal(2, sink.FramesDiscardedOffScreen);
        Assert.Equal(0, sink.FramesSuperseded);

        var posted = view.PresentsPosted;
        Reattach(window, view);
        Assert.Equal(posted, view.PresentsPosted); // nothing to present
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReleasingTheClaim_WhileDetached_DisposesTheKeptFrame()
    {
        // A view off screen pins no decoder surface for a player that is gone.
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        var player = ((IClaimableSink)sink).TryClaim("a player")!;
        Detach(window);
        var kept = new TrackedFrame();
        await sink.PresentAsync(kept, CancellationToken.None);
        Assert.False(kept.Disposed);

        player.Dispose();

        Assert.True(kept.Disposed);
        Assert.False(sink.HasPendingFrame);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LateCallbacks_FromAnEndedBinding_CommitNothing()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);
        await sink.PresentAsync(new TrackedFrame(), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        // As the producer thread and a hand-off in flight hold them: raised under this binding.
        var arrived = sink.FrameArrived!;
        var binding = view.Binding;

        // While the binding stands, both commit.
        var posted = view.PresentsPosted;
        arrived();
        view.OnPresentCommitted(sink, binding, Ms(1), TimelineId.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(posted + 1, view.PresentsPosted);
        Assert.Equal([Ms(1)], presented);

        Detach(window);
        arrived();
        view.OnPresentCommitted(sink, binding, Ms(2), TimelineId.None);
        Assert.Equal(posted + 1, view.PresentsPosted);

        // Presenting again, under a binding of its own: the old one's callbacks still commit nothing.
        Reattach(window, view);
        posted = view.PresentsPosted;
        arrived();
        view.OnPresentCommitted(sink, binding, Ms(3), TimelineId.None);
        Assert.Equal(posted, view.PresentsPosted);

        sink.FrameArrived!();
        view.OnPresentCommitted(sink, view.Binding, Ms(4), TimelineId.None);
        Assert.Equal(posted + 1, view.PresentsPosted);
        Assert.Equal([Ms(1), Ms(4)], presented);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisposeAfterADetach_DisposesTheViewsOwnSink_WithoutASecondTeardown()
    {
        var view = new CompositionInteropVideoView();
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(log);
        using var player = ((IClaimableSink)sink).TryClaim("a player");
        Detach(window);
        var kept = new TrackedFrame();
        await sink.PresentAsync(kept, CancellationToken.None);

        await view.DisposeAsync();

        Assert.True(kept.Disposed);
        Assert.Null(view.Sink);
        Assert.Throws<ObjectDisposedException>(() => ((IClaimableSink)sink).TryClaim("another player"));
        Assert.Single(log.MessagesFrom<CompositionInteropVideoView>(), m => m.Contains(TeardownCompleted, StringComparison.Ordinal));

        Reattach(window, view); // disposed is final
        Assert.Null(ElementComposition.GetElementChildVisual(view));
        Assert.False(view.IsWatchingForStalls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisposeAsync_LetsGoOfACallersSink_WithoutDisposingIt()
    {
        var view = new CompositionInteropVideoView();
        var sink = new CompositionInteropVideoSink();
        view.Sink = sink;
        view.Initialize(new RecordingLoggerFactory());
        var window = Show(view);

        await view.DisposeAsync();

        Assert.Null(view.Sink);
        Assert.Null(sink.FrameArrived);
        ((IClaimableSink)sink).TryClaim("a player")!.Dispose(); // still the caller's, and usable
        window.Close();
    }

    [AvaloniaFact]
    public void TheOwnSinksDiagnostics_SpanADetachAndAReattach()
    {
        // The player built with the sink may poll it across the trip: it reads the session's
        // counts, not zeros.
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());

        Detach(window);
        Assert.NotSame(VideoSinkDiagnosticsSnapshot.Empty, sink.GetDiagnostics());

        Reattach(window, view);
        Assert.NotSame(VideoSinkDiagnosticsSnapshot.Empty, sink.GetDiagnostics());
        window.Close();
    }

    [AvaloniaFact]
    public async Task ADetach_WhoseSinkFailsToReleaseItsFrame_StillTakesTheAttachmentDown()
    {
        // With no claim, the sink lets go of its frame as the view stops presenting. The core has
        // moved to Detached and will not ask for the stop again, so a throw from that frame must not
        // leave the surface and the watchdog up with nothing left to take them down.
        var view = new CompositionInteropVideoView();
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(log);
        await sink.PresentAsync(new TrackedFrame(throwOnDispose: true), CancellationToken.None);

        Detach(window);

        Assert.Null(ElementComposition.GetElementChildVisual(view));
        Assert.False(view.IsWatchingForStalls);
        var messages = log.MessagesFrom<CompositionInteropVideoView>();
        Assert.Contains(messages, m => m.Contains(TeardownCompleted, StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("teardown faulted", StringComparison.Ordinal)); // not swallowed
        Assert.Same(sink, view.Sink);

        Reattach(window, view);
        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        Assert.NotNull(sink.FrameArrived);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisposeAsync_WhoseSinkFailsToReleaseItsFrame_StillTearsDown_DisposesTheSink_AndReturnsTheFault()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        await sink.PresentAsync(new TrackedFrame(throwOnDispose: true), CancellationToken.None);

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() => view.DisposeAsync().AsTask());

        Assert.Equal(TrackedFrame.DisposeFault, fault.Message);
        Assert.Null(ElementComposition.GetElementChildVisual(view));
        Assert.False(view.IsWatchingForStalls);
        Assert.Null(view.Sink);
        Assert.Throws<ObjectDisposedException>(() => ((IClaimableSink)sink).TryClaim("a player"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task AfterDisposeAsync_TheViewRefusesASink_AndKeepsNone()
    {
        // DisposeAsync is final: no later detach or dispose would present a sink made or bound
        // afterwards, or release it and the frame it holds.
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        await view.DisposeAsync();
        await using var callers = new CompositionInteropVideoSink();

        Assert.Throws<ObjectDisposedException>(() => view.EnsureSink());
        Assert.Throws<ObjectDisposedException>(() => view.Initialize(new RecordingLoggerFactory()));
        Assert.Throws<ObjectDisposedException>(() => ((IVideoSurface)view).AttachSink(new RecordingLoggerFactory()));
        Assert.Throws<ObjectDisposedException>(() => view.Sink = callers);

        Assert.Null(view.Sink);
        Assert.Null(callers.FrameArrived);
        view.Sink = null; // clearing it is still allowed, and does nothing
        window.Close();
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

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

    /// <summary>
    /// A frame that records its disposal, and can throw from it. Nothing here reads its pixels.
    /// </summary>
    internal sealed class TrackedFrame(bool throwOnDispose = false) : IVideoFrame
    {
        public const string DisposeFault = "The frame failed to release.";

        public bool Disposed { get; private set; }
        public int Width => 16;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() =>
            throw new NotSupportedException("One-shot frame: ref counting is not supported.");

        public void Dispose()
        {
            Disposed = true;
            if (throwOnDispose)
                throw new InvalidOperationException(DisposeFault);
        }

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
