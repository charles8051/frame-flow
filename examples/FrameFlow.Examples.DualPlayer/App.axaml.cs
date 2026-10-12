using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FrameFlow.Examples.DualPlayer;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // [left] [right]: one path plays in both panes, none plays the test corpus clip.
            var corpus = Path.Combine(
                RepoRoot.Find(), "tests", "corpus", "files", "test-1080p-h264-aac.mp4");
            var (left, right) = desktop.Args switch
            {
                [var l, var r] => (l, r),
                [var both] => (both, both),
                _ => (corpus, corpus),
            };
            desktop.MainWindow = new MainWindow { LeftPath = left, RightPath = right };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
