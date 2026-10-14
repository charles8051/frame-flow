// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Core;

/// <summary>Where an item runtime is in its life (ADR-0086, decision 2).</summary>
internal enum ItemRuntimePhase
{
    /// <summary>Constructed and not opened. It holds the wallclock it was constructed with.</summary>
    New,

    /// <summary>Opening its source.</summary>
    Opening,

    /// <summary>Opened. <see cref="ItemRunStatus"/> says where its run is.</summary>
    Open,

    /// <summary>Its run is stopped, and nothing is freed until the run's work has ended.</summary>
    Disposing,

    /// <summary>Everything it held is freed. It refuses every command.</summary>
    Disposed,
}

/// <summary>Where an open item's run is.</summary>
internal enum ItemRunStatus
{
    /// <summary>
    /// No run since the demuxer and the decoders were last positioned, by the open or by a
    /// reposition that committed. Nothing is pending, and a run may start.
    /// </summary>
    Idle,

    /// <summary>
    /// The current run has started and has not been stopped. Its pump and graph may have ended by
    /// themselves at the end of the stream. A run warmed up and not yet played is running, behind
    /// closed gates.
    /// </summary>
    Running,

    /// <summary>The current run has been stopped, and some of its work has not ended.</summary>
    Stopping,

    /// <summary>
    /// The run has been stopped and its work has ended, but the demuxer and the decoders are where
    /// it left them, or partway through a reposition. A run starts from here only after a
    /// reposition (#714).
    /// </summary>
    Stopped,
}

/// <summary>The pieces of work a run is made of, each a task the shell holds until it ends.</summary>
[Flags]
internal enum ItemWork
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The demux pump, which reads packets into the decoders' queues.</summary>
    Pump = 1 << 0,

    /// <summary>The graph, which decodes and delivers to the sinks, and waits for them to drain.</summary>
    Graph = 1 << 1,

    /// <summary>The lateness-recovery walk, which sets the video decoder's readback and discard level.</summary>
    Recovery = 1 << 2,

    /// <summary>The end of stream of an item with no stream to play, raised off the launching thread.</summary>
    EmptyEnd = 1 << 3,
}

/// <summary>
/// What an item runtime has allocated. The values are in allocation order, so a teardown frees
/// them from the highest down (ADR-0005).
/// </summary>
[Flags]
internal enum ItemResources
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>
    /// The wallclock the runtime owns, made when it is constructed. The open replaces it when the
    /// audio sink masters the clock, and frees it then.
    /// </summary>
    Clock = 1 << 0,

    /// <summary>The demux session.</summary>
    Demux = 1 << 1,

    /// <summary>The video decoder. Made only when there is a video sink.</summary>
    VideoDecoder = 1 << 2,

    /// <summary>The audio decoder. Made only when there is an audio sink.</summary>
    AudioDecoder = 1 << 3,

    /// <summary>The decoding pipeline, which the pump runs on.</summary>
    Pipeline = 1 << 4,

    /// <summary>The select-by-clock pacer in front of the video sink, with its delivery loop.</summary>
    Pacer = 1 << 5,
}

/// <summary>How a run's graph is brought up.</summary>
internal enum ItemGraphPolicy
{
    /// <summary>Build a fresh graph, as a seek and every first launch do.</summary>
    Rebuild,

    /// <summary>Run the graph the last run built again, as a rewind does once one is built.</summary>
    Reuse,
}

/// <summary>Which command an item runtime is carrying out.</summary>
internal enum ItemCommandKind
{
    Initialize,
    WarmUp,
    Play,
    Pause,
    Seek,
    Rewind,
}

/// <summary>
/// Where a command is. Every stage but <see cref="Stopping"/> and
/// <see cref="WaitingForFirstFrame"/> awaits an action the shell performs.
/// </summary>
internal enum ItemCommandStage
{
    /// <summary>Initialize: the open is under way.</summary>
    Opening,

    /// <summary>Pause: the audio sink is pausing.</summary>
    PausingAudio,

    /// <summary>A first play: the audio sink is activating.</summary>
    ActivatingAudio,

    /// <summary>A play or a warm-up: a run is starting.</summary>
    StartingRun,

    /// <summary>A resume: the audio sink is resuming, or activating if it was left inactive.</summary>
    ResumingAudio,

    /// <summary>A warm-up: waiting for the first frame, or for the run's pump or graph to end.</summary>
    WaitingForFirstFrame,

    /// <summary>A reposition: the audio sink is pausing before the run is stopped.</summary>
    RepositionPausingAudio,

    /// <summary>A reposition: waiting for the work of the stopped run to end.</summary>
    Stopping,

    /// <summary>A reposition: the audio sink is deactivating.</summary>
    RepositionDeactivatingAudio,

    /// <summary>A reposition: the demuxer is seeking.</summary>
    RepositionSeekingDemux,

    /// <summary>A reposition that has committed: the audio sink is activating again.</summary>
    RepositionActivatingAudio,

    /// <summary>A reposition that holds: the audio sink, activated again, is pausing.</summary>
    RepositionPausingAgain,

    /// <summary>A reposition that relaunches: the next run is starting.</summary>
    RepositionStartingRun,

    /// <summary>A reposition that relaunches: the audio sink is resuming.</summary>
    RepositionResumingAudio,

    /// <summary>A reposition that relaunches: the clocks are settling on the destination frame.</summary>
    RepositionSettling,
}

/// <summary>A reposition a command is carrying out.</summary>
/// <param name="Target">Where the demuxer seeks to and the clocks are seated.</param>
/// <param name="Graph">How the run it relaunches brings its graph up.</param>
/// <param name="Relaunch">
/// Whether it starts the next run and plays on, as a seek or a rewind of an item that is playing
/// does, or holds and leaves the launch to the next play.
/// </param>
internal readonly record struct ItemReposition(TimeSpan Target, ItemGraphPolicy Graph, bool Relaunch);

/// <summary>The command an item runtime is carrying out, and where it is.</summary>
/// <param name="Id">The shell's number for the command, which its completion carries.</param>
/// <param name="Kind">Which command it is.</param>
/// <param name="Stage">Where it is.</param>
internal sealed record ItemCommand(int Id, ItemCommandKind Kind, ItemCommandStage Stage)
{
    /// <summary>Initialize: where the item starts, in the source's time.</summary>
    public TimeSpan Start { get; init; }

    /// <summary>
    /// A play or a warm-up: where the position clock stood when it arrived. A first play starts
    /// the clocks there, and a run that was stopped is repositioned there.
    /// </summary>
    public TimeSpan ClockPosition { get; init; }

    /// <summary>A play: whether the position clock was paused when it arrived.</summary>
    public bool ClockPaused { get; init; }

    /// <summary>The reposition the command is carrying out, or null.</summary>
    public ItemReposition? Reposition { get; init; }

    /// <summary>
    /// The caller cancelled the command while an action of it was under way. The command goes no
    /// further: it ends when that action does.
    /// </summary>
    public bool Abandoned { get; init; }

    /// <summary>
    /// The run the command started holds its destination frame for a settle that has not been
    /// asked for yet. A command that ends before asking releases the hold.
    /// </summary>
    public bool SettleHeld { get; init; }

    /// <summary>Whether the command is starting a run: the run's work may already be under way.</summary>
    public bool IsStartingRun => Stage is ItemCommandStage.StartingRun or ItemCommandStage.RepositionStartingRun;

    /// <summary>
    /// Whether the command waits for its run's work rather than for an action, so a newer command,
    /// its caller's cancellation or a dispose ends it at once.
    /// </summary>
    public bool IsWaiting => Stage is ItemCommandStage.Stopping or ItemCommandStage.WaitingForFirstFrame;
}

/// <summary>
/// The state <see cref="ItemRuntimeProtocol"/> steps: the runtime's phase, its run and the work
/// the run has pending, what it holds, and the command it is carrying out.
/// </summary>
/// <remarks>
/// Only the current run has work pending. A run starts only once every earlier run's work has
/// ended, so there is never a second run whose work the state would have to hold.
/// </remarks>
internal sealed record ItemRuntimeState
{
    /// <summary>A runtime just constructed, holding its own wallclock.</summary>
    /// <param name="attached">
    /// False for a runtime prepared ahead, which leaves the position clock and the audio sink it
    /// shares with the item that is playing alone until it is attached.
    /// </param>
    /// <param name="latenessRecovery">Whether each run has a lateness-recovery walk.</param>
    public static ItemRuntimeState Initial(bool attached = true, bool latenessRecovery = false) =>
        new()
        {
            Resources = ItemResources.Clock,
            Attached = attached,
            LatenessRecovery = latenessRecovery,
        };

    /// <summary>Where the runtime is in its life.</summary>
    public ItemRuntimePhase Phase { get; init; }

    /// <summary>Where its run is. <see cref="ItemRunStatus.Idle"/> until it opens.</summary>
    public ItemRunStatus Status { get; init; }

    /// <summary>
    /// The number of the current run, or of the run the next start launches. A reposition advances
    /// it once no work of any run is pending, and only then.
    /// </summary>
    public int Run { get; init; }

    /// <summary>
    /// The current run's work that has not ended. Empty while the run is starting: the work started
    /// is <see cref="ItemRuntimeProtocol.WorkOf"/> less <see cref="EndedBeforeStart"/> until the start's
    /// outcome comes back.
    /// </summary>
    public ItemWork Pending { get; init; }

    /// <summary>
    /// The work of the run being started that ended before the start's outcome came back, as the
    /// end of stream of an item with nothing to play, or a pump over a tiny or failing source, can.
    /// </summary>
    public ItemWork EndedBeforeStart { get; init; }

    /// <summary>What the runtime holds.</summary>
    public ItemResources Resources { get; init; }

    /// <summary>Whether each run has a lateness-recovery walk.</summary>
    public bool LatenessRecovery { get; init; }

    /// <summary>
    /// Whether the runtime acts on the position clock and the audio sink it shares with the
    /// playlist. False for a runtime prepared ahead, until it is attached.
    /// </summary>
    public bool Attached { get; init; }

    /// <summary>Whether a first play has completed.</summary>
    public bool Played { get; init; }

    /// <summary>
    /// Whether the audio sink was last activated rather than deactivated. A call that failed is
    /// taken to have changed nothing.
    /// </summary>
    public bool AudioActive { get; init; }

    /// <summary>Whether the video decoder has decoded its first frame. It does so once in its life.</summary>
    public bool FirstFrameSeen { get; init; }

    /// <summary>Whether a run has built a graph that a rewind can run again.</summary>
    public bool GraphBuilt { get; init; }

    /// <summary>
    /// Where the next run starts, once a reposition has committed. Zero, which is no floor, except
    /// while <see cref="ItemRunStatus.Idle"/>.
    /// </summary>
    public TimeSpan Floor { get; init; }

    /// <summary>The end of the item, or null when it plays to the end of the source.</summary>
    public TimeSpan? ItemEnd { get; init; }

    /// <summary>The source's duration, or zero when it is not known.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The command being carried out, or null.</summary>
    public ItemCommand? Command { get; init; }

    /// <summary>
    /// A dispose arrived while an action of the command was under way. The command ends when the
    /// action does, and the dispose begins.
    /// </summary>
    public bool DisposeRequested { get; init; }

    /// <summary>Whether the item has audio to play: an audio decoder, which exists only with a sink.</summary>
    public bool HasAudio => (Resources & ItemResources.AudioDecoder) != 0;

    /// <summary>Whether the item has video to play: a video decoder, which exists only with a sink.</summary>
    public bool HasVideo => (Resources & ItemResources.VideoDecoder) != 0;

    /// <summary>Whether a run has a graph to run. Without one, a run is only its end of stream.</summary>
    public bool HasGraph => HasAudio || HasVideo;

    /// <summary>Whether an action of the command in progress is under way.</summary>
    public bool IsBusy => Command is { IsWaiting: false };

    /// <summary>Whether a run is starting, so its work may already be under way.</summary>
    public bool IsStartingRun => Command is { IsStartingRun: true };
}
