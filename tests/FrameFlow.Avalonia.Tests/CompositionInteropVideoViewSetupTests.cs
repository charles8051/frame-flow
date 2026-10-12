using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Player;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #613 on the Windows zero-copy view: the sink <see cref="CompositionInteropVideoView.EnsureSink"/>
/// hands out is one the view will present, with or without <c>Initialize</c>.
/// </summary>
/// <remarks>
/// <para>
/// The view sets up its compositor surface and GPU interop only once <c>Initialize</c> has run,
/// and holds every frame until they exist. <c>EnsureSink</c> did not run it, so the
/// <c>FrameFlowVideoView</c> pattern, <c>WithVideoSink(view.EnsureSink())</c>, built, played and
/// showed a black window with nothing in the log.
/// </para>
/// <para>
/// Here rather than in FrameFlow.Avalonia.Windows.Tests because the view is a control and needs
/// an Avalonia app; this project has the headless one. Setup builds the surface visual on any
/// platform. Only the GPU interop and the presents need Direct3D, and none of these tests
/// present a frame.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoViewSetupTests
{
    [AvaloniaFact]
    public void EnsureSinkWithoutInitialize_SetsUpTheSurfaceOnceTheViewIsOnScreen()
    {
        var view = new CompositionInteropVideoView();

        view.EnsureSink();
        var window = Show(view);

        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        window.Close();
    }

    [AvaloniaFact]
    public void EnsureSinkOnAViewAlreadyOnScreen_SetsUpTheSurface()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        Assert.Null(ElementComposition.GetElementChildVisual(view)); // nothing asked for a sink yet

        view.EnsureSink();

        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        window.Close();
    }

    [AvaloniaFact]
    public void WithVideoSurface_PassesTheViewsSinkAndReadiesTheView()
    {
        var view = new CompositionInteropVideoView();
        var window = Show(view);
        var builder = FrameFlowAvaloniaBuilderExtensionsTests.Recorder.Create<IPlayerBuilder>();

        builder.WithVideoSurface(view, new RecordingLoggerFactory());

        var (method, args) = Assert.Single(FrameFlowAvaloniaBuilderExtensionsTests.Recorder.CallsOn(builder));
        Assert.Equal("WithVideoSink", method.Name);
        Assert.Same(view.Sink, Assert.Single(args));
        Assert.NotNull(ElementComposition.GetElementChildVisual(view));
        window.Close();
    }

    [AvaloniaFact]
    public async Task InitializeAfterEnsureSink_MovesTheSinksLoggingToTheNewFactory()
    {
        // EnsureSink now readies the view without a logger, so the sink exists before the
        // factory arrives. The factory still has to reach it, as on the CPU view (#630).
        var view = new CompositionInteropVideoView();
        var sink = view.EnsureSink();
        var log = new RecordingLoggerFactory();

        view.Initialize(log);
        sink.FramePresented += (_, _) => throw new InvalidOperationException("overlay failed");
        sink.RaiseFramePresented(TimeSpan.FromMilliseconds(16));

        Assert.Contains(log.MessagesFrom<CompositionInteropVideoSink>(), m => m.Contains("FramePresented handler threw", StringComparison.Ordinal));
        await view.DisposeAsync();
    }

    private static Window Show(Control view)
    {
        var window = new Window { Width = 200, Height = 100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
