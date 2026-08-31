using Avalonia;

namespace FrameFlow.Examples.AvaloniaPlayer;

/// <summary>
/// Application entry point. Configures FrameFlow services and launches the Avalonia app.
/// </summary>
/// <remarks>
/// Full Avalonia UI integration requires a MainWindow with a video surface control.
/// The App class configures FrameFlow with the Avalonia presenter and OpenAL audio.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // No timer-resolution call here, deliberately. FrameFlow's pacing clocks used to
        // need the host to raise the platform tick with timeBeginPeriod(1) or deliver ~34 fps
        // against a 60 fps source; they now sleep on their own high-resolution waitable
        // timers instead, which is per-timer rather than process-wide (ADR-0067). An example
        // that still made the call would be documenting a requirement that no longer exists.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
