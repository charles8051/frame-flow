using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FrameFlow.Media;

namespace FrameFlow.Examples.ZeroCopyInterop;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Reads <c>&lt;file&gt; [--hw-device d3d12va|d3d11va] [--super-resolution]</c> and shows the
    /// window. The log goes to <c>logs/zero-copy-interop.log</c>.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var flag = Array.IndexOf(args, "--hw-device");
            var device = flag >= 0 && flag + 1 < args.Length ? args[flag + 1].ToLowerInvariant() : null;

            string? logFailure = null;
            var loggerFactory = ExampleLogging.CreateFactory(
                "zero-copy-interop.log",
                onFailure: ex => logFailure = ex.Message
            );
            desktop.Exit += (_, _) => loggerFactory.Dispose();

            desktop.MainWindow = new MainWindow(
                loggerFactory,
                path: args.FirstOrDefault(File.Exists),
                backend: device switch
                {
                    "d3d12va" => HardwareDecodeBackendKind.D3D12Va,
                    "d3d11va" => HardwareDecodeBackendKind.D3D11Va,
                    _ => null,
                },
                superResolution: args.Contains("--super-resolution")
            );
            if (logFailure is not null)
                desktop.MainWindow.Title += $"  [no log file: {logFailure}]";
        }

        base.OnFrameworkInitializationCompleted();
    }
}
