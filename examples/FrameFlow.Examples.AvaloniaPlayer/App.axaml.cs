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

        // FFmpeg has to resolve before any decode happens.
        var result = new FrameFlowBootstrapper(new FrameFlowNativeOptions()).Initialize();
        if (!result.IsSuccess)
            Console.Error.WriteLine($"FFmpeg bootstrap failed: {result.Message}");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Exactly one optional argument: a media file, or a folder to play
            // through. Anything else opens an empty window. Scanning the whole
            // argument list for a path that happens to exist is what made an
            // earlier version play its own --log-file; a stale invocation should
            // open nothing rather than pick a file out of a flag's value. Any
            // leading '-' is an option, so a file named like one is not openable
            // from the command line here.
            //
            // Diagnostic switches (presenter selection, hardware-decode A/B, audio
            // off, self-terminate) belong to tools/FrameFlow.TestBench (ADR-0068).
            var startupPath =
                desktop.Args is [var only]
                && !only.StartsWith('-')
                && (File.Exists(only) || Directory.Exists(only))
                    ? only
                    : null;

            desktop.MainWindow = new MainWindow { StartupPath = startupPath };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
