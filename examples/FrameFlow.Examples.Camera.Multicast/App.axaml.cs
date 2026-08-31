using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FrameFlow.Examples.Camera.Multicast;

public class App : Application
{
    public override void Initialize()
    {
        StartupClock.Mark("App.Initialize entered");
        AvaloniaXamlLoader.Load(this);
        StartupClock.Mark("App.Initialize: XAML loaded");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupClock.Mark("App.OnFrameworkInitializationCompleted entered");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();

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

            // --auto-pick [index] selects an enumerated camera by index
            // (default 0) as soon as enumeration completes, without
            // waiting for a UI click. Pairs with --exit-after for
            // autonomous diagnostic runs: the agent launches the app,
            // lets it capture frames into the log for N seconds, then
            // has it self-terminate.
            var autoPick = false;
            var autoPickIndex = 0;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--auto-pick")
                {
                    autoPick = true;
                    if (i + 1 < args.Length
                        && int.TryParse(args[i + 1], out var idx))
                        autoPickIndex = idx;
                    break;
                }
            }

            // --exit-after <seconds> shuts the window cleanly after the
            // given delay. Useful for non-interactive runs: the file
            // logger flushes on dispose, so the log is complete by the
            // time the process exits. 0 (or absent) means "stay open
            // until the user closes the window."
            double exitAfterSeconds = 0;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--exit-after"
                    && double.TryParse(
                        args[i + 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    exitAfterSeconds = parsed;
                    break;
                }
            }

            desktop.MainWindow = new MainWindow
            {
                StartupLogFilePath = logFilePath,
                BreakYolo = breakYolo,
                AutoPickFirstCamera = autoPick,
                AutoPickIndex = autoPickIndex,
                ExitAfterSeconds = exitAfterSeconds,
            };
            StartupClock.Mark("MainWindow assigned to ApplicationLifetime");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
