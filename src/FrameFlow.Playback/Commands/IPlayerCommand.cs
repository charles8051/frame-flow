// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback.Commands;

/// <summary>
/// Marker interface for all commands dispatched through the playback controller's
/// channel-serialized command loop. Each command carries its own completion source
/// so the caller can await the result.
/// </summary>
internal interface IPlayerCommand
{
    /// <summary>Completion source that the command loop signals when processing finishes.</summary>
    TaskCompletionSource<Result> Completion { get; }

    /// <summary>Cancellation token supplied by the caller.</summary>
    CancellationToken CancellationToken { get; }
}

/// <summary>
/// Fires a simple trigger on the primary playback state machine.
/// Used for Play, Pause, Stop, Reset, and Release operations.
/// </summary>
internal sealed record FireTriggerCommand(PlaybackTrigger Trigger) : IPlayerCommand
{
    public TaskCompletionSource<Result> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Requests a seek to <paramref name="Position"/>.
/// </summary>
internal sealed record SeekCommand(TimeSpan Position) : IPlayerCommand
{
    public TaskCompletionSource<Result> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Requests loading a media source into the playback pipeline.
/// </summary>
internal sealed record LoadCommand(IMediaSource Source) : IPlayerCommand
{
    public TaskCompletionSource<Result> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Changes the repeat mode on the orthogonal repeat region.
/// </summary>
internal sealed record SetRepeatCommand(RepeatMode Mode) : IPlayerCommand
{
    public TaskCompletionSource<Result> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Carries a <see cref="PlaybackTrigger"/> originated internally by the pipeline
/// (e.g. <see cref="PlaybackTrigger.LastFrameRendered"/>,
/// <see cref="PlaybackTrigger.FatalError"/>,
/// <see cref="PlaybackTrigger.BufferReady"/>,
/// <see cref="PlaybackTrigger.BufferUnderrun"/>).
/// </summary>
/// <remarks>
/// Unlike <see cref="FireTriggerCommand"/>, this command bypasses the
/// <c>CanFire</c> validation guard in the dispatch loop because the pipeline
/// knows the trigger is valid at the time it fires. The dispatch loop fires
/// the trigger unconditionally.
/// </remarks>
internal sealed record InternalTriggerCommand(PlaybackTrigger Trigger) : IPlayerCommand
{
    /// <summary>
    /// Optional exception associated with a <see cref="PlaybackTrigger.FatalError"/> trigger.
    /// Ignored for all other triggers.
    /// </summary>
    public Exception? Error { get; init; }

    public TaskCompletionSource<Result> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Carries the terminal outcome of an asynchronously launched seek operation back
/// through the playback controller's command channel.
/// </summary>
internal sealed record SeekOutcomeCommand(long OperationId) : IPlayerCommand
{
    /// <summary>
    /// True when the seek observed cooperative cancellation.
    /// </summary>
    public bool WasCanceled { get; init; }

    /// <summary>
    /// Optional exception raised by the background seek runner.
    /// </summary>
    public Exception? Error { get; init; }

    public TaskCompletionSource<Result> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken CancellationToken { get; init; }
}
