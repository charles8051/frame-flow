// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace FrameFlow.Playback;

/// <summary>
/// Source-generated log methods for <see cref="PositionTickerWorker"/>.
/// Uses <see cref="LoggerMessageAttribute"/> for high-performance structured logging.
/// </summary>
internal sealed partial class PositionTickerWorker
{
    // ── Lifecycle events (Debug) ───────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Position ticker started")]
    private static partial void LogTickerLoopStartedCore(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Position ticker stopped")]
    private static partial void LogTickerLoopStoppedCore(ILogger logger);

    // ── Error (Error) ──────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Error, Message = "Position ticker loop faulted")]
    private static partial void LogTickerLoopErrorCore(ILogger logger, Exception exception);

    // ── Null-safe wrappers ─────────────────────────────────────────────

    private static void LogTickerLoopStarted(ILogger? logger)
    {
        if (logger is not null)
            LogTickerLoopStartedCore(logger);
    }

    private static void LogTickerLoopStopped(ILogger? logger)
    {
        if (logger is not null)
            LogTickerLoopStoppedCore(logger);
    }

    private static void LogTickerLoopError(ILogger? logger, Exception exception)
    {
        if (logger is not null)
            LogTickerLoopErrorCore(logger, exception);
    }
}
