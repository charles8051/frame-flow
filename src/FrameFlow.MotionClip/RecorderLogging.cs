// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace FrameFlow.MotionClip;

/// <summary>
/// Builds the logger factory used by both run modes: a single-line console
/// sink plus an optional <c>--log-file</c> file sink. The console sink works
/// because this example targets the console subsystem (see the csproj note),
/// so logs are visible in headless runs and in the console that accompanies
/// the windowed preview.
/// </summary>
internal static class RecorderLogging
{
    public static ILoggerFactory Create(
        string? logFile,
        string? logDirectory,
        LogLevel minLevel = LogLevel.Information
    ) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minLevel);
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });

            string? path = ResolveLogPath(logFile, logDirectory);
            if (path is not null)
                builder.AddProvider(new FileLoggerProvider(path));
        });

    /// <summary>
    /// Resolves the effective log file path: an explicit <c>--log-file</c> wins;
    /// otherwise <c>--log-dir</c> yields a timestamped <c>motionclip-*.log</c> in
    /// that directory (created if needed) so successive runs don't clobber each
    /// other. Returns <see langword="null"/> when neither is set (console only).
    /// </summary>
    private static string? ResolveLogPath(string? logFile, string? logDirectory)
    {
        if (!string.IsNullOrWhiteSpace(logFile))
            return logFile;

        if (string.IsNullOrWhiteSpace(logDirectory))
            return null;

        Directory.CreateDirectory(logDirectory);
        return Path.Combine(logDirectory, $"motionclip-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }
}
