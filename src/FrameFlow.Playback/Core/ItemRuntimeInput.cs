// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace FrameFlow.Playback.Core;

/// <summary>An input to <see cref="ItemRuntimeProtocol.Step"/>.</summary>
/// <remarks>
/// Commands come from the runtime's caller, each with the shell's number for it. The rest the shell
/// feeds back: the outcome of the action a step awaited, and the end of each piece of a run's work.
/// </remarks>
internal abstract record ItemRuntimeInput
{
    private ItemRuntimeInput() { }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>A command from the runtime's caller, numbered by the shell.</summary>
    /// <param name="Command">The shell's number for the command, which its completion carries.</param>
    public abstract record CommandInput(int Command) : ItemRuntimeInput;

    /// <summary>
    /// Open the source, positioned at <paramref name="Start"/> in the source's time, for an item
    /// that ends at <paramref name="End"/> when it has one.
    /// </summary>
    public sealed record Initialize(int Command, TimeSpan Start, TimeSpan? End) : CommandInput(Command);

    /// <summary>Start the run behind closed gates, and wait for its first frame.</summary>
    /// <param name="Command">The shell's number for the command.</param>
    /// <param name="ClockPosition">Where the position clock stands, read when the warm-up arrived.</param>
    public sealed record WarmUp(int Command, TimeSpan ClockPosition) : CommandInput(Command);

    /// <summary>Play: the first play, or a resume.</summary>
    /// <param name="Command">The shell's number for the command.</param>
    /// <param name="ClockPosition">Where the position clock stands, read when the play arrived.</param>
    /// <param name="ClockPaused">Whether the position clock is paused, read when the play arrived.</param>
    public sealed record Play(int Command, TimeSpan ClockPosition, bool ClockPaused) : CommandInput(Command);

    /// <summary>Pause.</summary>
    public sealed record Pause(int Command) : CommandInput(Command);

    /// <summary>Seek.</summary>
    /// <param name="Command">The shell's number for the command.</param>
    /// <param name="Target">Where to seek to.</param>
    /// <param name="ClockPaused">
    /// Whether the position clock is paused, read when the seek arrived. A seek on an item that is
    /// playing plays on from the target; one on an item that is paused, or has not played, holds.
    /// </param>
    public sealed record Seek(int Command, TimeSpan Target, bool ClockPaused) : CommandInput(Command);

    /// <summary>Rewind in place to the start of the item the runtime now plays.</summary>
    /// <param name="Command">The shell's number for the command.</param>
    /// <param name="Position">The item's start, in the source's time.</param>
    /// <param name="End">Where the item's runs stop, or null when it plays to the end of the source.</param>
    /// <param name="ClockPaused">Whether the position clock is paused, read when the rewind arrived.</param>
    public sealed record Rewind(int Command, TimeSpan Position, TimeSpan? End, bool ClockPaused)
        : CommandInput(Command);

    /// <summary>
    /// The caller of <paramref name="Command"/> has cancelled it. It abandons the caller's wait,
    /// not the runtime's stop.
    /// </summary>
    public sealed record CallerCancelled(int Command) : ItemRuntimeInput;

    /// <summary>A runtime prepared ahead becomes the current item, and acts on what it shares from here.</summary>
    public sealed record Attach : ItemRuntimeInput;

    /// <summary>
    /// Dispose. The shell steps it once, however many callers dispose: the others await the first.
    /// </summary>
    public sealed record Dispose : ItemRuntimeInput;

    // ── Fed back by the shell ───────────────────────────────────────────────

    /// <summary>The open succeeded, holding <paramref name="Resources"/>.</summary>
    /// <param name="Resources">
    /// What the runtime holds after the open: what it allocated, and the wallclock it was
    /// constructed with unless the open replaced it with the audio sink's clock and freed it.
    /// </param>
    /// <param name="Duration">The source's duration, or zero when it is not known.</param>
    public sealed record Opened(ItemResources Resources, TimeSpan Duration) : ItemRuntimeInput;

    /// <summary>
    /// The awaited action has finished. The success of an open is <see cref="Opened"/>, and an Ok
    /// here for an open is not one.
    /// </summary>
    /// <param name="Outcome">Whether it succeeded, failed, or was cancelled through the command's token.</param>
    /// <param name="Error">The failure or the cancellation.</param>
    public sealed record Performed(ItemActionOutcome Outcome, Exception? Error = null) : ItemRuntimeInput;

    /// <summary>A piece of a run's work has ended.</summary>
    /// <param name="Run">The run it belongs to.</param>
    /// <param name="Work">Which piece: one flag.</param>
    /// <param name="Ending">How it ended.</param>
    /// <param name="Error">The fault, when it faulted.</param>
    public sealed record WorkEnded(int Run, ItemWork Work, ItemWorkEnding Ending, Exception? Error = null)
        : ItemRuntimeInput;

    /// <summary>The video decoder decoded its first frame.</summary>
    public sealed record FirstFrameDecoded : ItemRuntimeInput;
}

/// <summary>How an awaited action finished.</summary>
internal enum ItemActionOutcome
{
    Ok,
    Failed,
    Cancelled,
}

/// <summary>How a piece of a run's work ended.</summary>
internal enum ItemWorkEnding
{
    /// <summary>It ran to its end: the pump read the whole source, the graph delivered everything.</summary>
    Completed,

    /// <summary>It ended on the run's cancellation.</summary>
    Cancelled,

    /// <summary>It faulted. The fault has not been reported.</summary>
    Faulted,
}

/// <summary>Why a command was refused without being carried out.</summary>
internal enum ItemRefusal
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>The runtime is disposed or disposing.</summary>
    Disposed,

    /// <summary>
    /// An action of another command is under way. The caller serialises commands, so this is a
    /// caller's error.
    /// </summary>
    Busy,

    /// <summary>The runtime has opened already.</summary>
    AlreadyOpen,

    /// <summary>
    /// A play or a pause of a runtime prepared ahead, before it is attached: either would act on the
    /// position clock and the audio sink it shares with the item that is playing.
    /// </summary>
    Detached,

    /// <summary>The start is at or past the source's known duration.</summary>
    PastTheEnd,
}

/// <summary>How a command finished.</summary>
internal enum ItemCommandOutcome
{
    Ok,
    Cancelled,
    Failed,
    Refused,
}

/// <summary>How a command finished, as the shell hands it back to its caller.</summary>
internal readonly record struct ItemCommandResult(
    ItemCommandOutcome Outcome,
    Exception? Error = null,
    ItemRefusal Refusal = default
)
{
    public static ItemCommandResult Ok { get; } = new(ItemCommandOutcome.Ok);

    public static ItemCommandResult Cancelled { get; } = new(ItemCommandOutcome.Cancelled);

    public static ItemCommandResult Failed(Exception? error) => new(ItemCommandOutcome.Failed, error);

    public static ItemCommandResult Refused(ItemRefusal refusal) =>
        new(ItemCommandOutcome.Refused, Refusal: refusal);
}

/// <summary>An effect <see cref="ItemRuntimeProtocol.Step"/> asks the shell to perform.</summary>
/// <remarks>
/// The awaited actions can fail, and the shell feeds back how each finished. The others are
/// immediate and do not fail, except the teardown's, whose failures the shell collects and the
/// dispose throws (ADR-0086, decision 3.4).
/// </remarks>
internal abstract record ItemRuntimeAction
{
    private ItemRuntimeAction() { }

    // ── Awaited: the shell feeds back Opened or Performed ───────────────────

    /// <summary>
    /// Open the source: allocate the demux session, the decoders, the pipeline and the pacer, and
    /// choose the master clock. One that fails frees what it allocated.
    /// </summary>
    public sealed record Open : ItemRuntimeAction;

    /// <summary>
    /// Start run <paramref name="Run"/>: the tasks of <paramref name="Work"/>, under the run's own
    /// cancellation. It fails, starting nothing, when the graph cannot be built.
    /// </summary>
    /// <param name="Run">The run's number.</param>
    /// <param name="Work">What the run is made of.</param>
    /// <param name="Graph">Whether to build a fresh graph or run the last one again.</param>
    /// <param name="Floor">Where the run starts: nothing presented before it is delivered.</param>
    /// <param name="End">Where the run stops, or null when it plays to the end of the source.</param>
    /// <param name="HoldForSettle">
    /// Whether the pacer holds the destination frame for a <see cref="Settle"/> that follows.
    /// </param>
    public sealed record StartRun(
        int Run,
        ItemWork Work,
        ItemGraphPolicy Graph,
        TimeSpan Floor,
        TimeSpan? End,
        bool HoldForSettle
    ) : ItemRuntimeAction;

    /// <summary>Seek the demuxer to <paramref name="Target"/>.</summary>
    public sealed record SeekDemux(TimeSpan Target) : ItemRuntimeAction;

    /// <summary>Activate the audio sink.</summary>
    public sealed record ActivateAudio : ItemRuntimeAction;

    /// <summary>Pause the audio sink.</summary>
    public sealed record PauseAudio : ItemRuntimeAction;

    /// <summary>Resume the audio sink.</summary>
    public sealed record ResumeAudio : ItemRuntimeAction;

    /// <summary>Deactivate the audio sink. Immediate in the teardown, awaited in a reposition.</summary>
    public sealed record DeactivateAudio : ItemRuntimeAction;

    /// <summary>
    /// Wait for the run's destination frame, seat the clocks on it, and release the pacer's hold,
    /// however the wait ends.
    /// </summary>
    public sealed record Settle : ItemRuntimeAction;

    // ── Immediate ───────────────────────────────────────────────────────────

    /// <summary>Cancel run <paramref name="Run"/>. Its work goes on until it observes the cancellation.</summary>
    public sealed record CancelRun(int Run) : ItemRuntimeAction;

    /// <summary>Close the pause gates and suspend the pacer's wait cap.</summary>
    public sealed record CloseGates : ItemRuntimeAction;

    /// <summary>Open the pause gates and arm the pacer's wait cap.</summary>
    public sealed record OpenGates : ItemRuntimeAction;

    /// <summary>Drop the frames the pacer holds from the run before.</summary>
    public sealed record FlushPacer : ItemRuntimeAction;

    /// <summary>
    /// Release the pacer's hold on the current run's destination frame: the run was started to be
    /// settled, and the command that started it ended before it asked for the <see cref="Settle"/>.
    /// </summary>
    public sealed record ReleaseSettle : ItemRuntimeAction;

    /// <summary>Reset the decoders and the pipeline for the seek (ADR-0056).</summary>
    public sealed record ResetDecoders : ItemRuntimeAction;

    /// <summary>
    /// Seat the clocks on <paramref name="Target"/>: the position clock and the master clock's
    /// baseline when <paramref name="Shared"/>, and otherwise only the wallclock the runtime owns,
    /// recording the target for <see cref="AttachClocks"/>.
    /// </summary>
    public sealed record SeatClocks(TimeSpan Target, bool Shared) : ItemRuntimeAction;

    /// <summary>Seat the master clock's baseline on <paramref name="Origin"/>, before a first play.</summary>
    public sealed record SeatBaseline(TimeSpan Origin) : ItemRuntimeAction;

    /// <summary>Start the position clock, and the wallclock the runtime owns, at <paramref name="Origin"/>.</summary>
    public sealed record StartClocks(TimeSpan Origin) : ItemRuntimeAction;

    /// <summary>Pause the position clock, and the wallclock the runtime owns.</summary>
    public sealed record PauseClocks : ItemRuntimeAction;

    /// <summary>Resume the position clock, and the wallclock the runtime owns.</summary>
    public sealed record ResumeClocks : ItemRuntimeAction;

    /// <summary>Stop the position clock.</summary>
    public sealed record StopClock : ItemRuntimeAction;

    /// <summary>Seat the shared clocks where the runtime was positioned while it was detached.</summary>
    public sealed record AttachClocks : ItemRuntimeAction;

    /// <summary>Free <paramref name="Resource"/>: one flag.</summary>
    public sealed record Free(ItemResources Resource) : ItemRuntimeAction;

    /// <summary>
    /// Report the fault of run <paramref name="Run"/>'s <paramref name="Work"/>: a pump's or a
    /// graph's as a worker fault, a recovery walk's as a recoverable error.
    /// </summary>
    public sealed record ReportFault(int Run, ItemWork Work, Exception? Error) : ItemRuntimeAction;

    /// <summary>Raise the end of stream of run <paramref name="Run"/>, the current run.</summary>
    public sealed record RaiseEndOfStream(int Run) : ItemRuntimeAction;

    /// <summary>Hand <paramref name="Result"/> back to the caller of <paramref name="Command"/>.</summary>
    public sealed record CompleteCommand(int Command, ItemCommandResult Result) : ItemRuntimeAction;

    /// <summary>The dispose has finished.</summary>
    public sealed record CompleteDispose : ItemRuntimeAction;
}

/// <summary>What one input asks of the shell.</summary>
/// <param name="Actions">The immediate actions, performed in order.</param>
/// <param name="Awaited">
/// The action performed after them, whose outcome the shell feeds back, or null.
/// </param>
internal sealed record ItemRuntimeStep(ImmutableArray<ItemRuntimeAction> Actions, ItemRuntimeAction? Awaited);
