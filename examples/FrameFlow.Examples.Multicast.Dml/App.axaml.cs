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

            // --break-yolo skips Yolov8Detector.CreateAsync entirely
            // and flips pane 2 to Unavailable. Lets us verify that a
            // broken pane-2 bootstrap doesn't take down panes 1 and 3.
            var breakYolo = args.Contains("--break-yolo", StringComparer.Ordinal);

            // --exit-after <seconds> closes the window on a timer and prints the
            // per-stage timing report, so a measurement run needs no operator.
            // It also turns on the decoder's readback timing, which is off by
            // default so the normal path pays nothing for it.
            int? exitAfter = null;
            var exitAfterIndex = Array.IndexOf(args, "--exit-after");
            if (
                exitAfterIndex >= 0
                && exitAfterIndex + 1 < args.Length
                && int.TryParse(args[exitAfterIndex + 1], out var seconds)
            )
            {
                exitAfter = seconds;
                // Reset first: the collector is process-wide, so a session that
                // only enables it reports whatever a previous decoder left.
                FrameFlow.Decoding.Diagnostics.DecodeStageMetrics.Reset();
                FrameFlow.Decoding.Diagnostics.DecodeStageMetrics.Enabled = true;
            }

            desktop.MainWindow = new MainWindow
            {
                StartupFilePath = startupFile,
                BreakYolo = breakYolo,
                ExitAfterSeconds = exitAfter,
            };
            StartupClock.Mark("MainWindow assigned to ApplicationLifetime");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
