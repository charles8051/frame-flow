using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FrameFlow.Examples.AvaloniaPlayer;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // One optional argument: a media file, or a folder to play through.
            desktop.MainWindow = new MainWindow
            {
                StartupPath = desktop.Args is [var path] ? path : null,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
