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
            // One optional argument: a media file, or a folder to play through.
            // Diagnostic switches (presenter selection, hardware-decode A/B, audio
            // off, self-terminate) belong to tools/FrameFlow.TestBench (ADR-0068).
            var args = desktop.Args ?? [];
            desktop.MainWindow = new MainWindow
            {
                StartupPath = args.FirstOrDefault(a => File.Exists(a) || Directory.Exists(a)),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
