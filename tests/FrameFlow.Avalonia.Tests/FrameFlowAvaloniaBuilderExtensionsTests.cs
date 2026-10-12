using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// <see cref="FrameFlowAvaloniaBuilderExtensions"/>: both <c>WithAvaloniaVideoView</c> overloads
/// hand the builder the view's own sink, made before the view is on screen (#24), and both
/// <c>WithVideoSurface</c> overloads hand it the sink any surface returns from <c>AttachSink</c>
/// with the factory the caller gave (#613).
/// </summary>
public sealed class FrameFlowAvaloniaBuilderExtensionsTests
{
    [AvaloniaFact]
    public void OnAPlayerBuilder_TheBuilderGetsTheSinkTheViewNowHolds()
    {
        var view = new FrameFlowVideoView();
        var builder = Recorder.Create<IPlayerBuilder>();

        Assert.Same(builder, builder.WithAvaloniaVideoView(view));

        Assert.NotNull(view.Sink);
        AssertOnlyWithVideoSink(builder, view.Sink);
    }

    [AvaloniaFact]
    public void OnAPassBuilder_TheBuilderGetsTheSinkTheViewNowHolds()
    {
        var view = new FrameFlowVideoView();
        var builder = Recorder.Create<IPassBuilder>();

        Assert.Same(builder, builder.WithAvaloniaVideoView(view));

        Assert.NotNull(view.Sink);
        AssertOnlyWithVideoSink(builder, view.Sink);
    }

    [AvaloniaFact]
    public void AViewWithAnAssignedSink_PassesThatSink()
    {
        var sink = new AvaloniaVideoSink();
        var view = new FrameFlowVideoView { Sink = sink };
        var builder = Recorder.Create<IPlayerBuilder>();

        builder.WithAvaloniaVideoView(view);

        AssertOnlyWithVideoSink(builder, sink);
    }

    [AvaloniaFact]
    public void ANullBuilderOrView_IsRejected()
    {
        var view = new FrameFlowVideoView();

        Assert.Throws<ArgumentNullException>(() => ((IPlayerBuilder)null!).WithAvaloniaVideoView(view));
        Assert.Throws<ArgumentNullException>(() => ((IPassBuilder)null!).WithAvaloniaVideoView(view));
        Assert.Throws<ArgumentNullException>(() => Recorder.Create<IPlayerBuilder>().WithAvaloniaVideoView(null!));
        Assert.Throws<ArgumentNullException>(() => Recorder.Create<IPassBuilder>().WithAvaloniaVideoView(null!));
    }

    [AvaloniaFact]
    public void WithVideoSurface_OnAPlayerBuilder_AttachesTheSurfaceWithTheFactoryAndPassesItsSink()
    {
        var surface = new RecordingSurface();
        var builder = Recorder.Create<IPlayerBuilder>();
        var log = new RecordingLoggerFactory();

        Assert.Same(builder, builder.WithVideoSurface(surface, log));

        Assert.Same(log, Assert.Single(surface.Attached));
        AssertOnlyWithVideoSink(builder, surface.Sink);
    }

    [AvaloniaFact]
    public void WithVideoSurface_OnAPassBuilder_AttachesTheSurfaceWithTheFactoryAndPassesItsSink()
    {
        var surface = new RecordingSurface();
        var builder = Recorder.Create<IPassBuilder>();
        var log = new RecordingLoggerFactory();

        Assert.Same(builder, builder.WithVideoSurface(surface, log));

        Assert.Same(log, Assert.Single(surface.Attached));
        AssertOnlyWithVideoSink(builder, surface.Sink);
    }

    [AvaloniaFact]
    public async Task WithVideoSurface_OnAViewAlreadyOnScreen_TheSinkItPassesLogsThroughTheFactory()
    {
        // The usual order: the window is up, and the player is built from OnLoaded (#630).
        var view = new FrameFlowVideoView();
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var builder = Recorder.Create<IPlayerBuilder>();
        var log = new RecordingLoggerFactory();

        builder.WithVideoSurface(view, log);
        await view.Sink!.OnFormatChangedAsync(new VideoFormatInfo(32, 16, PixelFormat.Bgra32), CancellationToken.None);

        AssertOnlyWithVideoSink(builder, view.Sink);
        Assert.Contains(log.MessagesFrom<AvaloniaVideoSink>(), m => m.Contains("Video format changed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void WithVideoSurface_ANullArgument_IsRejected()
    {
        var surface = new RecordingSurface();
        var log = NullLoggerFactory.Instance;

        Assert.Throws<ArgumentNullException>(() => ((IPlayerBuilder)null!).WithVideoSurface(surface, log));
        Assert.Throws<ArgumentNullException>(() => ((IPassBuilder)null!).WithVideoSurface(surface, log));
        Assert.Throws<ArgumentNullException>(() => Recorder.Create<IPlayerBuilder>().WithVideoSurface(null!, log));
        Assert.Throws<ArgumentNullException>(() => Recorder.Create<IPassBuilder>().WithVideoSurface(null!, log));
        Assert.Throws<ArgumentNullException>(() => Recorder.Create<IPlayerBuilder>().WithVideoSurface(surface, null!));
        Assert.Throws<ArgumentNullException>(() => Recorder.Create<IPassBuilder>().WithVideoSurface(surface, null!));
        Assert.Empty(surface.Attached);
    }

    private static void AssertOnlyWithVideoSink(object builder, IVideoSink expected)
    {
        var (method, args) = Assert.Single(Recorder.CallsOn(builder));
        Assert.Equal("WithVideoSink", method.Name);
        Assert.Same(expected, Assert.Single(args));
    }

    /// <summary>A surface that records the factories it is attached with.</summary>
    private sealed class RecordingSurface : IVideoSurface
    {
        public List<ILoggerFactory> Attached { get; } = [];

        public IVideoSink Sink { get; } = new AvaloniaVideoSink();

        public Control Control { get; } = new Border();

        public IVideoSink AttachSink(ILoggerFactory loggerFactory)
        {
            Attached.Add(loggerFactory);
            return Sink;
        }
    }

    /// <summary>
    /// Records every call made on the interface it stands in for. A call that returns the
    /// interface returns the proxy, as a fluent builder does; anything else is not supported.
    /// </summary>
    public class Recorder : DispatchProxy
    {
        private readonly List<(MethodInfo Method, object?[] Args)> _calls = [];

        public static T Create<T>()
            where T : class => DispatchProxy.Create<T, Recorder>();

        public static IReadOnlyList<(MethodInfo Method, object?[] Args)> CallsOn(object proxy) =>
            ((Recorder)proxy)._calls;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            _calls.Add((targetMethod, args ?? []));
            return targetMethod.ReturnType.IsInstanceOfType(this)
                ? this
                : throw new NotSupportedException(targetMethod.Name);
        }
    }
}
