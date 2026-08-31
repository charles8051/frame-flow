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
            var logFilePath = GetArg(args, "--log-file");
            var exitAfter = int.TryParse(GetArg(args, "--exit-after"), out var s) ? s : 0;
            var hwMode = GetArg(args, "--hw-mode");
            var fullscreen = args.Contains("--fullscreen");

            // Build the logger here (before the window shows) so the bootstrap
            // result and any window-creation problems are captured even if the
            // GPU window never renders (headless / no-desktop runs).
            var loggerFactory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Debug);
                if (!string.IsNullOrEmpty(logFilePath))
                    b.AddProvider(new FileLoggerProvider(ExampleLogPaths.Resolve(logFilePath), LogLevel.Debug));
            });
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
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }
}
