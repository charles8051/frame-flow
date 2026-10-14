using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Media;
using static FrameFlow.Avalonia.Tests.FrameFlowVideoViewReattachTests;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0085 decision 2's assignment of <see cref="FrameFlowVideoView.Sink"/> and
/// <see cref="FrameFlowVideoView.DisposeAsync"/>: the view first claims the outgoing sink, refuses
/// while a player holds it, and otherwise disposes its own sink or lets go of a caller's, and
/// releases that claim; dispose is final.
/// </summary>
/// <remarks>
/// A player holding the sink is played by a claim taken through <see cref="IClaimableSink"/>, which
/// is what the player builder does at build.
/// </remarks>
public sealed class FrameFlowVideoViewSinkAssignmentTests
{
    private const string Holder = "a player built for 'clip.mp4'";

    [AvaloniaFact]
    public async Task AssigningASink_OverOneAPlayerHolds_ThrowsNamingTheHolder_AndChangesNothing()
    {
        var view = new FrameFlowVideoView();
        var window = ShowWindow(view);
        var held = view.Sink!;
        using var player = ((IClaimableSink)held).TryClaim(Holder);
        var replacement = new AvaloniaVideoSink();

        var error = Assert.Throws<InvalidOperationException>(() => view.Sink = replacement);
        Assert.Throws<InvalidOperationException>(() => view.Sink = null);

        Assert.Contains(Holder, error.Message, StringComparison.Ordinal);
        Assert.Same(held, view.Sink);
        Assert.Equal(Holder, ((IClaimableSink)held).Claimant);
        Assert.Null(replacement.FrameArrived);
        ((IClaimableSink)replacement).TryClaim("a player")!.Dispose(); // untouched

        // And the view still presents the held sink.
        await held.PresentAsync(new TrackedFrame(Pts(1)), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, view.RenderedFrameCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AssigningASink_OverTheViewsOwnIdleSink_DisposesIt()
    {
        var view = new FrameFlowVideoView();
        var window = ShowWindow(view);
        var own = view.Sink!;
        var replacement = new AvaloniaVideoSink();

        view.Sink = replacement;

        Assert.Same(replacement, view.Sink);
        Assert.Throws<ObjectDisposedException>(() => ((IClaimableSink)own).TryClaim("a player"));
        await replacement.PresentAsync(new TrackedFrame(Pts(1)), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, replacement.RenderedFrameCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AssigningASink_OverACallersIdleSink_LetsItGo_UnclaimedAndKeepingNothing()
    {
        var view = new FrameFlowVideoView();
        var callers = new AvaloniaVideoSink();
        view.Sink = callers;
        var window = ShowWindow(view);

        view.Sink = new AvaloniaVideoSink();

        Assert.Null(((IClaimableSink)callers).Claimant); // the view's own claim is released
        Assert.Null(callers.FrameArrived);
        var late = new TrackedFrame(Pts(1));
        await callers.PresentAsync(late, CancellationToken.None);
        Assert.Equal(1, late.DisposeCount); // no view presents it, and no player holds it
        Assert.Equal(1, callers.FramesDiscardedOffScreen);
        ((IClaimableSink)callers).TryClaim("a player")!.Dispose(); // not disposed: still the caller's
        window.Close();
    }

    [AvaloniaFact]
    public async Task AssigningASink_OverACallersSinkItsOwnerDisposed_GoesAhead()
    {
        // A disposed sink cannot be claimed, and presents nothing whoever still feeds it.
        var view = new FrameFlowVideoView();
        var callers = new AvaloniaVideoSink();
        view.Sink = callers;
        var window = ShowWindow(view);
        await callers.DisposeAsync();
        var replacement = new AvaloniaVideoSink();

        view.Sink = replacement;

        Assert.Same(replacement, view.Sink);
        Assert.NotNull(replacement.FrameArrived);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AssigningASinkWithAFrameWaiting_WhileTheViewPresents_PresentsThatFrame()
    {
        // A paused player fed the sink before any view took it up: its frame is the one meant to
        // be on screen, and nothing else is coming.
        var view = new FrameFlowVideoView();
        var window = ShowWindow(view);
        var callers = new AvaloniaVideoSink();
        await callers.PresentAsync(new TrackedFrame(Pts(1)), CancellationToken.None);

        view.Sink = callers;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(1, callers.RenderedFrameCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisposeAsync_DisposesTheViewsOwnSink_AndIsFinal()
    {
        var view = new FrameFlowVideoView();
        var window = ShowWindow(view);
        var own = view.Sink!;
        using var player = ((IClaimableSink)own).TryClaim("a player");
        Detach(window);
        var kept = new TrackedFrame(Pts(1));
        await own.PresentAsync(kept, CancellationToken.None);

        await view.DisposeAsync();

        Assert.Equal(1, kept.DisposeCount); // with the sink
        Assert.Null(view.Sink);
        Assert.Throws<ObjectDisposedException>(() => ((IClaimableSink)own).TryClaim("another player"));
        Assert.Throws<ObjectDisposedException>(() => view.EnsureSink());
        Assert.Throws<ObjectDisposedException>(() => ((IVideoSurface)view).AttachSink(new RecordingLoggerFactory()));
        Assert.Throws<ObjectDisposedException>(() => view.Sink = new AvaloniaVideoSink());
        view.Sink = null; // nothing to clear, and not an error

        Reattach(window, view); // attaching a disposed view is not an error either
        Assert.Null(view.Sink);
        await view.DisposeAsync(); // nor is a second dispose
        window.Close();
    }

    [AvaloniaFact]
    public async Task ADetach_AfterDisposeAsyncWhileAttached_IsNotAnError()
    {
        // Disposed in the visual tree, as an app's window-closing handler does, then taken out of
        // it as the window closes. The lifecycle has nothing to do for that detach.
        var view = new FrameFlowVideoView();
        var log = new RecordingLoggerFactory();
        var window = ShowWindow(view);
        ((IVideoSurface)view).AttachSink(log);

        await view.DisposeAsync();
        Detach(window);

        Assert.Null(view.Sink);
        Assert.DoesNotContain(log.MessagesFrom<FrameFlowVideoView>(), m => m.Contains("leaving the visual tree", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisposeAsync_LetsGoOfACallersSink_WithoutDisposingIt_AndReleasesThePicture()
    {
        var view = new FrameFlowVideoView();
        var callers = new AvaloniaVideoSink();
        view.Sink = callers;
        var window = ShowWindow(view);
        await callers.PresentAsync(new TrackedFrame(Pts(1)), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(view.Picture);

        await view.DisposeAsync();

        Assert.Null(view.Sink);
        Assert.Null(callers.FrameArrived);
        Assert.Null(view.Picture);
        var late = new TrackedFrame(Pts(2));
        await callers.PresentAsync(late, CancellationToken.None);
        Assert.Equal(1, late.DisposeCount); // no view presents it, and no player holds it
        ((IClaimableSink)callers).TryClaim("a player")!.Dispose(); // still the caller's, and usable
        window.Close();
    }
}
