using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FrameFlow.Examples.Multicast.Dml;

public class App : Application
{
    public override void Initialize()
    {
        StartupClock.Mark("App.Initialize entered");
        AvaloniaXamlLoader.Load(this);
        StartupClock.Mark("App.Initialize: XAML loaded");
        // FFmpeg bootstrap is owned by the FrameFlowPlayer builder
        // (PlayerBuilder.BuildAsync → IFrameFlowBootstrapper.Initialize()),
        // so no manual call is needed here.
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupClock.Mark("App.OnFrameworkInitializationCompleted entered");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();

            // First non-flag arg that points at an existing file is the autoplay path.
            var startupFile = args.FirstOrDefault(a =>
                !a.StartsWith("--", StringComparison.Ordinal) && System.IO.File.Exists(a)
            );

            // --log-file <path> opt-in file sink. Without it the app
            // adds no log provider and every pipeline error is silent
            // beyond what bubbles to the StatusText surface.
            string? logFilePath = null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--log-file")
                {
                    logFilePath = args[i + 1];
                    break;
                }
            }

            // --break-yolo skips Yolov8Detector.CreateAsync entirely
            // and flips pane 2 to Unavailable. Lets us verify that a
            // broken pane-2 bootstrap doesn't take down panes 1 and 3.
            var breakYolo = args.Contains("--break-yolo", StringComparer.Ordinal);

            desktop.MainWindow = new MainWindow
            {
                StartupFilePath = startupFile,
                StartupLogFilePath = logFilePath,
                BreakYolo = breakYolo,
            };
            StartupClock.Mark("MainWindow assigned to ApplicationLifetime");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
