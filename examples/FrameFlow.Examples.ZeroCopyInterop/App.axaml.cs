using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FrameFlow.Examples.ZeroCopyInterop;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Reads <c>&lt;file&gt; [--hw-device d3d12va]</c> and shows the window. The log goes to
    /// <c>logs/zero-copy-interop.log</c>.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Anything else leaves the path null, and the window shows the usage line.
            var (path, d3d12Device) = desktop.Args switch
            {
                [var file] when !file.StartsWith('-') => (file, false),
                [var file, "--hw-device", "d3d12va"] when !file.StartsWith('-') => (file, true),
                _ => ((string?)null, false),
            };

            string? logFailure = null;
            var loggerFactory = ExampleLogging.CreateFactory(
                "zero-copy-interop.log",
                onFailure: ex => logFailure = ex.Message
            );
            desktop.Exit += (_, _) => loggerFactory.Dispose();

            desktop.MainWindow = new MainWindow(loggerFactory, path, d3d12Device);
            if (logFailure is not null)
                desktop.MainWindow.Title += $"  [no log file: {logFailure}]";
        }

        base.OnFrameworkInitializationCompleted();
    }
}
