using Avalonia;

namespace FrameFlow.Examples.Multicast.Dml;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupClock.Mark("Main entered");
        var builder = BuildAvaloniaApp();
        StartupClock.Mark("Avalonia AppBuilder configured");
        builder.StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
