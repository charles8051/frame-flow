using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.Camera.Multicast;

/// <summary>
/// Wall-clock instrumentation for startup phases. Started in
/// <see cref="Program.Main"/> and ticked at each phase boundary via
/// <see cref="Mark(string)"/> so we can isolate which step
/// (Avalonia init, XAML parse, camera enumerate / open / first frame,
/// ORT-CUDA session construct, YOLO warmup, …) dominates cold-start
/// time.
/// </summary>
internal static class StartupClock
{
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static ILogger? _logger;

    public static double ElapsedMs => _sw.Elapsed.TotalMilliseconds;

    public static void AttachLogger(ILogger logger) => _logger = logger;

    public static void Mark(string phase)
    {
        var ms = _sw.Elapsed.TotalMilliseconds;
        var line = $"[Startup +{ms,7:F0} ms] {phase}";
        Console.WriteLine(line);
        _logger?.LogInformation(
            "[Startup +{ElapsedMs:F0} ms] {Phase}",
            ms,
            phase);
    }
}
