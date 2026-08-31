// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace FrameFlow.Playback;

/// <summary>
/// Source-generated log methods for <see cref="PlaybackControllerCore"/>.
/// Uses <see cref="LoggerMessageAttribute"/> for high-performance structured logging
/// per ADR-0010.
/// </summary>
internal sealed partial class PlaybackControllerCore
{
    // ── State transitions (Debug) ──────────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Playback state transition: {Source} → {Destination} via {Trigger}"
    )]
    private partial void LogPlaybackStateTransition(
        string source,
        string destination,
        string trigger
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Seek state transition: {Source} → {Destination} via {Trigger}"
    )]
    private partial void LogSeekStateTransition(string source, string destination, string trigger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Repeat mode transition: {Source} → {Destination} via {Trigger}"
    )]
    private partial void LogRepeatModeTransition(string source, string destination, string trigger);

    // ── Entry actions (Debug) ──────────────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Entering Initializing — loading source '{SourceName}'"
    )]
    private partial void LogInitializingEntry(string sourceName);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Entering Preparing — metadata already parsed during initialization"
    )]
    private partial void LogPreparingEntry();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Entering InitialBuffering — warming up decoders before BufferReady"
    )]
    private partial void LogInitialBufferingEntry();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entering Paused")]
    private partial void LogPausedEntry();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entering Playing")]
    private partial void LogPlayingEntry();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Entering Rebuffering — natural stall, waiting for refill"
    )]
    private partial void LogRebufferingEntry();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entering Ended")]
    private partial void LogEndedEntry();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entering Unloaded — disposing session")]
    private partial void LogUnloadedEntry();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Entering Error — category: {Category}, message: {ErrorMessage}"
    )]
    private partial void LogErrorEntry(string category, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entering Destroyed (terminal)")]
    private partial void LogDestroyedEntry();

    // ── Session lifecycle (Information/Debug) ───────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Session created via factory")]
    private partial void LogSessionCreated();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Session initialization failed: {ErrorMessage}"
    )]
    private partial void LogSessionInitializationFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Renderers started (first play)")]
    private partial void LogRenderersStarted();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Replaying ended item through recovery path — source: {SourceName}"
    )]
    private partial void LogReplayRecoveryStarted(string sourceName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session resumed from pause")]
    private partial void LogSessionResumed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Disposing session")]
    private partial void LogSessionDisposing();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Seek accepted — operation: {OperationId}, position: {PositionSeconds}s"
    )]
    private partial void LogSeekAccepted(long operationId, double positionSeconds);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Seek cancel requested — operation: {OperationId}, requested by: {RequestedBy}"
    )]
    private partial void LogSeekCancelRequested(long operationId, string requestedBy);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Seek completed — operation: {OperationId}")]
    private partial void LogSeekCompleted(long operationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Seek cancelled — operation: {OperationId}")]
    private partial void LogSeekCancelled(long operationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Seek faulted — operation: {OperationId}, message: {ErrorMessage}"
    )]
    private partial void LogSeekFaulted(long operationId, string errorMessage);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Ignoring stale seek outcome — operation: {OperationId}, active: {ActiveOperationId}"
    )]
    private partial void LogStaleSeekOutcome(long operationId, long activeOperationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ignoring malformed seek outcome — operation: {OperationId}, cancelled: {WasCanceled}, hasError: {HasError}"
    )]
    private partial void LogMalformedSeekOutcome(long operationId, bool wasCanceled, bool hasError);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Playback trigger {Trigger} proceeding after seek drain — operation: {OperationId}"
    )]
    private partial void LogPlaybackTriggerAfterSeekDrain(string trigger, long operationId);

    // ── Internal trigger routing (Trace/Warning) ───────────────────────

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Internal trigger posted to command channel: {Trigger}"
    )]
    private partial void LogInternalTriggerPosted(string trigger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Internal trigger dropped — channel full or closed: {Trigger}"
    )]
    private partial void LogInternalTriggerDropped(string trigger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Ignoring internal trigger after controller disposal: {Trigger}"
    )]
    private partial void LogInternalTriggerIgnoredAfterDisposal(string trigger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dropping stale internal trigger {Trigger} — not permitted from {CurrentState}"
    )]
    private partial void LogStaleInternalTrigger(string trigger, string currentState);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Ignoring seek outcome after controller disposal — operation: {OperationId}, cancelled: {WasCanceled}, hasError: {HasError}"
    )]
    private partial void LogSeekOutcomeIgnoredAfterDisposal(
        long operationId,
        bool wasCanceled,
        bool hasError
    );

    // ── Loop restart (Debug) ───────────────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Loop restarted — count: {LoopCount}, mode: {Mode}"
    )]
    private partial void LogLoopRestarted(int loopCount, string mode);

    // ── Loop stall (Error) ─────────────────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Loop STALLED — RepeatMode.One position {PositionSec:F1}s has overrun duration {DurationSec:F1}s by {OverrunSec:F1}s with no restart (loop count stuck at {LoopCount}). Frame delivery has likely stopped while the clock kept advancing."
    )]
    private partial void LogLoopStalled(
        int loopCount,
        double positionSec,
        double durationSec,
        double overrunSec
    );

    // ── Position ticker (Debug) ────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Position ticker started (250ms interval)")]
    private partial void LogPositionTickerStarted();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Position ticker stopped")]
    private partial void LogPositionTickerStopped();

    // ── DOT graph diagnostics (Debug) ─────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Generating DOT graphs for all state machines"
    )]
    private partial void LogDotGraphGeneration();

    // ── Dispatch loop (Trace) ──────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Trace, Message = "Dispatching command: {CommandType}")]
    private partial void LogDispatchCommand(string commandType);

    // ── Invalid operations (Warning) ───────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Invalid operation: cannot fire {Trigger} from state {CurrentState}"
    )]
    private partial void LogInvalidOperation(string trigger, string currentState);

    // ── Errors and lifecycle ───────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Error, Message = "Exception in dispatch loop: {ErrorMessage}")]
    private partial void LogDispatchException(string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PlaybackController disposed")]
    private partial void LogDisposed();
}
