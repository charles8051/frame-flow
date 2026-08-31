// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace FrameFlow.MotionClip;

/// <summary>Top-level command verb. <c>Help</c> is the default when no command is given.</summary>
public enum MotionClipVerb
{
    /// <summary>Run the motion-triggered recorder. Must be given explicitly.</summary>
    Run,

    /// <summary>Enumerate the system's cameras and print their Ids, then exit.</summary>
    Scan,

    /// <summary>Print usage and exit. The default when no command is supplied.</summary>
    Help,

    /// <summary>Print the version and exit.</summary>
    Version,
}

/// <summary>
/// Parsed command line, shared between the headless entry point (<c>Program</c>)
/// and the windowed entry point (<c>App</c>) so the two modes can't drift.
/// </summary>
/// <remarks>
/// Usage: <c>motionclip &lt;run|scan&gt; [flags]</c>. The command is required: a
/// bare <c>motionclip</c> (or <c>-h</c>/<c>--help</c>) prints help; <c>--version</c>
/// prints the version.
/// <list type="bullet">
/// <item><c>scan</c> — print available cameras (Id + name) and exit.</item>
/// <item><c>--IdStartsWith &lt;prefix&gt;</c> — track the camera whose Id starts
/// with this prefix (see <c>scan</c> for Ids). The recorder starts regardless of
/// whether the camera is currently plugged in and connects when it appears.</item>
/// <item><c>--camera &lt;index&gt;</c> — track the camera at this enumeration
/// index (resolved to its Id at startup). Lower precedence than --IdStartsWith.</item>
/// <item><c>--synthetic</c> — use the generated scene instead of a camera.</item>
/// <item><c>--headless</c> — run with no preview window.</item>
/// <item><c>--sensitivity &lt;0.0–1.0&gt;</c> — motion sensitivity.
/// Higher is MORE sensitive: 0.1 ignores all but big movements,
/// 0.8 (default) catches normal activity, 1.0 triggers on tiny twitches.
/// Mapped internally to a changed-pixel ratio via
/// <see cref="ClipRecorderArgs.SensitivityToMotionThreshold"/>.</item>
/// <item><c>--output-dir &lt;dir&gt;</c>, <c>--fps &lt;n&gt;</c>,
/// <c>--exit-after &lt;seconds&gt;</c>, <c>--log-file &lt;path&gt;</c>,
/// <c>--log-dir &lt;dir&gt;</c> (timestamped log file in a directory).</item>
/// <item><c>--log-level &lt;level&gt;</c> — minimum log level
/// (trace|debug|info|warning|error|critical|none). Default info.</item>
/// </list>
/// Camera selection precedence: <c>--IdStartsWith</c> → <c>--camera</c> → first
/// available camera.
/// </remarks>
public sealed record ClipRecorderArgs
{
    public MotionClipVerb Verb { get; init; } = MotionClipVerb.Help;
    public bool Headless { get; init; }
    public bool Synthetic { get; init; }

    /// <summary>Track the camera whose Id starts with this prefix (null = unset).</summary>
    public string? IdStartsWith { get; init; }

    /// <summary>Track the camera at this enumeration index (null = unset).</summary>
    public int? CameraIndex { get; init; }

    public string OutputDirectory { get; init; } =
        Path.Combine(Directory.GetCurrentDirectory(), "clips");
    public int FrameRate { get; init; } = 30;

    /// <summary>
    /// Motion sensitivity, on a 0–1 user-facing scale. <c>0.1</c> ignores all
    /// but large movements; <c>0.8</c> (default) catches normal activity;
    /// <c>1.0</c> triggers on tiny twitches. Mapped to the detector's
    /// changed-pixel ratio via <see cref="SensitivityToMotionThreshold"/>.
    /// </summary>
    public double Sensitivity { get; init; } = 0.8;

    /// <summary>
    /// Derived changed-pixel ratio fed into <see cref="MotionDetector"/>.
    /// Computed from <see cref="Sensitivity"/>; callers shouldn't override
    /// this directly. Lower ratio = more sensitive detector.
    /// </summary>
    public double MotionThreshold => SensitivityToMotionThreshold(Sensitivity);

    /// <summary>
    /// Maps user-facing sensitivity (0.0–1.0) to the detector's internal
    /// changed-pixel ratio. Inverse-linear with a floor:
    /// <c>max(0.002, 0.1 × (1 − clamp(s, 0, 1)))</c>. Anchor points:
    /// <list type="bullet">
    /// <item><c>s = 0.1</c> → <c>0.090</c> (9% — only big movements trigger)</item>
    /// <item><c>s = 0.5</c> → <c>0.050</c> (5%)</item>
    /// <item><c>s = 0.8</c> → <c>0.020</c> (2% — the previous default)</item>
    /// <item><c>s = 1.0</c> → <c>0.002</c> (0.2% — very twitchy)</item>
    /// </list>
    /// The floor prevents <c>s = 1.0</c> from collapsing to a ratio of zero
    /// (which would record continuously regardless of input).
    /// </summary>
    public static double SensitivityToMotionThreshold(double sensitivity) =>
        Math.Max(0.002, 0.1 * (1.0 - Math.Clamp(sensitivity, 0.0, 1.0)));

    /// <summary>
    /// Hard cap on a single clip's length, in seconds. The recording gate emits
    /// the in-progress segment when frame count reaches <c>MaxClipSeconds × fps</c>
    /// even if motion is still active — bounds memory and prevents the "infinite
    /// recording under continuous motion" state. Default 30 s.
    /// </summary>
    public int MaxClipSeconds { get; init; } = 30;

    /// <summary>
    /// Number of pooled frame buffers the underlying <c>CameraSession</c>
    /// pre-allocates. The session uses <c>DropIncoming</c> exhaustion, so
    /// when all buffers are "outstanding" (held by the downstream resize /
    /// gate / encoder pipeline), the camera drops the next captured frame
    /// at the source rather than blocking. Raising this gives the encoder
    /// more headroom during long-clip / slow-encode bursts at the cost of
    /// memory (one buffer ≈ camera frame size, e.g. ~1.4 MB for 1280x720
    /// NV12). Default 3 matches Periphery's own default; production kiosks
    /// running motion-triggered recording with non-trivial encode windows
    /// typically want 6–8.
    /// </summary>
    public int CameraBuffers { get; init; } = 3;

    /// <summary>
    /// Numpad-numbered sectors (1-9) of a 3×3 grid that motion detection
    /// watches. The whole frame is still previewed and recorded — only
    /// motion-trigger evaluation is restricted. <see langword="null"/> or
    /// empty = watch the whole frame (all 9 sectors armed), identical to
    /// the historic behaviour.
    /// </summary>
    /// <remarks>
    /// Layout — numpad convention (1 is bottom-left, 9 is top-right):
    /// <code>
    ///   7 8 9
    ///   4 5 6
    ///   1 2 3
    /// </code>
    /// </remarks>
    public IReadOnlyList<int>? MotionSectors { get; init; }

    public double ExitAfterSeconds { get; init; }
    public string? LogFile { get; init; }

    /// <summary>
    /// Directory for the file log sink. When set (and <see cref="LogFile"/> is
    /// not), a timestamped <c>motionclip-*.log</c> is written here. <c>--log-file</c>
    /// takes precedence if both are given.
    /// </summary>
    public string? LogDirectory { get; init; }

    /// <summary>
    /// Minimum log level for the console (and <c>--log-file</c>) sink. Default
    /// <see cref="LogLevel.Information"/>; pass <c>--log-level debug</c> (or
    /// <c>trace</c>) to surface FFmpeg-bootstrap / per-frame diagnostics.
    /// </summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    public static ClipRecorderArgs Parse(string[] args)
    {
        var verb = MotionClipVerb.Help; // no command → help
        bool help = false;
        bool version = false;
        bool headless = false;
        bool synthetic = false;
        string? idStartsWith = null;
        int? cameraIndex = null;
        string outputDir = Path.Combine(Directory.GetCurrentDirectory(), "clips");
        int fps = 30;
        double sensitivity = 0.8;
        int maxClipSeconds = 30;
        int cameraBuffers = 3;
        IReadOnlyList<int>? motionSectors = null;
        double exitAfter = 0;
        string? logFile = null;
        string? logDir = null;
        var logLevel = LogLevel.Information;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "scan":
                    verb = MotionClipVerb.Scan;
                    break;
                case "run":
                    verb = MotionClipVerb.Run;
                    break;
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                    help = true;
                    break;
                case "--version":
                    version = true;
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--synthetic":
                    synthetic = true;
                    break;
                case "--IdStartsWith" when i + 1 < args.Length:
                case "--id-starts-with" when i + 1 < args.Length:
                    idStartsWith = args[++i];
                    break;
                case "--camera" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var ci))
                        cameraIndex = ci;
                    break;
                case "--output-dir" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;
                case "--fps" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var f))
                        fps = f;
                    break;
                case "--sensitivity" when i + 1 < args.Length:
                    if (
                        double.TryParse(
                            args[++i],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var s
                        )
                        && s >= 0
                    )
                        sensitivity = Math.Clamp(s, 0.0, 1.0);
                    break;
                case "--max-clip-length" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var mcl) && mcl > 0)
                        maxClipSeconds = mcl;
                    break;
                case "--camera-buffers" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var cb) && cb > 0)
                        cameraBuffers = cb;
                    break;
                case "--motion-sectors" when i + 1 < args.Length:
                    motionSectors = ParseMotionSectors(args[++i]);
                    break;
                case "--exit-after" when i + 1 < args.Length:
                    _ = double.TryParse(
                        args[++i],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out exitAfter
                    );
                    break;
                case "--log-file" when i + 1 < args.Length:
                    logFile = args[++i];
                    break;
                case "--log-dir" when i + 1 < args.Length:
                    logDir = args[++i];
                    break;
                case "--log-level" when i + 1 < args.Length:
                    logLevel = ParseLogLevel(args[++i]) ?? logLevel;
                    break;
            }
        }

        // --help beats --version beats the command; both override run/scan.
        if (help)
            verb = MotionClipVerb.Help;
        else if (version)
            verb = MotionClipVerb.Version;

        return new ClipRecorderArgs
        {
            Verb = verb,
            Headless = headless,
            Synthetic = synthetic,
            IdStartsWith = idStartsWith,
            CameraIndex = cameraIndex,
            OutputDirectory = outputDir,
            FrameRate = fps <= 0 ? 30 : fps,
            Sensitivity = sensitivity,
            MaxClipSeconds = maxClipSeconds,
            CameraBuffers = cameraBuffers,
            MotionSectors = motionSectors,
            ExitAfterSeconds = exitAfter,
            LogFile = logFile,
            LogDirectory = logDir,
            LogLevel = logLevel,
        };
    }

    /// <summary>
    /// Parses the <c>--motion-sectors</c> argument value into a sorted,
    /// deduplicated list of numpad sector numbers (1-9). Accepts:
    /// <list type="bullet">
    /// <item><c>"all"</c> (case-insensitive) → <see langword="null"/>, meaning "no filter" (all 9 armed).</item>
    /// <item><c>"5"</c> → just sector 5.</item>
    /// <item><c>"4,5,6"</c> or <c>"4 5 6"</c> → middle row.</item>
    /// <item><c>"123456789"</c> → equivalent to "all".</item>
    /// </list>
    /// Tokens outside 1-9 are silently dropped (matches
    /// <see cref="MotionSectorMask"/>'s leniency). Returns
    /// <see langword="null"/> for "all" or an empty result so the gate
    /// constructs an all-armed mask — preserves historic behaviour.
    /// </summary>
    private static IReadOnlyList<int>? ParseMotionSectors(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || string.Equals(s.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            return null;

        var set = new HashSet<int>();
        // Split on any non-digit so "4,5,6" / "4 5 6" / "456" all work.
        foreach (char c in s)
        {
            if (c is >= '1' and <= '9')
                set.Add(c - '0');
        }
        if (set.Count == 0)
            return null;
        return set.OrderBy(n => n).ToArray();
    }

    /// <summary>
    /// Maps friendly level names to <see cref="LogLevel"/>. Returns null for
    /// unrecognized input so the caller keeps the current/default level.
    /// </summary>
    private static LogLevel? ParseLogLevel(string s) =>
        s.Trim().ToLowerInvariant() switch
        {
            "trace" or "verbose" => LogLevel.Trace,
            "debug" or "dbug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" or "err" => LogLevel.Error,
            "critical" or "crit" or "fatal" => LogLevel.Critical,
            "none" or "off" or "silent" => LogLevel.None,
            _ => null,
        };
}
