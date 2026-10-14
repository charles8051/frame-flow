using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using TrackedFrame = FrameFlow.Avalonia.Tests.CompositionInteropVideoViewReattachTests.TrackedFrame;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0085 decision 2's assignment of <see cref="CompositionInteropVideoView.Sink"/>: the view
/// first claims the outgoing sink, refuses while a player holds it, and otherwise disposes its own
/// sink or lets go of a caller's, and releases that claim.
/// </summary>
/// <remarks>
/// A player holding the sink is played by a claim taken through <see cref="IClaimableSink"/>, which
/// is what the player builder does at build.
/// </remarks>
public sealed class CompositionInteropVideoViewSinkAssignmentTests
{
    private const string Holder = "a player built for 'clip.mp4'";

    [AvaloniaFact]
    public void AssigningASink_OverOneAPlayerHolds_ThrowsNamingTheHolder_AndChangesNothing()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var held = view.EnsureSink();
        using var player = ((IClaimableSink)held).TryClaim(Holder);
        var replacement = new CompositionInteropVideoSink();

        var error = Assert.Throws<InvalidOperationException>(() => view.Sink = replacement);
        Assert.Throws<InvalidOperationException>(() => view.Sink = null);

        Assert.Contains(Holder, error.Message, StringComparison.Ordinal);
        Assert.Same(held, view.Sink);
        Assert.NotNull(held.FrameArrived);
        Assert.Null(replacement.FrameArrived);
        Assert.Equal(Holder, ((IClaimableSink)held).Claimant);
        Assert.NotSame(VideoSinkDiagnosticsSnapshot.Empty, held.GetDiagnostics());
        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        Assert.True(view.IsWatchingForStalls);
        window.Close();
    }

    [AvaloniaFact]
    public void AssigningASink_OverTheViewsOwnIdleSink_DisposesIt()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var own = view.EnsureSink();
        var replacement = new CompositionInteropVideoSink();

        view.Sink = replacement;

        Assert.Same(replacement, view.Sink);
        Assert.Throws<ObjectDisposedException>(() => ((IClaimableSink)own).TryClaim("a player"));
        Assert.Same(VideoSinkDiagnosticsSnapshot.Empty, own.GetDiagnostics());
        Assert.NotNull(replacement.FrameArrived);
        Assert.NotNull(ElementComposition.GetElementChildVisual(view)); // a replacement only rebinds
        window.Close();
    }

    [AvaloniaFact]
    public async Task AssigningASink_OverACallersIdleSink_LetsItGo_UnclaimedAndKeepingNothing()
    {
        var view = new CompositionInteropVideoView();
        var callers = new CompositionInteropVideoSink();
        view.Sink = callers;
        view.Initialize(new RecordingLoggerFactory());
        var window = Show(view);
        var pending = new TrackedFrame();
        await callers.PresentAsync(pending, CancellationToken.None);
        var replacement = new CompositionInteropVideoSink();

        view.Sink = replacement;

        Assert.Null(((IClaimableSink)callers).Claimant); // the view's own claim is released
        Assert.True(pending.Disposed); // no view presents it, and no player holds it
        Assert.Null(callers.FrameArrived);
        ((IClaimableSink)callers).TryClaim("a player")!.Dispose(); // not disposed: still the caller's
        Assert.NotNull(replacement.FrameArrived);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AssigningASink_OverACallersSinkItsOwnerDisposed_GoesAhead()
    {
        // A disposed sink cannot be claimed, and presents nothing whoever still feeds it.
        var view = new CompositionInteropVideoView();
        var callers = new CompositionInteropVideoSink();
        view.Sink = callers;
        view.Initialize(new RecordingLoggerFactory());
        var window = Show(view);
        await callers.DisposeAsync();
        var replacement = new CompositionInteropVideoSink();

        view.Sink = replacement;

        Assert.Same(replacement, view.Sink);
        Assert.NotNull(replacement.FrameArrived);
        window.Close();
    }

    [AvaloniaFact]
    public void ClearingTheSink_WhileAttached_TakesTheCompositorSideDown()
    {
        // Presenting needs a sink. The last frame no longer stays up.
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        view.EnsureSink();

        view.Sink = null;

        Assert.Null(view.Sink);
        Assert.Null(ElementComposition.GetElementChildVisual(view));
        Assert.False(view.IsWatchingForStalls);

        view.EnsureSink();
        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        Assert.True(view.IsWatchingForStalls);
        window.Close();
    }

    private static Window Show(Control view)
    {
        var window = new Window { Width = 200, Height = 100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
