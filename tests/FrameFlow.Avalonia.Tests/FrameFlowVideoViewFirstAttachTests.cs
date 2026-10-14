using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Media;
using static FrameFlow.Avalonia.Tests.FrameFlowVideoViewReattachTests;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0085 decision 3 on <see cref="FrameFlowVideoView"/>: a sink bound before the view first
/// joins the visual tree keeps its newest frame for a player that holds its claim and nothing
/// without one, and the view copies, presents and counts nothing, and raises no
/// <c>FramePresented</c>, until it is attached. The first attach presents the kept frame.
/// </summary>
/// <remarks>
/// The view used to copy and count frames from the moment its sink was bound, and raise
/// <c>FramePresented</c> for a view that was not on screen. <c>WithAvaloniaVideoView</c> binds the
/// sink before the view attaches, so a player built in a window's constructor fed it from then on.
/// An item with one frame (#287) given in that window has no frame after it, so the attach has to
/// present the one the sink kept.
/// </remarks>
public sealed class FrameFlowVideoViewFirstAttachTests
{
    [AvaloniaFact]
    public async Task BeforeTheFirstAttach_NothingIsCopiedOrRaised_AndTheAttachPresentsTheKeptFrame()
    {
        var view = new FrameFlowVideoView();
        var sink = view.EnsureSink();
        using var player = ((IClaimableSink)sink).TryClaim("a player");
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);
        var first = new TrackedFrame(Pts(1));
        var second = new TrackedFrame(Pts(2));

        await sink.PresentAsync(first, CancellationToken.None);
        await sink.PresentAsync(second, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, view.RenderedFrameCount);
        Assert.Empty(presented);
        Assert.Equal(1, first.DisposeCount); // replaced while the view did not present
        Assert.Equal(0, second.DisposeCount); // kept
        Assert.Equal(0, sink.DroppedFrameCount);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);

        var window = ShowWindow(view);

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal([Pts(2)], presented);
        Assert.Equal(1, second.DisposeCount); // copied, then released
        window.Close();
    }

    [AvaloniaFact]
    public async Task BeforeTheFirstAttach_AnUnclaimedSinkKeepsNothing()
    {
        // A graph built by hand takes no claim, so nothing would say when to let a kept frame go.
        var view = new FrameFlowVideoView();
        var sink = view.EnsureSink();
        var frame = new TrackedFrame(Pts(1));

        await sink.PresentAsync(frame, CancellationToken.None);

        Assert.Equal(1, frame.DisposeCount);
        Assert.False(sink.HasPendingFrame);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);

        var window = ShowWindow(view);
        Assert.Equal(0, view.RenderedFrameCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnItemWithOneFrame_GivenBeforeTheFirstAttach_ShowsOnceTheViewIsAttached()
    {
        // #287's shape: one frame, at a size no bitmap has been allocated for, and nothing after it.
        var view = new FrameFlowVideoView();
        var sink = view.EnsureSink();
        using var player = ((IClaimableSink)sink).TryClaim("a player built for 'still.png'");
        await sink.PresentAsync(new TrackedFrame(Pts(1), width: 64, height: 48), CancellationToken.None);

        var window = ShowWindow(view);

        Assert.Equal(1, view.RenderedFrameCount);
        Assert.Equal(Pts(1), sink.GetDiagnostics().LastPresentedPresentationTime);
        Assert.Equal(0, sink.DroppedFrameCount);
        Assert.Equal(new global::Avalonia.PixelSize(64, 48), view.Picture?.PixelSize);
        window.Close();
    }
}
