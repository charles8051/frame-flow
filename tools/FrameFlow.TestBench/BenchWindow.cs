using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using FrameFlow.Avalonia;
using FrameFlow.Media;

namespace FrameFlow.TestBench;

/// <summary>
/// The Avalonia application, for the runs that need a window.
/// </summary>
/// <remarks>
/// Code-only, with no <c>App.axaml</c>. The bench has no chrome, no resources and no
/// styles of its own — a XAML file would carry a theme reference and nothing else.
/// </remarks>
internal sealed class BenchApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        // The window is created by the host rather than here: it needs the options and
        // has to hand its surface back before the pipeline is built.
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// A window that is the video surface and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// No transport bar, no seek bar, no chrome. The bench is driven from the console, and
/// a control that could also drive it would make a transcript an incomplete record of
/// what happened to the session.
/// </para>
/// <para>
/// The surface is <see cref="IVideoSurface"/> either way, so the CPU and compositor
/// presenters differ here only in which one is constructed.
/// </para>
/// </remarks>
internal sealed class BenchWindow : Window
{
    private readonly Panel _host;

    internal IVideoSurface Surface { get; }

    internal BenchWindow(PresenterSelection presenter)
    {
        Title = $"FrameFlow test bench — {presenter}";
        Width = 960;
        Height = 540;
        Background = Brushes.Black;

        Surface = CreateSurface(presenter.Resolved);
        _host = new Panel();
        _host.Children.Add(Surface.Control);
        Content = _host;
    }

    private static IVideoSurface CreateSurface(PresenterKind kind) =>
        kind switch
        {
            // Constructed only after PresenterSelection.Resolve has ruled out the
            // non-Windows case, so this never reaches Direct3D off Windows.
            PresenterKind.Gpu => new global::FrameFlow.Avalonia.Windows.CompositionInteropVideoView(),
            PresenterKind.Cpu => new FrameFlowVideoView(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Headless runs do not open a window."
            ),
        };
}
