using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FrameFlow.Examples.LiveCaptioning;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();

            // Pick the first path-shaped arg as the startup file. Anything
            // beginning with "--" is treated as a flag.
            var startupFile = args.FirstOrDefault(a =>
                !a.StartsWith("--", StringComparison.Ordinal) && System.IO.File.Exists(a)
            );

            // --log-file <path> enables an opt-in file sink alongside
            // the in-window TextBox log. Whisper + YOLO + caption pump
            // chatter all goes through the framework logger; the file
            // sink gives a post-mortem view when something's wrong.
            string? logFilePath = null;
            var exitAfter = 0;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--log-file")
                    logFilePath = args[i + 1];
                else if (args[i] == "--exit-after" && int.TryParse(args[i + 1], out var secs))
                    exitAfter = secs;
            }

            // --gpu routes the display branch through the Windows zero-copy
            // composition-interop presenter (one D3D11VA decode → AddRef fork:
            // GPU frame to the display, CPU readback to YOLO). Windows-only.
            var useGpu = args.Contains("--gpu", StringComparer.Ordinal);

            desktop.MainWindow = new MainWindow
            {
                StartupFilePath = startupFile,
                StartupLogFilePath = logFilePath,
                UseGpu = useGpu,
                ExitAfterSeconds = exitAfter,
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
