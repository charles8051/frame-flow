using Avalonia;

namespace FrameFlow.Examples.DualPlayer;

/// <summary>
/// Application entry point for the dual-player example.
/// </summary>
/// <remarks>
/// Runs <b>two fully independent <see cref="FrameFlow.Player.IMediaPlayer"/>
/// instances in one process</b> — each with its own decoders, video sink,
/// audio sink, frame pool and playback controller — to exercise the
/// multi-player-per-process path that has historically been a source of
/// shared-state bugs. The two panes differ only in their per-player
/// configuration (corpus file, hardware-decode policy, clock source, loop).
/// </remarks>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
