// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>An input to <see cref="PlaylistSessionProtocol.Step"/>.</summary>
/// <remarks>
/// Commands, item notifications and queue requests arrive on the shell's channel, one at a time.
/// <see cref="Outcome"/> and <see cref="Continue"/> are fed back by the shell while it handles one
/// of them.
/// </remarks>
internal abstract record PlaylistSessionInput
{
    private PlaylistSessionInput() { }

    // ── Commands from the controller ────────────────────────────────────────

    /// <summary>Load the first item.</summary>
    public sealed record Initialize(int Command) : PlaylistSessionInput;

    /// <summary>Warm the item up, on load or on a seek out of Ended.</summary>
    public sealed record WarmUp(int Command) : PlaylistSessionInput;

    /// <summary>Play.</summary>
    public sealed record Play(int Command) : PlaylistSessionInput;

    /// <summary>Pause.</summary>
    public sealed record Pause(int Command) : PlaylistSessionInput;

    /// <summary>Seek within the current item.</summary>
    public sealed record Seek(int Command, TimeSpan Position) : PlaylistSessionInput;

    /// <summary>Rewind the current item to its start.</summary>
    public sealed record Rewind(int Command) : PlaylistSessionInput;

    /// <summary>Dispose the item the session holds. The last input the session acts on.</summary>
    public sealed record Dispose : PlaylistSessionInput;

    // ── Notifications from an item runtime ──────────────────────────────────

    /// <summary>A runtime reached the end of its stream.</summary>
    /// <param name="Generation">The generation the runtime's callbacks are tagged with.</param>
    /// <param name="Run">The runtime's run number, read when it raised the end-of-stream.</param>
    public sealed record EndOfStream(int Generation, int Run) : PlaylistSessionInput;

    /// <summary>A runtime's worker faulted.</summary>
    /// <param name="Generation">The generation the runtime's callbacks are tagged with.</param>
    /// <param name="Error">The fault.</param>
    /// <param name="PlayedFor">How far the item had played, read when the fault was raised.</param>
    public sealed record Fault(int Generation, Exception Error, TimeSpan PlayedFor)
        : PlaylistSessionInput;

    // ── Requests from the coordinator ───────────────────────────────────────

    /// <summary>The caller skipped the current item.</summary>
    /// <param name="Generation">The session's generation when the skip was requested.</param>
    /// <param name="RunAtRequest">The session's run state when the skip was requested.</param>
    public sealed record SkipRequested(int Generation, PlaylistRunState RunAtRequest)
        : PlaylistSessionInput;

    /// <summary>The caller recorded a jump on the coordinator.</summary>
    public sealed record JumpRequested : PlaylistSessionInput;

    // ── Fed back by the shell ───────────────────────────────────────────────

    /// <summary>The awaited action has finished.</summary>
    /// <param name="Kind">Whether it succeeded, failed or was cancelled.</param>
    /// <param name="Run">The runtime's run number, read when the action finished.</param>
    /// <param name="Info">The runtime's metadata, after an open.</param>
    /// <param name="Error">The failure or cancellation.</param>
    public sealed record Outcome(
        PlaylistOutcome Kind,
        int Run = 0,
        MediaInfo? Info = null,
        Exception? Error = null
    ) : PlaylistSessionInput;

    /// <summary>The step's immediate actions have run, and the step was not the last.</summary>
    public sealed record Continue : PlaylistSessionInput;
}

/// <summary>How an awaited action finished.</summary>
internal enum PlaylistOutcome
{
    Ok,
    Failed,
    Cancelled,
}

/// <summary>How a command finished, as the shell hands it back to its caller.</summary>
internal readonly record struct PlaylistCommandResult(PlaylistOutcome Kind, Exception? Error)
{
    public static PlaylistCommandResult Ok { get; } = new(PlaylistOutcome.Ok, null);

    public static PlaylistCommandResult Failed(Exception error) => new(PlaylistOutcome.Failed, error);

    /// <summary>The result an awaited action's outcome gives the command it served.</summary>
    public static PlaylistCommandResult From(PlaylistSessionInput.Outcome outcome) =>
        new(outcome.Kind, outcome.Kind == PlaylistOutcome.Ok ? null : outcome.Error);
}

/// <summary>What a failed item did, for its report.</summary>
internal enum PlaylistItemFailure
{
    /// <summary>It could not be opened, warmed or started.</summary>
    CouldNotStart,

    /// <summary>It faulted while it played.</summary>
    FaultedDuringPlayback,
}

/// <summary>A log line the session writes that has no report of its own.</summary>
internal enum PlaylistSessionLog
{
    /// <summary>An advance was requested after the queue ended.</summary>
    AdvanceIgnoredAtEnd,

    /// <summary>An end-of-stream came from a run a seek or rewind replaced.</summary>
    StaleEndOfStream,

    /// <summary>An in-place rewind failed, and the item is rebuilt instead.</summary>
    ReplayFellBack,

    /// <summary>A skipped item's pause failed at the end of the queue, and it is disposed.</summary>
    KeptItemPauseFailed,
}

/// <summary>An effect <see cref="PlaylistSessionProtocol.Step"/> asks the shell to perform.</summary>
internal abstract record PlaylistSessionAction
{
    private PlaylistSessionAction() { }

    // ── Awaited: the shell feeds back an Outcome ────────────────────────────

    /// <summary>Create a runtime tagged with <paramref name="Generation"/>, and open the source.</summary>
    public sealed record OpenItem(int Generation, IMediaSource Source, int? Command)
        : PlaylistSessionAction;

    /// <summary>Warm the runtime up.</summary>
    public sealed record WarmUpItem(int? Command) : PlaylistSessionAction;

    /// <summary>Play the runtime.</summary>
    public sealed record PlayItem(int? Command) : PlaylistSessionAction;

    /// <summary>Pause the runtime.</summary>
    public sealed record PauseItem(int? Command) : PlaylistSessionAction;

    /// <summary>Seek the runtime.</summary>
    public sealed record SeekItem(TimeSpan Position, int? Command) : PlaylistSessionAction;

    /// <summary>Rewind the runtime in place.</summary>
    public sealed record RewindItem(int? Command) : PlaylistSessionAction;

    /// <summary>Dispose the runtime. It never fails: the shell logs a throw and carries on.</summary>
    public sealed record DisposeItem : PlaylistSessionAction;

    // ── Immediate ───────────────────────────────────────────────────────────

    /// <summary>Attach the session's skip and jump handlers to the coordinator.</summary>
    public sealed record AttachToCoordinator : PlaylistSessionAction;

    /// <summary>Stop the controller's position clock, so the next item starts from zero.</summary>
    public sealed record StopClock : PlaylistSessionAction;

    /// <summary>Tell the controller the current item's metadata changed.</summary>
    public sealed record ReportCurrentItemChanged(MediaInfo? Info) : PlaylistSessionAction;

    /// <summary>Log a failed item and report it to the controller as a recoverable error.</summary>
    public sealed record ReportItemFailed(string Source, PlaylistItemFailure What, Exception? Error)
        : PlaylistSessionAction;

    /// <summary>Tell the controller the queue has ended.</summary>
    public sealed record ReportEndOfStream : PlaylistSessionAction;

    /// <summary>Hand the controller a fatal error.</summary>
    public sealed record ReportFatal(Exception Error) : PlaylistSessionAction;

    /// <summary>Raise the coordinator's <c>SourceTransitioned</c> for an item that started.</summary>
    public sealed record RaiseTransition(PlaylistItem Item, MediaInfo? Info, int Index, bool Wrapped)
        : PlaylistSessionAction;

    /// <summary>Complete a command.</summary>
    public sealed record CompleteCommand(int Command, PlaylistCommandResult Result)
        : PlaylistSessionAction;

    /// <summary>Write a log line.</summary>
    public sealed record Log(
        PlaylistSessionLog Event,
        string? Source = null,
        Exception? Error = null,
        int Run = 0,
        int CurrentRun = 0,
        bool Faulted = false
    ) : PlaylistSessionAction;
}

/// <summary>What one call of <see cref="PlaylistSessionProtocol.Step"/> asks of the shell.</summary>
/// <param name="Actions">Immediate actions, performed in order.</param>
/// <param name="Awaited">
/// The action to await after them, if any. Its outcome is the next input.
/// </param>
/// <param name="Done">
/// With no awaited action: whether the input is handled. When it is not, the shell feeds
/// <see cref="PlaylistSessionInput.Continue"/>.
/// </param>
internal sealed record PlaylistSessionStep(
    ImmutableArray<PlaylistSessionAction> Actions,
    PlaylistSessionAction? Awaited,
    bool Done
);
