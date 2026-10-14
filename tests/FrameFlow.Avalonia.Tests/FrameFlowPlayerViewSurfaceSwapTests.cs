using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0085 decision 7 on <see cref="FrameFlowPlayerView.VideoSurface"/>: an in-box surface is
/// swapped out under the player view's own claim on its sink, refused while a player holds it; the
/// default surface stays alive and claimed while it is out; a caller's surface goes back unclaimed;
/// a surface of the app's own type is swapped without a check. And the player view's dispose, which
/// disposes the default surface and nothing else.
/// </summary>
/// <remarks>
/// A player holding a sink is played by a claim taken through <see cref="IClaimableSink"/>, which is
/// what the player builder does at build; a build itself is <see cref="PlaybackController.Create"/>,
/// which claims the sink it is given and needs no media.
/// </remarks>
public sealed class FrameFlowPlayerViewSurfaceSwapTests
{
    private const string Holder = "a player built for 'clip.mp4'";

    [AvaloniaFact]
    public void SwappingOutTheDefaultSurface_WhileAPlayerHoldsItsSink_ThrowsNamingTheHolder_AndChangesNothing()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = view.VideoSurface;
            var sink = SinkOf(surface)!;
            using var player = sink.TryClaim(Holder);
            var next = new FrameFlowVideoView();

            var error = Assert.Throws<InvalidOperationException>(() => view.VideoSurface = next);

            Assert.Contains(Holder, error.Message, StringComparison.Ordinal);
            Assert.Same(surface, view.VideoSurface);
            Assert.True(InTree(surface.Control));
            Assert.False(InTree(next));
            Assert.Equal(Holder, sink.Claimant);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SwappingOutACallersZeroCopySurface_WhileAPlayerHoldsItsSink_Throws_AndChangesNothing()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var callers = new CompositionInteropVideoView();
            var sink = ((IVideoSurface)callers).AttachSink(NullLoggerFactory.Instance);
            view.VideoSurface = callers;
            using var player = ((IClaimableSink)sink).TryClaim(Holder);

            var error = Assert.Throws<InvalidOperationException>(() => view.VideoSurface = new FrameFlowVideoView());

            Assert.Contains(Holder, error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(CompositionInteropVideoView), error.Message, StringComparison.Ordinal);
            Assert.Same(callers, view.VideoSurface);
            Assert.True(InTree(callers));
            Assert.Same(sink, callers.Sink);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnIdleDefaultSurface_SwappedOut_StaysAliveAndClaimed_UntilItIsSwappedBackIn()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = (FrameFlowVideoView)view.VideoSurface;
            var sink = surface.Sink!;

            view.VideoSurface = new FrameFlowVideoView();

            Assert.False(InTree(surface));
            Assert.Same(sink, surface.Sink); // alive, and keeping its sink
            var error = Assert.Throws<InvalidOperationException>(() =>
                PlaybackController.Create(videoSink: ((IVideoSurface)surface).AttachSink(NullLoggerFactory.Instance))
            );
            Assert.Contains(DefaultHolder, error.Message, StringComparison.Ordinal);

            view.VideoSurface = surface;

            Assert.True(InTree(surface));
            Assert.Null(((IClaimableSink)sink).Claimant);
            await using var player = PlaybackController.Create(videoSink: view.AttachSink(NullLoggerFactory.Instance));
            Assert.Same(sink, surface.Sink);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheDefaultSurface_SwappedOutAgain_IsClaimedAgain()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = (FrameFlowVideoView)view.VideoSurface;
            var sink = (IClaimableSink)surface.Sink!;
            view.VideoSurface = new FrameFlowVideoView();
            view.VideoSurface = surface;
            Assert.Null(sink.Claimant);

            view.VideoSurface = new FrameFlowVideoView();

            Assert.Contains(DefaultHolder, sink.Claimant, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() =>
                PlaybackController.Create(videoSink: ((IVideoSurface)surface).AttachSink(NullLoggerFactory.Instance))
            );

            view.VideoSurface = surface;
            Assert.Null(sink.Claimant);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ADefaultSurfaceTheAppDisposed_IsSwappedOut_WithNothingToClaim()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = (FrameFlowVideoView)view.VideoSurface;
            await surface.DisposeAsync();
            var next = new FrameFlowVideoView();

            view.VideoSurface = next;

            Assert.Same(next, view.VideoSurface);
            Assert.False(InTree(surface));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ADefaultSurfaceWithNoSinkYet_GetsOne_AndIsClaimed_AsItIsSwappedOut()
    {
        // Never in the tree, so the default surface has made no sink. The player view's promise
        // still holds: a build written against it later fails at its claim.
        var view = new FrameFlowPlayerView();
        var surface = (FrameFlowVideoView)view.VideoSurface;
        Assert.Null(surface.Sink);

        view.VideoSurface = new FrameFlowVideoView();

        Assert.NotNull(surface.Sink);
        Assert.Throws<InvalidOperationException>(() =>
            PlaybackController.Create(videoSink: ((IVideoSurface)surface).AttachSink(NullLoggerFactory.Instance))
        );
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIdleCallersSurface_SwappedOut_GoesBackUnclaimed_WithItsSink(bool zeroCopy)
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            IVideoSurface callers = zeroCopy ? new CompositionInteropVideoView() : new FrameFlowVideoView();
            var sink = (IClaimableSink)callers.AttachSink(NullLoggerFactory.Instance);
            view.VideoSurface = callers;

            view.VideoSurface = new FrameFlowVideoView();

            Assert.False(InTree(callers.Control));
            Assert.Same(sink, SinkOf(callers)); // kept, not disposed
            Assert.Null(sink.Claimant);
            sink.TryClaim("a player")!.Dispose(); // still the caller's, and usable
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WhileACallersSurfaceLeavesTheTree_ThePlayerViewHoldsItsSink()
    {
        // No build can claim the sink while the swap runs: the surface's detach runs inside it, and
        // the seam reads the claim at that moment.
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var callers = new FrameFlowVideoView();
            view.VideoSurface = callers;
            var sink = (IClaimableSink)callers.Sink!;
            string? holder = null;
            callers.TransitionPublished = () => holder = sink.Claimant;

            view.VideoSurface = new FrameFlowVideoView();
            callers.TransitionPublished = null;

            Assert.Equal("a FrameFlowPlayerView swapping its surface out", holder);
            Assert.Null(sink.Claimant);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ASwapWhoseIncomingSurfaceThrowsOnAttach_StillSettlesTheClaim()
    {
        // The swap's claim is settled whatever the surfaces' own attach does: the default surface,
        // out of the tree, stays claimed; a caller's surface goes back unclaimed.
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = (FrameFlowVideoView)view.VideoSurface;
            var defaultSink = (IClaimableSink)surface.Sink!;

            var error = Assert.Throws<InvalidOperationException>(() => view.VideoSurface = new ThrowingSurface());
            Assert.Equal(ThrowsOnAttach.Fault, error.Message);
            Assert.Contains(DefaultHolder, defaultSink.Claimant, StringComparison.Ordinal);

            view.VideoSurface = surface;
            Assert.Null(defaultSink.Claimant);

            var callers = new FrameFlowVideoView();
            view.VideoSurface = callers;
            var callersSink = (IClaimableSink)callers.Sink!;

            Assert.Throws<InvalidOperationException>(() => view.VideoSurface = new ThrowingSurface());
            Assert.Null(callersSink.Claimant);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ACallersSurfaceWithNoSink_IsSwappedOut_AndNothingIsMadeForIt()
    {
        // Out of the tree, so a surface swapped in makes no sink of its own.
        var view = new FrameFlowPlayerView();
        var callers = new FrameFlowVideoView();
        view.VideoSurface = callers;

        var next = new FrameFlowVideoView();
        view.VideoSurface = next;

        Assert.Same(next, view.VideoSurface);
        Assert.Null(callers.Sink);
    }

    [AvaloniaFact]
    public void ASurfaceOfTheAppsOwnType_IsSwappedOutWithoutACheck()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var own = new OwnSurface();
            view.VideoSurface = own;
            // A player holds its sink, which the player view cannot see.
            using var player = ((IClaimableSink)own.Sink).TryClaim(Holder);

            view.VideoSurface = new FrameFlowVideoView();

            Assert.False(InTree(own.Control));
            Assert.Equal(Holder, ((IClaimableSink)own.Sink).Claimant);
            Assert.Equal(0, own.AttachSinkCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DisposeAsync_DisposesTheSwappedOutDefaultSurface_ReleasingItsClaim_AndNotTheCallersSurface()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = (FrameFlowVideoView)view.VideoSurface;
            var sink = (IClaimableSink)surface.Sink!;
            var callers = new FrameFlowVideoView();
            view.VideoSurface = callers;
            var callersSink = callers.Sink!;

            await view.DisposeAsync();

            Assert.Null(surface.Sink);
            Assert.Throws<ObjectDisposedException>(() => sink.TryClaim("a player"));
            Assert.Null(sink.Claimant); // the player view's claim is released
            Assert.Same(callers, view.VideoSurface);
            Assert.Same(callersSink, callers.Sink);
            ((IClaimableSink)callersSink).TryClaim("a player")!.Dispose(); // the caller's, untouched

            await view.DisposeAsync(); // a second call does nothing
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WhileTheSwappedOutDefaultSurfaceIsDisposed_ThePlayerViewStillHoldsItsSink()
    {
        // Released first, the claim would leave a moment in which a build on another thread could
        // take a sink about to be disposed. The surface's dispose runs the seam as it moves on.
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = (FrameFlowVideoView)view.VideoSurface;
            var sink = (IClaimableSink)surface.Sink!;
            view.VideoSurface = new FrameFlowVideoView();
            string? holder = null;
            surface.TransitionPublished = () => holder = sink.Claimant;

            await view.DisposeAsync();

            Assert.Contains(DefaultHolder, holder, StringComparison.Ordinal);
            Assert.Null(sink.Claimant);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AfterDisposeAsync_AssigningAnotherSurface_Throws_AndChangesNothing()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = view.VideoSurface;
            await view.DisposeAsync();
            var next = new FrameFlowVideoView();

            Assert.Throws<ObjectDisposedException>(() => view.VideoSurface = next);

            Assert.Same(surface, view.VideoSurface);
            Assert.False(InTree(next));
            view.VideoSurface = surface; // the surface it hosts: nothing to do
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DisposeAsync_DisposesTheHostedDefaultSurface()
    {
        var view = new FrameFlowPlayerView();
        var window = Show(view);
        try
        {
            var surface = (FrameFlowVideoView)view.VideoSurface;
            var sink = (IClaimableSink)surface.Sink!;

            await view.DisposeAsync();

            Assert.Null(surface.Sink);
            Assert.Throws<ObjectDisposedException>(() => sink.TryClaim("a player"));
            Assert.Throws<ObjectDisposedException>(() => view.AttachSink(NullLoggerFactory.Instance));
        }
        finally
        {
            window.Close();
        }
    }

    // How the player view's claim on its swapped-out default surface names it.
    private const string DefaultHolder = "FrameFlowPlayerView that swapped out its default surface";

    private static IClaimableSink? SinkOf(IVideoSurface surface) => ((IInBoxVideoSurface)surface).Sink;

    private static bool InTree(Control control) => TopLevel.GetTopLevel(control) is not null;

    // The theme goes in first: without one the window has no template to present its content
    // through, and the player view never joins the visual tree.
    private static Window Show(Control control)
    {
        var window = new Window { Width = 800, Height = 450 };
        window.Styles.Add(new FluentTheme());
        window.Content = control;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(window, TopLevel.GetTopLevel(control));
        return window;
    }

    /// <summary>A surface of the app's own type whose control throws as it joins the tree.</summary>
    private sealed class ThrowingSurface : IVideoSurface
    {
        public Control Control { get; } = new ThrowsOnAttach();

        public IVideoSink AttachSink(ILoggerFactory loggerFactory) => throw new NotSupportedException();
    }

    private sealed class ThrowsOnAttach : Border
    {
        public const string Fault = "The surface failed to join the tree.";

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            throw new InvalidOperationException(Fault);
        }
    }

    /// <summary>A surface of the app's own type: it exposes no sink to the player view.</summary>
    private sealed class OwnSurface : IVideoSurface
    {
        public HeadlessVideoSink Sink { get; } = new();

        public Control Control { get; } = new Border();

        public int AttachSinkCalls { get; private set; }

        public IVideoSink AttachSink(ILoggerFactory loggerFactory)
        {
            AttachSinkCalls++;
            return Sink;
        }
    }
}
