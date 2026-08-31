using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FrameFlow.Native;

namespace FrameFlow.Examples.DualPlayer;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Bootstrap FFmpeg once for the whole process before either player
        // starts. The bootstrapper caches its result, so each player's
        // MediaPlayer.CreateAsync re-invocation is cheap — but doing it here
        // surfaces a native-load failure before any window appears.
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
            var options = DualPlayerOptions.Parse(args);
            desktop.MainWindow = new MainWindow(options);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
