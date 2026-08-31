using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FrameFlow.Native;

namespace FrameFlow.Examples.AvaloniaPlayer;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Bootstrap FFmpeg before any playback can happen.
        var bootstrapper = new FrameFlowBootstrapper(new FrameFlowNativeOptions());
        var result = bootstrapper.Initialize();
        if (!result.IsSuccess)
            System.Console.Error.WriteLine($"FFmpeg bootstrap failed: {result.Message}");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? System.Array.Empty<string>();

            // Find the positional media-file arg, skipping the VALUES of
            // value-taking flags. Without this, a value that happens to exist on
            // disk (notably --log-file's path) is mistaken for the media file to
            // open — which made the repro profile try to play its own log file.
            var valueFlags = new System.Collections.Generic.HashSet<string>(
                System.StringComparer.Ordinal
            )
            {
                "--log-file",
                "--hw-mode",
                "--presenter",
                "--exit-after",
                "--folder",
            };
            string? startupFile = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (valueFlags.Contains(args[i]))
                {
                    i++; // skip the flag's value
                    continue;
                }
                if (
                    !args[i].StartsWith("--", System.StringComparison.Ordinal)
                    && System.IO.File.Exists(args[i])
                )
                {
                    startupFile = args[i];
                    break;
                }
            }

            var startupLoop = args.Contains("--loop");

            // --no-audio reproduces the headless-signage path: no audio sink is
            // attached, so the player paces video off the WallClockSource
            // fallback (ADR-0003) instead of the audio sample-counter clock.
            var noAudio = args.Contains("--no-audio");

            // --log-file <path> enables an opt-in file sink alongside the
            // in-window TextBox log. Useful for inspecting playback issues
            // after the window has closed or crashed.
            string? logFilePath = null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--log-file")
                {
                    logFilePath = args[i + 1];
                    break;
                }
            }

            // --hw-mode <disabled|auto|required> overrides the decoder's
            // hardware-acceleration policy so software vs hardware decode can
            // be A/B'd on a single binary. Unset = Auto (the default).
            string? hwMode = null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--hw-mode")
                {
                    hwMode = args[i + 1];
                    break;
                }
            }

            // --presenter <cpu|gpu> selects the video surface. gpu = the Windows
            // zero-copy composition-interop presenter; default/cpu = FrameFlowPlayerView.
            string? presenter = null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--presenter")
                {
                    presenter = args[i + 1];
                    break;
                }
            }

            // --folder <path> auto-opens a folder on startup and plays through all
            // of its media files over one warm presenter (the gapless-playlist
            // path). Mirrors passing a single file as a positional arg.
            string? folderPath = null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--folder")
                {
                    folderPath = args[i + 1];
                    break;
                }
            }

            // --exit-after <seconds> auto-closes the window after N seconds so an
            // agent can run the example unattended (launch, exercise, read the
            // log) without a human closing the window. 0/unset = run until closed.
            int exitAfterSeconds = 0;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--exit-after"
                    && int.TryParse(args[i + 1], out var secs)
                    && secs > 0)
                {
                    exitAfterSeconds = secs;
                    break;
                }
            }

            desktop.MainWindow = new MainWindow
            {
                StartupFilePath = startupFile,
                StartupFolderPath = folderPath,
                StartupLoop = startupLoop,
                StartupLogFilePath = logFilePath,
                StartupHwMode = hwMode,
                StartupPresenter = presenter,
                StartupNoAudio = noAudio,
                StartupExitAfterSeconds = exitAfterSeconds,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
