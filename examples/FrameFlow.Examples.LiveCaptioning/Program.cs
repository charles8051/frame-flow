using Avalonia;

namespace FrameFlow.Examples.LiveCaptioning;

/// <summary>
/// Application entry point. Parses the file-path arg from
/// <c>desktop.Args</c> in <see cref="App"/>, then hands off to the
/// classic-desktop lifetime.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
