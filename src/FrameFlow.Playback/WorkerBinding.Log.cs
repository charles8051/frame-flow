// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace FrameFlow.Playback;

/// <summary>
/// Source-generated log methods for <see cref="WorkerBinding{TWorker}"/>.
/// Uses <see cref="LoggerMessageAttribute"/> for high-performance structured logging
/// per ADR-0010 and ADR-0026.
/// </summary>
/// <remarks>
/// Log methods are <c>static</c> and accept a nullable <see cref="ILogger"/>
/// so they can be called unconditionally — when the logger is <c>null</c>,
/// the call is a no-op. This avoids null checks at every call site.
/// </remarks>
internal sealed partial class WorkerBinding<TWorker>
{
    // ── Lifecycle events (Debug) ───────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Worker started: {WorkerType}")]
    private static partial void LogWorkerStartedCore(ILogger logger, string workerType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Worker stopped: {WorkerType}")]
    private static partial void LogWorkerStoppedCore(ILogger logger, string workerType);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Double-start guarded — worker already running: {WorkerType}"
    )]
    private static partial void LogDoubleStartGuardedCore(ILogger logger, string workerType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Disposal complete: {WorkerType}")]
    private static partial void LogDisposalCompleteCore(ILogger logger, string workerType);

    // ── Error and a slow stop (Error / Warning) ────────────────────────

    [LoggerMessage(Level = LogLevel.Error, Message = "Worker faulted: {WorkerType}")]
    private static partial void LogWorkerErrorCore(
        ILogger logger,
        Exception exception,
        string workerType
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Worker {WorkerType} has not ended {Seconds:F0}s after it was asked to stop. It is not observing cancellation; the stop waits for it."
    )]
    private static partial void LogWorkerNotEndedCore(
        ILogger logger,
        string workerType,
        double seconds
    );

    // ── Null-safe wrappers ─────────────────────────────────────────────

    private static string WorkerTypeName => typeof(TWorker).Name;

    private static void LogWorkerStarted(ILogger? logger)
    {
        if (logger is not null)
            LogWorkerStartedCore(logger, WorkerTypeName);
    }

    private static void LogWorkerStopped(ILogger? logger)
    {
        if (logger is not null)
            LogWorkerStoppedCore(logger, WorkerTypeName);
    }

    private static void LogDoubleStartGuarded(ILogger? logger)
    {
        if (logger is not null)
            LogDoubleStartGuardedCore(logger, WorkerTypeName);
    }

    private static void LogDisposalComplete(ILogger? logger)
    {
        if (logger is not null)
            LogDisposalCompleteCore(logger, WorkerTypeName);
    }

    private static void LogWorkerError(ILogger? logger, Exception exception)
    {
        if (logger is not null)
            LogWorkerErrorCore(logger, exception, WorkerTypeName);
    }

    private static void LogWorkerNotEnded(ILogger? logger, TimeSpan waited)
    {
        if (logger is not null)
            LogWorkerNotEndedCore(logger, WorkerTypeName, waited.TotalSeconds);
    }
}
