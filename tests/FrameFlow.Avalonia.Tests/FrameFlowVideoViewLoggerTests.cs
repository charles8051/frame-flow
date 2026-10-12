using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #630: a logger factory given to a <see cref="FrameFlowVideoView"/> reaches the sink it
/// owns whenever it is given, not only before the view joins the visual tree.
/// </summary>
/// <remarks>
/// The view makes its sink the moment it attaches, with whatever factory it holds then, which is
/// the null one. A caller builds a player from <c>OnLoaded</c> or a click handler, after that, and
/// wires the logger there through <c>AttachSink</c> or <c>LoggerFactory</c>. The sink used to keep
/// its null logger, so its format changes, present gaps, drops and lifetime lines never reached
/// the app's log. Each test here gives the factory after the sink exists and asks the sink to log.
/// </remarks>
public sealed class FrameFlowVideoViewLoggerTests
{
    private static readonly VideoFormatInfo Format = new(32, 16, PixelFormat.Bgra32);

    [AvaloniaFact]
    public async Task AFactoryAssignedAfterTheViewAttaches_ReachesTheSinkItMadeOnAttach()
    {
        var view = new FrameFlowVideoView();
        Attach(view);
        var sink = view.Sink!;
        var log = new RecordingLoggerFactory();

        view.LoggerFactory = log;
        await sink.OnFormatChangedAsync(Format, CancellationToken.None);

        Assert.Contains(log.MessagesFrom<AvaloniaVideoSink>(), m => m.Contains("Video format changed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task AttachSinkAfterTheViewAttaches_WiresTheLoggerIntoTheSinkItReturns()
    {
        var view = new FrameFlowVideoView();
        Attach(view);
        var log = new RecordingLoggerFactory();

        var sink = ((IVideoSurface)view).AttachSink(log);
        await sink.OnFormatChangedAsync(Format, CancellationToken.None);

        Assert.Same(view.Sink, sink);
        Assert.Contains(log.MessagesFrom<AvaloniaVideoSink>(), m => m.Contains("Video format changed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task APlayerViewWiredAfterItAttaches_LogsFromItsSink()
    {
        var playerView = new FrameFlowPlayerView();
        Attach(playerView);
        var log = new RecordingLoggerFactory();

        var sink = playerView.AttachSink(log);
        await sink.OnFormatChangedAsync(Format, CancellationToken.None);

        Assert.Contains(log.MessagesFrom<AvaloniaVideoSink>(), m => m.Contains("Video format changed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task AFactoryAssignedAfterEnsureSink_ReachesTheSink()
    {
        // WithAvaloniaVideoView calls EnsureSink, so a factory assigned after the builder chain
        // arrives after the sink exists even when the view is not on screen yet.
        var view = new FrameFlowVideoView();
        var sink = view.EnsureSink();
        var log = new RecordingLoggerFactory();

        view.LoggerFactory = log;
        await sink.OnFormatChangedAsync(Format, CancellationToken.None);

        Assert.Contains(log.MessagesFrom<AvaloniaVideoSink>(), m => m.Contains("Video format changed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task ALaterFactory_TakesOverFromTheEarlierOne()
    {
        var view = new FrameFlowVideoView();
        Attach(view);
        var first = new RecordingLoggerFactory();
        var second = new RecordingLoggerFactory();

        var sink = ((IVideoSurface)view).AttachSink(first);
        ((IVideoSurface)view).AttachSink(second);
        await sink.OnFormatChangedAsync(Format, CancellationToken.None);

        Assert.Empty(first.MessagesFrom<AvaloniaVideoSink>());
        Assert.Contains(second.MessagesFrom<AvaloniaVideoSink>(), m => m.Contains("Video format changed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task AnAssignedSink_KeepsTheLoggerItsOwnerGaveIt()
    {
        var owners = new RecordingLoggerFactory();
        var sink = new AvaloniaVideoSink(owners.CreateLogger<AvaloniaVideoSink>());
        var view = new FrameFlowVideoView { Sink = sink };
        Attach(view);
        var log = new RecordingLoggerFactory();

        Assert.Same(sink, ((IVideoSurface)view).AttachSink(log));
        await sink.OnFormatChangedAsync(Format, CancellationToken.None);

        Assert.Empty(log.MessagesFrom<AvaloniaVideoSink>());
        Assert.Contains(owners.MessagesFrom<AvaloniaVideoSink>(), m => m.Contains("Video format changed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task AViewBuiltWithoutALogger_LogsItsOwnFailuresThroughTheFactory()
    {
        // LoggerFactory is documented as the view's logger too when the constructor had none.
        var view = new FrameFlowVideoView();
        Attach(view);
        var log = new RecordingLoggerFactory();
        var sink = ((IVideoSurface)view).AttachSink(log);

        await sink.PresentAsync(new UnreadableFrame(), CancellationToken.None);

        Assert.Contains(log.MessagesFrom<FrameFlowVideoView>(), m => m.Contains("Failed to copy", StringComparison.Ordinal));
        Assert.Equal(1, view.Sink!.DroppedFrameCount);
    }

    private static void Attach(Control control)
    {
        var window = new Window { Width = 200, Height = 100, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A frame whose pixels cannot be read, so the view's copy fails and logs.</summary>
    private sealed class UnreadableFrame : IVideoFrame
    {
        public int Width => 32;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() =>
            throw new NotSupportedException("One-shot frame: ref counting is not supported.");

        public void Dispose() { }

        public CpuFrameData? AsCpu() => throw new InvalidOperationException("pixel access failed");

        public CpuFrameData ToCpu() => throw new InvalidOperationException("pixel access failed");
    }
}
