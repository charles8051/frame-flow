using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FrameFlow.Native;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.ZeroCopyInterop;

public class App : Application
{
    private bool _bootstrapOk;
    private string? _bootstrapMessage;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Bootstrap FFmpeg before any playback can happen (idempotent; cached).
        var bootstrapper = new FrameFlowBootstrapper(new FrameFlowNativeOptions());
        var result = bootstrapper.Initialize();
        _bootstrapOk = result.IsSuccess;
        _bootstrapMessage = result.Message;
        if (!result.IsSuccess)
            System.Console.Error.WriteLine($"FFmpeg bootstrap failed: {result.Message}");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? System.Array.Empty<string>();
            var startupFile = args.FirstOrDefault(a =>
                !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)
            );
            var exitAfter = int.TryParse(GetArg(args, "--exit-after"), out var s) ? s : 0;
            var hwMode = GetArg(args, "--hw-mode");
            var fullscreen = args.Contains("--fullscreen");
            var soak = args.Contains("--soak") ? ParseSoak(args) : null;

            // Build the logger here (before the window shows) so the bootstrap
            // result and any window-creation problems are captured even if the
            // GPU window never renders (headless / no-desktop runs).
            string? logFailure = null;
            var loggerFactory = ExampleLogging.CreateFactory(
                "zero-copy-interop.log",
                onFailure: ex => logFailure = ex.Message
            );
            loggerFactory
                .CreateLogger<App>()
                .LogInformation(
                    "Zero-copy spike init. FFmpeg ok={Ok} ({Msg}); file={File}; exitAfter={N}s.",
                    _bootstrapOk,
                    _bootstrapMessage,
                    startupFile ?? "(none)",
                    exitAfter
                );

            desktop.MainWindow = new MainWindow(loggerFactory)
            {
                StartupFilePath = startupFile,
                ExitAfterSeconds = exitAfter,
                StartupHwMode = hwMode,
                StartupFullscreen = fullscreen,
                Soak = soak,
            };
            if (logFailure is not null)
                desktop.MainWindow.Title += $"  [no log file: {logFailure}]";
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Reads the soak flags: <c>--second &lt;path&gt;</c> for the right pane's clip,
    /// <c>--sample &lt;seconds&gt;</c> for the sampling interval (60 by default),
    /// <c>--csv &lt;name|path&gt;</c> for the samples (a bare name lands beside the logs), and
    /// <c>--label &lt;name&gt;</c> for the name every row carries.
    /// </summary>
    private static SoakOptions ParseSoak(string[] args)
    {
        var label = GetArg(args, "--label") ?? "soak";
        var csv = GetArg(args, "--csv") ?? $"{label}.csv";
        return new SoakOptions(
            SecondFilePath: GetArg(args, "--second"),
            SampleSeconds: int.TryParse(GetArg(args, "--sample"), out var s) && s > 0 ? s : 60,
            CsvPath: ExampleLogPaths.Resolve(csv),
            Label: label
        );
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }
}
