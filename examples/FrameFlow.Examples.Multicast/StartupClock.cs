using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.Multicast;

/// <summary>
/// Wall-clock instrumentation for startup phases. Started in
/// <see cref="Program.Main"/> and ticked at each phase boundary via
/// <see cref="Mark(string)"/> so we can isolate which step
/// (Avalonia init, XAML parse, FFmpeg bootstrap, ORT-CUDA session
/// construct, YOLO warmup, …) dominates cold-start time.
/// </summary>
/// <remarks>
/// Writes every mark to <see cref="Console.Out"/> so the earliest
/// phases (before <see cref="LoggerFactory"/> is wired) still produce
/// observable output. Once <see cref="AttachLogger"/> is called the
/// same marks also flow through <see cref="ILogger"/> at Information
/// level so they land in the <c>--log-file</c> trace.
/// </remarks>
internal static class StartupClock
{
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static ILogger? _logger;

    /// <summary>Milliseconds elapsed since <see cref="Stopwatch.StartNew"/>.</summary>
    public static double ElapsedMs => _sw.Elapsed.TotalMilliseconds;

    /// <summary>
    /// Attaches a logger so subsequent <see cref="Mark"/> calls also
    /// emit Information-level log lines. Idempotent.
    /// </summary>
    public static void AttachLogger(ILogger logger) => _logger = logger;

    /// <summary>
    /// Stamps the given phase name with the current elapsed time and
    /// writes "[Startup +Nms] phase" to <see cref="Console.Out"/> (and
    /// the attached logger, if any).
    /// </summary>
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
