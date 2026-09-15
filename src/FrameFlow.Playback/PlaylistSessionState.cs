// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>What the controller last asked of a playlist session, or that its queue has ended.</summary>
internal enum PlaylistRunState
{
    /// <summary>
    /// Before the first Play: the controller is loading the first item or paused on it. Left once,
    /// so an item a pending skip starts inside that first Play counts as played.
    /// </summary>
    NotStarted,

    /// <summary>After Play.</summary>
    Playing,

    /// <summary>After Pause, or the warm-up of a seek out of <see cref="Ended"/>.</summary>
    Paused,

    /// <summary>
    /// The queue ran out and end-of-stream was reported. The item that ended the queue is kept if it
    /// ended or was skipped without failing, and is not played again until a seek out of Ended. Only
    /// that seek's warm-up leaves Ended: a Play, Pause or Seek the controller dispatched before it saw
    /// the end-of-stream must not replace it, because the controller ends when it does. A Play from
    /// Ended never reaches the session; the controller replays on a new one.
    /// </summary>
    Ended,
}

/// <summary>The item runtime a playlist session holds.</summary>
/// <param name="Generation">
/// The generation the runtime's callbacks are tagged with. A notification from an older generation
/// is from a replaced runtime.
/// </param>
/// <param name="Item">The queue's item the runtime plays.</param>
/// <param name="Played">Whether Play has been called on the runtime.</param>
/// <param name="AwaitsPlay">
/// Whether an advance opened the runtime while paused, so a Play starts it. If that start fails, the
/// item is skipped like one that could not be started.
/// </param>
/// <param name="KnownRun">The run number the runtime reported when its last operation finished.</param>
/// <param name="Info">The runtime's metadata, from its open.</param>
internal sealed record PlaylistItemSlot(
    int Generation,
    PlaylistItem Item,
    bool Played,
    bool AwaitsPlay,
    int KnownRun,
    MediaInfo? Info
)
{
    /// <summary>The item's duration, or <see cref="TimeSpan.Zero"/> when it is not known.</summary>
    public TimeSpan Duration => Info?.Duration ?? TimeSpan.Zero;
}

/// <summary>
/// The state <see cref="PlaylistSessionProtocol"/> steps: what the session holds and, while it
/// handles an input over several steps, where it is.
/// </summary>
internal sealed record PlaylistSessionState
{
    /// <summary>A session before its first input.</summary>
    public static PlaylistSessionState Initial { get; } = new();

    /// <summary>What the controller last asked, or that the queue has ended.</summary>
    public PlaylistRunState Run { get; init; }

    /// <summary>The runtime the session holds, or <see langword="null"/>.</summary>
    public PlaylistItemSlot? Item { get; init; }

    /// <summary>
    /// The newest generation handed to a runtime. It rises with every runtime an advance opens,
    /// including one that fails to open.
    /// </summary>
    public int Generation { get; init; }

    /// <summary>
    /// The generation whose fault was last handled. A faulted runtime is never rewound in place, so a
    /// second fault carrying it is another worker reporting the same failure.
    /// </summary>
    public int LastFaultedGeneration { get; init; } = -1;

    /// <summary>
    /// Set once the session has handed the controller a fatal error. The controller disposes the
    /// session on its way into Error; until then nothing starts another item.
    /// </summary>
    public bool GaveUp { get; init; }

    /// <summary>
    /// Where the session is in the input it is handling, or <see langword="null"/> between inputs.
    /// </summary>
    public PlaylistSessionWork? Work { get; init; }
}

/// <summary>
/// Where a playlist session is in an input it handles over several steps: the awaited action it is
/// waiting on, or the <see cref="PlaylistSessionInput.Continue"/> it expects.
/// </summary>
internal abstract record PlaylistSessionWork
{
    private PlaylistSessionWork() { }

    /// <summary>A command's open of the first item.</summary>
    public sealed record InitializeOpening(int Command, PlaylistItem Item) : PlaylistSessionWork;

    /// <summary>A command's disposal of a first item that failed to open.</summary>
    public sealed record InitializeDiscarding(
        int Command,
        PlaylistItem Item,
        PlaylistSessionInput.Outcome Failure
    ) : PlaylistSessionWork;

    /// <summary>A command's call on the runtime it holds.</summary>
    public sealed record ItemCommand(int Command, PlaylistItemCommand Kind) : PlaylistSessionWork;

    /// <summary>The disposal of the runtime when the session is disposed.</summary>
    public sealed record Disposing : PlaylistSessionWork;

    /// <summary>The in-place rewind of an item that repeats.</summary>
    public sealed record Replaying(PlaylistAdvanceRun Advance, PlaylistQueue.NextDecision Decision)
        : PlaylistSessionWork;

    /// <summary>The pause of a skipped item that ends the queue and is kept.</summary>
    public sealed record EndPausing(PlaylistAdvanceRun Advance) : PlaylistSessionWork;

    /// <summary>The disposal of a skipped item whose pause failed at the end of the queue.</summary>
    public sealed record EndDiscarding(PlaylistAdvanceRun Advance) : PlaylistSessionWork;

    /// <summary>The disposal of the item an advance replaces.</summary>
    public sealed record DisposingOld(
        PlaylistAdvanceRun Advance,
        PlaylistQueue.NextDecision Pending
    ) : PlaylistSessionWork;

    /// <summary>An advance's open of the item it took.</summary>
    public sealed record Opening(PlaylistAdvanceRun Advance, PlaylistQueue.NextDecision Pending)
        : PlaylistSessionWork;

    /// <summary>An advance's warm-up of the item it opened.</summary>
    public sealed record Warming(PlaylistAdvanceRun Advance, PlaylistQueue.NextDecision Pending)
        : PlaylistSessionWork;

    /// <summary>An advance's play of the item it opened, while the controller is playing.</summary>
    public sealed record Starting(PlaylistAdvanceRun Advance, PlaylistQueue.NextDecision Pending)
        : PlaylistSessionWork;

    /// <summary>The disposal of an item an advance could not open, warm or start.</summary>
    public sealed record DiscardingFailedStart(
        PlaylistAdvanceRun Advance,
        PlaylistItem Item,
        PlaylistSessionInput.Outcome Failure
    ) : PlaylistSessionWork;

    /// <summary>
    /// An advance has started its item and raised the transition. The next step reads the queue
    /// again, so a jump a transition subscriber requested is taken in the same advance.
    /// </summary>
    public sealed record Started(PlaylistAdvanceRun Advance) : PlaylistSessionWork;
}

/// <summary>A command that calls the runtime the session holds.</summary>
internal enum PlaylistItemCommand
{
    WarmUp,
    Play,
    Pause,
    Seek,
    Rewind,
}

/// <summary>One pass of an advance.</summary>
/// <param name="Command">The command to complete when the advance ends, if a command started it.</param>
/// <param name="Playing">Whether the controller was playing when the pass began.</param>
internal sealed record PlaylistAdvanceRun(int? Command, bool Playing);

/// <summary>What the shell knows at the moment it calls the core, and the core does not own.</summary>
/// <param name="Disposing">Whether the session is being disposed.</param>
internal readonly record struct PlaylistStepContext(bool Disposing);
