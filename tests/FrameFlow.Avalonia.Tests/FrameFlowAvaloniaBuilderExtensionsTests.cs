using System.Reflection;
using Avalonia.Headless.XUnit;
using FrameFlow.Player;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// <see cref="FrameFlowAvaloniaBuilderExtensions"/>: both <c>WithAvaloniaVideoView</c> overloads
/// hand the builder the view's own sink, made before the view is on screen (#24).
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

    private static void AssertOnlyWithVideoSink(object builder, AvaloniaVideoSink expected)
    {
        var (method, args) = Assert.Single(Recorder.CallsOn(builder));
        Assert.Equal("WithVideoSink", method.Name);
        Assert.Same(expected, Assert.Single(args));
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
