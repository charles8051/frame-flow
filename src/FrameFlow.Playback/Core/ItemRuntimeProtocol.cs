// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace FrameFlow.Playback.Core;

/// <summary>
/// The item runtime's decisions as a pure step function (ADR-0086, decision 2): a state and one
/// input give the next state and the actions the shell must perform. It owns no IO, no
/// <c>await</c>, no clock, no lock and no mutable state.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule it exists for.</b> Seeking the demuxer, resetting the decoders, starting a run and
/// freeing a resource are asked for only from a state in which no work of any run is pending. The
/// run number advances only once the stopped run's work has ended, so an end of stream a run raises
/// carries its own number. A run starts only over decoders the open or a reposition left clean, so
/// none decodes on codec state a cancellation interrupted (#714).
/// </para>
/// <para>
/// <b>The shell.</b> It steps one input at a time. It performs a step's immediate actions in order,
/// then its awaited action, if any, with the command's token, and feeds back how that finished:
/// <see cref="ItemRuntimeInput.Opened"/> for an open that succeeded, and otherwise
/// <see cref="ItemRuntimeInput.Performed"/>. It starts each run's work as one task per
/// <see cref="ItemWork"/> flag, holds each until it ends, and feeds back its end with the run's
/// number. A completion or a command may be stepped while an awaited action is under way: a
/// completion is taken in, a dispose waits for the action, and any other command is refused as
/// <see cref="ItemRefusal.Busy"/>. The caller serialises its commands, so none is refused in practice.
/// A run's work is under way from the moment the shell starts it, so a piece that ends before the
/// start's outcome is stepped, its fault or its end of stream with it, is taken in then.
/// </para>
/// <para>
/// <b>A reposition.</b> A seek, a rewind, and a play, warm-up or open that has to position first
/// close the gates and pause the audio sink of an item that has played, then stop the run and wait
/// for every piece of its work to end. Only then does the run number advance, the pacer flush, the
/// audio sink deactivate and the demuxer seek. Once the seek has succeeded the decoders are reset and the clocks seated, and
/// the reposition has committed. One on an item that is playing then relaunches: it activates the
/// audio sink, starts the next run, opens the gates, resumes the audio sink and settles the clocks
/// on the destination frame; one that ends before its settle releases the hold its run took on that
/// frame. One on an item that is paused, or has not played, holds, and leaves the
/// launch to the next play; one that holds an item that has played pauses the clocks with the
/// gates, as a pause does, so the runtime's own wallclock does not run on from the target while the
/// item waits (#739). A seek and a play build a fresh graph; a rewind runs the last one again
/// once one is built. Every path cancels a run that is still running and waits for its work: one
/// rule, where the runtime used to choose between cancelling a run and waiting for it to end.
/// </para>
/// <para>
/// <b>Superseding commands coalesce.</b> A command waiting for work, a stop or a first frame, is
/// superseded by the next command: it completes Cancelled, a seek's target is dropped, and the newer
/// command decides from the same stop. A scrub therefore costs one unwind of the old run and one seek
/// to the last target.
/// </para>
/// <para>
/// <b>A caller's cancellation is the caller's.</b> It ends the caller's command, Cancelled, and
/// leaves the stop going. A command cancelled while it waits for a stop ends at once, and the
/// runtime is <see cref="ItemRunStatus.Stopped"/> once the work has ended. One cancelled while an
/// action is under way goes no further, and ends when the action does, which the token passed to the
/// action bounds; so a caller that serialises its commands never finds an action under way. What
/// goes with that action still follows it, so the runtime rests playing, held behind the gates with
/// its clocks, or with its run stopped: a reposition cut after its audio pause stops its run, a
/// relaunch cut after its run started opens the gates over it, and a play cut after its run started
/// holds it with the clocks. The next play, or a warm-up, repositions at the position clock before
/// it starts a run (#714).
/// </para>
/// <para>
/// <b>The audio comes first.</b> A pause, and a reposition, pause the audio sink before they shut
/// the gates; a resume resumes it before it runs the clocks and opens the gates. So a call to the
/// sink that fails leaves the picture where the sound is, and one that goes through is followed by
/// the gates and the clocks that go with it, even when its command goes no further. An activation
/// needs nothing to follow it: the sink plays only what reaches it through the gates.
/// </para>
/// <para>
/// <b>A runtime prepared ahead</b> acts on nothing it shares with the item that is playing until it
/// is attached: it refuses a play and a pause, opens no gate, and seats only its own clock. It is
/// opened, positioned, warmed up and disposed.
/// </para>
/// <para>
/// <b>A fault ends the item.</b> A pump's or a graph's fault, and one from outside the run's work
/// (the pacer's delivery, or an immediate action that threw), are one input here (#824). The first
/// is reported, and the run is stopped with it; no later fault is reported, and no end of stream is
/// raised, so the owner hears the fault and never an end that follows it. The run stops at once, or,
/// while an action of the command in progress is under way, as soon as none is. The command goes on
/// as it would have: its caller hears how it went, and the owner hears of the fault. A
/// lateness-recovery walk's fault is not the item's: it is reported, and the run plays on.
/// </para>
/// <para>
/// <b>Disposal.</b> The dispose opens the gates of an attached runtime, cancels the run and waits
/// for its work. Then it frees the pacer, deactivates the audio sink of an attached runtime or of
/// one that activated it, stops the position clock of an attached runtime, and frees the rest in
/// reverse allocation order. A command waiting for work completes Cancelled; one with an action
/// under way has it cut short, ends when the action does, and the dispose begins then. A disposing or disposed
/// runtime refuses every command.
/// </para>
/// </remarks>
internal static class ItemRuntimeProtocol
{
    /// <summary>Steps <paramref name="input"/>.</summary>
    public static (ItemRuntimeState State, ItemRuntimeStep Step) Step(ItemRuntimeState state, ItemRuntimeInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);

        var stepper = new Stepper(state);
        stepper.Handle(input);
        stepper.StopIfOwed();
        return (stepper.State, stepper.Build());
    }

    /// <summary>The work a run of an item in <paramref name="state"/> is made of.</summary>
    public static ItemWork WorkOf(ItemRuntimeState state) =>
        state.HasGraph
            ? ItemWork.Pump | ItemWork.Graph | (state.LatenessRecovery ? ItemWork.Recovery : ItemWork.None)
            : ItemWork.EmptyEnd;

    // The frees of a teardown after the pacer's, in reverse allocation order.
    private static readonly ItemResources[] FreedAfterThePacer =
    [
        ItemResources.Pipeline,
        ItemResources.AudioDecoder,
        ItemResources.VideoDecoder,
        ItemResources.Demux,
        ItemResources.Clock,
    ];

    /// <summary>
    /// Builds one step. Local to a single call of <see cref="Step"/>, so the function stays pure.
    /// </summary>
    private sealed class Stepper(ItemRuntimeState state)
    {
        private readonly ImmutableArray<ItemRuntimeAction>.Builder _actions =
            ImmutableArray.CreateBuilder<ItemRuntimeAction>();
        private ItemRuntimeAction? _awaited;

        public ItemRuntimeState State { get; private set; } = state;

        public ItemRuntimeStep Build() => new(_actions.ToImmutable(), _awaited);

        // The command in progress. Read only where one is.
        private ItemCommand Command => State.Command!;

        public void Handle(ItemRuntimeInput input)
        {
            switch (input)
            {
                case ItemRuntimeInput.CommandInput command:
                    OnCommand(command);
                    break;
                case ItemRuntimeInput.CallerCancelled cancelled:
                    OnCallerCancelled(cancelled.Command);
                    break;
                case ItemRuntimeInput.Attach:
                    OnAttach();
                    break;
                case ItemRuntimeInput.Dispose:
                    OnDispose();
                    break;
                case ItemRuntimeInput.Opened opened:
                    OnOutcome(ItemActionOutcome.Ok, null, opened);
                    break;
                case ItemRuntimeInput.Performed performed:
                    OnOutcome(performed.Outcome, performed.Error, null);
                    break;
                case ItemRuntimeInput.WorkEnded ended:
                    OnWorkEnded(ended);
                    break;
                case ItemRuntimeInput.FirstFrameDecoded:
                    OnFirstFrame();
                    break;
                case ItemRuntimeInput.Faulted faulted:
                    OnItemFault(State.Run, ItemWork.None, faulted.Error);
                    break;
            }
        }

        // ── Commands ────────────────────────────────────────────────────────

        private void OnCommand(ItemRuntimeInput.CommandInput input)
        {
            if (State.Phase is ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed || State.DisposeRequested)
            {
                Complete(input.Command, ItemCommandResult.Refused(ItemRefusal.Disposed));
                return;
            }

            // A play or a pause acts on the position clock and the audio sink, which a runtime
            // prepared ahead shares with the item that is playing until it is attached.
            if (input is ItemRuntimeInput.Play or ItemRuntimeInput.Pause && !State.Attached)
            {
                Complete(input.Command, ItemCommandResult.Refused(ItemRefusal.Detached));
                return;
            }

            if (State.IsBusy)
            {
                Complete(input.Command, ItemCommandResult.Refused(ItemRefusal.Busy));
                return;
            }

            if (input is ItemRuntimeInput.Initialize && State.Phase != ItemRuntimePhase.New)
            {
                Complete(input.Command, ItemCommandResult.Refused(ItemRefusal.AlreadyOpen));
                return;
            }

            // Superseded while it waits for work. The stop it waited for goes on, and the newer
            // command decides from it.
            if (State.Command is { } waiting)
                End(waiting, ItemCommandResult.Cancelled);

            switch (input)
            {
                case ItemRuntimeInput.Initialize initialize:
                    OnInitialize(initialize);
                    break;
                case ItemRuntimeInput.WarmUp warmUp:
                    OnWarmUp(warmUp);
                    break;
                case ItemRuntimeInput.Play play:
                    OnPlay(play);
                    break;
                case ItemRuntimeInput.Pause pause:
                    OnPause(pause);
                    break;
                case ItemRuntimeInput.Seek seek:
                    OnSeek(seek);
                    break;
                case ItemRuntimeInput.Rewind rewind:
                    OnRewind(rewind);
                    break;
            }
        }

        private void OnInitialize(ItemRuntimeInput.Initialize input)
        {
            State = State with { Phase = ItemRuntimePhase.Opening, ItemEnd = input.End };
            Begin(new ItemCommand(input.Command, ItemCommandKind.Initialize, ItemCommandStage.Opening) { Start = input.Start });
            Await(ItemCommandStage.Opening, new ItemRuntimeAction.Open());
        }

        private void OnWarmUp(ItemRuntimeInput.WarmUp input)
        {
            if (State.Phase != ItemRuntimePhase.Open)
            {
                Complete(input.Command, ItemCommandResult.Ok);
                return;
            }

            Begin(
                new ItemCommand(input.Command, ItemCommandKind.WarmUp, ItemCommandStage.StartingRun)
                {
                    ClockPosition = input.ClockPosition,
                }
            );
            ContinueWarmUp();
        }

        private void OnPlay(ItemRuntimeInput.Play input)
        {
            if (State.Phase != ItemRuntimePhase.Open)
            {
                Complete(input.Command, ItemCommandResult.Ok);
                return;
            }

            Begin(
                new ItemCommand(input.Command, ItemCommandKind.Play, ItemCommandStage.StartingRun)
                {
                    ClockPosition = input.ClockPosition,
                    ClockPaused = input.ClockPaused,
                }
            );
            ContinuePlay();
        }

        private void OnPause(ItemRuntimeInput.Pause input)
        {
            Begin(new ItemCommand(input.Command, ItemCommandKind.Pause, ItemCommandStage.PausingAudio));

            // The audio first, so the pause is heard at once.
            if (State.HasAudio)
                Await(ItemCommandStage.PausingAudio, new ItemRuntimeAction.PauseAudio());
            else
                FinishPause();
        }

        private void FinishPause()
        {
            FollowAudio(ItemCommandStage.PausingAudio);
            Finish();
        }

        private void OnSeek(ItemRuntimeInput.Seek input)
        {
            if (State.Phase != ItemRuntimePhase.Open)
            {
                Complete(input.Command, ItemCommandResult.Ok);
                return;
            }

            var command = new ItemCommand(input.Command, ItemCommandKind.Seek, ItemCommandStage.RepositionPausingAudio);
            Begin(command);
            BeginReposition(new ItemReposition(input.Target, ItemGraphPolicy.Rebuild, Relaunch: PlaysOn(input.ClockPaused)));
        }

        private void OnRewind(ItemRuntimeInput.Rewind input)
        {
            if (State.Phase != ItemRuntimePhase.Open)
            {
                Complete(input.Command, ItemCommandResult.Ok);
                return;
            }

            // The runs that follow stop at the new item's end, even when the rewind is refused: the
            // playlist then rebuilds the item, and the rebuilt one fails to start.
            State = State with { ItemEnd = input.End };
            if (input.Position > TimeSpan.Zero && IsPastTheEnd(input.Position))
            {
                Complete(input.Command, ItemCommandResult.Refused(ItemRefusal.PastTheEnd));
                return;
            }

            Begin(new ItemCommand(input.Command, ItemCommandKind.Rewind, ItemCommandStage.RepositionPausingAudio));
            BeginReposition(
                new ItemReposition(
                    input.Position,
                    State.GraphBuilt ? ItemGraphPolicy.Reuse : ItemGraphPolicy.Rebuild,
                    Relaunch: PlaysOn(input.ClockPaused)
                )
            );
        }

        // A reposition plays on only on an item that is playing: one that has played, with its
        // position clock running.
        private bool PlaysOn(bool clockPaused) => State.Played && !clockPaused;

        // The reposition in progress holds an item that has played: its position clock was read
        // paused. It leaves the item as a pause does, with the clocks paused, so the next play runs
        // both from the target. The controller freezes the position clock by itself at the end of
        // the stream, which leaves the runtime's own wallclock running, and a wallclock seated on
        // the target while it runs is ahead of the frames by the time the play comes (#739).
        private bool Holds => State.Played && Command.Reposition is { Relaunch: false };

        private bool IsPastTheEnd(TimeSpan start) => State.Duration > TimeSpan.Zero && start >= State.Duration;

        private void OnCallerCancelled(int id)
        {
            if (State.Command is not { } command || command.Id != id)
                return;

            // Waiting for a stop: the caller's command ends, and the stop goes on.
            if (command.IsWaiting)
            {
                End(command, ItemCommandResult.Cancelled);
                return;
            }

            // An action is under way, and the shell has passed the caller's token to it. The command
            // ends when the action does, whatever its outcome, so the caller's next command finds
            // nothing under way.
            State = State with { Command = command with { Abandoned = true } };
        }

        private void OnAttach()
        {
            if (State.Phase is ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed || State.Attached)
                return;

            State = State with { Attached = true };
            Emit(new ItemRuntimeAction.AttachClocks(State.DetachedOrigin));
        }

        private void OnDispose()
        {
            if (State.Phase is ItemRuntimePhase.Disposing or ItemRuntimePhase.Disposed || State.DisposeRequested)
                return;

            if (State.Command is { } command)
            {
                if (!command.IsWaiting)
                {
                    // The action under way is cut short, and the dispose begins once it has ended.
                    State = State with { DisposeRequested = true };
                    Emit(new ItemRuntimeAction.CutShort(command.Id));
                    return;
                }

                End(command, ItemCommandResult.Cancelled);
            }

            BeginDispose();
        }

        // ── An awaited action's outcome ─────────────────────────────────────

        private void OnOutcome(ItemActionOutcome outcome, Exception? error, ItemRuntimeInput.Opened? opened)
        {
            // Nothing is awaited: a stray outcome changes nothing. Only Opened is an open's success.
            if (State.Command is not { IsWaiting: false } awaiting)
                return;
            if ((opened is not null) != (awaiting.Stage == ItemCommandStage.Opening && outcome == ItemActionOutcome.Ok))
                return;

            var ok = outcome == ItemActionOutcome.Ok;
            TakeEffect(awaiting, ok, opened);
            var command = Command;

            if (!ok)
            {
                // A resume whose audio did not resume, behind shut gates, holds its run with the
                // clocks, which a reposition that failed before it may have left running.
                if (command.Stage == ItemCommandStage.ResumingAudio && !State.GatesOpen)
                    Emit(new ItemRuntimeAction.PauseClocks());
                End(
                    command,
                    outcome == ItemActionOutcome.Cancelled ? ItemCommandResult.Cancelled : ItemCommandResult.Failed(error)
                );
                return;
            }

            // A command whose caller has gone, or that a dispose is waiting for, goes no further. The
            // action that went through still gets what goes with it at once, so the runtime rests
            // playing, held with its clock, or with its run stopped.
            if (command.Abandoned || State.DisposeRequested)
            {
                FollowCutShort(command);
                End(command, ItemCommandResult.Cancelled);
                return;
            }

            Continue();
        }

        // What the awaited action did to the state, whether or not the command goes on.
        private void TakeEffect(ItemCommand command, bool ok, ItemRuntimeInput.Opened? opened)
        {
            switch (command.Stage)
            {
                case ItemCommandStage.Opening:
                    State = ok
                        ? State with
                        {
                            Phase = ItemRuntimePhase.Open,
                            Resources = opened?.Resources ?? State.Resources,
                            Duration = opened?.Duration ?? TimeSpan.Zero,
                        }
                        : State with { Phase = ItemRuntimePhase.New };
                    break;

                case ItemCommandStage.StartingRun or ItemCommandStage.RepositionStartingRun when ok:
                    var relaunch = command.Stage == ItemCommandStage.RepositionStartingRun;
                    var graph = relaunch ? command.Reposition!.Value.Graph : ItemGraphPolicy.Rebuild;

                    // The run's work started with the action, and some of it may have ended already.
                    State = State with
                    {
                        Status = ItemRunStatus.Running,
                        Pending = WorkOf(State) & ~State.EndedBeforeStart,
                        EndedBeforeStart = ItemWork.None,
                        Floor = TimeSpan.Zero,
                        GraphBuilt = State.GraphBuilt || (graph == ItemGraphPolicy.Rebuild && State.HasGraph),
                        Command = command with { SettleHeld = relaunch && Settles },
                    };
                    break;

                case ItemCommandStage.StartingRun or ItemCommandStage.RepositionStartingRun:
                    // A start that fails starts nothing.
                    State = State with { EndedBeforeStart = ItemWork.None };
                    break;

                case ItemCommandStage.ActivatingAudio
                    or ItemCommandStage.ResumingAudio
                    or ItemCommandStage.RepositionActivatingAudio when ok:
                    State = State with { AudioActive = true };
                    break;

                case ItemCommandStage.RepositionDeactivatingAudio when ok:
                    State = State with { AudioActive = false };
                    break;
            }
        }

        // What follows a pause or a resume of the audio sink at once: the gates and the clocks, and
        // for a reposition the stop of its run, with the clocks when it holds. An activation needs
        // nothing, since an activated sink plays only what reaches it through the gates, and nothing
        // does until they open.
        private void FollowAudio(ItemCommandStage stage)
        {
            switch (stage)
            {
                case ItemCommandStage.PausingAudio:
                    CloseGates();
                    Emit(new ItemRuntimeAction.PauseClocks());
                    break;
                case ItemCommandStage.RepositionPausingAudio:
                    CloseGates();
                    if (Holds)
                        Emit(new ItemRuntimeAction.PauseClocks());
                    StopRun();
                    break;
                case ItemCommandStage.ResumingAudio:
                    Emit(new ItemRuntimeAction.ResumeClocks());
                    OpenGates();
                    break;
            }
        }

        // What a command cut short still does after the action that went through. A reposition cut
        // after its audio pause stops its run, as its stop would have, and the next play repositions
        // where the clock stands (#714). A relaunch cut after its run started opens the gates over the
        // sink it activated, with the clock running. A play cut after its run started holds the run
        // behind the gates with the clocks paused, as a pause does, for the next play to resume.
        private void FollowCutShort(ItemCommand command)
        {
            switch (command.Stage)
            {
                case ItemCommandStage.RepositionStartingRun:
                    OpenGates();
                    break;
                case ItemCommandStage.StartingRun when command.Kind == ItemCommandKind.Play:
                    Emit(new ItemRuntimeAction.PauseClocks());
                    break;
                default:
                    FollowAudio(command.Stage);
                    break;
            }
        }

        private void Continue()
        {
            switch (Command.Stage)
            {
                case ItemCommandStage.Opening:
                    AfterOpen();
                    break;
                case ItemCommandStage.PausingAudio:
                    FinishPause();
                    break;
                case ItemCommandStage.ActivatingAudio:
                    AfterFirstPlayActivation();
                    break;
                case ItemCommandStage.StartingRun:
                    if (Command.Kind == ItemCommandKind.WarmUp)
                        WaitForFirstFrame();
                    else if (State.Played)
                        ResumeOverRun();
                    else
                        FinishFirstPlay();
                    break;
                case ItemCommandStage.ResumingAudio:
                    FinishResume();
                    break;
                case ItemCommandStage.RepositionPausingAudio:
                    RepositionCloseAndStop();
                    break;
                case ItemCommandStage.RepositionDeactivatingAudio:
                    RepositionSeek();
                    break;
                case ItemCommandStage.RepositionSeekingDemux:
                    RepositionCommit();
                    break;
                case ItemCommandStage.RepositionActivatingAudio:
                    if (Relaunches)
                        RepositionLaunch();
                    else
                        Await(ItemCommandStage.RepositionPausingAgain, new ItemRuntimeAction.PauseAudio());
                    break;
                case ItemCommandStage.RepositionPausingAgain:
                    RepositionDone();
                    break;
                case ItemCommandStage.RepositionStartingRun:
                    // The sink was activated before the run started, and an activated sink plays, so
                    // the gates open over sound whatever the resume below does.
                    OpenGates();
                    if (State.HasAudio)
                        Await(ItemCommandStage.RepositionResumingAudio, new ItemRuntimeAction.ResumeAudio());
                    else
                        RepositionSettle();
                    break;
                case ItemCommandStage.RepositionResumingAudio:
                    RepositionSettle();
                    break;
                case ItemCommandStage.RepositionSettling:
                    RepositionDone();
                    break;
            }
        }

        // ── Open ────────────────────────────────────────────────────────────

        private void AfterOpen()
        {
            var start = Command.Start;
            if (start <= TimeSpan.Zero)
            {
                Finish();
                return;
            }

            // The item would open with nothing in it and end at once.
            if (IsPastTheEnd(start))
            {
                End(Command, ItemCommandResult.Refused(ItemRefusal.PastTheEnd));
                return;
            }

            // Before the first play a reposition holds: the warm-up that follows decodes from the
            // start (#639).
            BeginReposition(new ItemReposition(start, ItemGraphPolicy.Rebuild, Relaunch: false));
        }

        // ── Warm-up ─────────────────────────────────────────────────────────

        private void ContinueWarmUp()
        {
            // Only video has a cold start worth warming up, and only before the first play.
            if (State.Played || !State.HasVideo)
            {
                Finish();
                return;
            }

            switch (State.Status)
            {
                case ItemRunStatus.Stopping or ItemRunStatus.Stopped:
                    BeginReposition(new ItemReposition(Command.ClockPosition, ItemGraphPolicy.Rebuild, Relaunch: false));
                    break;
                case ItemRunStatus.Idle:
                    AwaitStartRun(ItemCommandStage.StartingRun, ItemGraphPolicy.Rebuild, holdForSettle: false);
                    break;
                default:
                    WaitForFirstFrame();
                    break;
            }
        }

        // The warm-up is over once a frame is waiting at the gate, or the run's pump or graph has
        // ended, a fault among them, so a run that will never decode one does not hold it.
        private void WaitForFirstFrame()
        {
            if (State.FirstFrameSeen || (State.Pending & (ItemWork.Pump | ItemWork.Graph)) == ItemWork.None)
            {
                Finish();
                return;
            }

            SetStage(ItemCommandStage.WaitingForFirstFrame);
        }

        // ── Play ────────────────────────────────────────────────────────────

        private void ContinuePlay()
        {
            var command = Command;

            // A seek cut short left the run stopped. A play repositions where the clock stands
            // before it starts one (#714).
            if (State.Status is ItemRunStatus.Stopping or ItemRunStatus.Stopped)
            {
                BeginReposition(
                    new ItemReposition(command.ClockPosition, ItemGraphPolicy.Rebuild, Relaunch: PlaysOn(command.ClockPaused))
                );
                return;
            }

            if (!State.Played)
            {
                // The master clock's origin is seated before the audio sink activates, so an
                // audio-mastered clock does not rediscover it from the first buffer.
                Emit(new ItemRuntimeAction.SeatBaseline(command.ClockPosition));
                if (State.HasAudio)
                    Await(ItemCommandStage.ActivatingAudio, new ItemRuntimeAction.ActivateAudio());
                else
                    AfterFirstPlayActivation();
                return;
            }

            if (State.Status == ItemRunStatus.Idle)
            {
                AwaitStartRun(ItemCommandStage.StartingRun, ItemGraphPolicy.Rebuild, holdForSettle: false);
                return;
            }

            ResumeOverRun();
        }

        private void AfterFirstPlayActivation()
        {
            // A run the warm-up started is already behind the gates. One started here starts before
            // the clocks, so a play that ends before its run has started leaves them still.
            if (State.Status == ItemRunStatus.Idle)
            {
                State = State with { Floor = Command.ClockPosition };
                AwaitStartRun(ItemCommandStage.StartingRun, ItemGraphPolicy.Rebuild, holdForSettle: false);
                return;
            }

            FinishFirstPlay();
        }

        private void FinishFirstPlay()
        {
            Emit(new ItemRuntimeAction.StartClocks(Command.ClockPosition));
            OpenGates();
            State = State with { Played = true };
            Finish();
        }

        // The audio sink first, as a pause pauses it first: a resume whose audio fails leaves the
        // clocks still and the gates shut, rather than the picture moving in silence.
        private void ResumeOverRun()
        {
            if (!State.HasAudio)
            {
                FinishResume();
                return;
            }

            // A reposition that failed after deactivating the sink left it inactive, and resuming
            // an inactive sink plays nothing.
            Await(
                ItemCommandStage.ResumingAudio,
                State.AudioActive ? new ItemRuntimeAction.ResumeAudio() : new ItemRuntimeAction.ActivateAudio()
            );
        }

        private void FinishResume()
        {
            FollowAudio(ItemCommandStage.ResumingAudio);
            Finish();
        }

        // ── Reposition ──────────────────────────────────────────────────────

        private void BeginReposition(ItemReposition reposition)
        {
            State = State with { Command = Command with { Reposition = reposition } };

            // No audio from before the break is heard, and then no frame reaches a sink. The audio
            // first, so a pause that fails leaves the item playing as it was.
            if (State.HasAudio && State.Played)
                Await(ItemCommandStage.RepositionPausingAudio, new ItemRuntimeAction.PauseAudio());
            else
                RepositionCloseAndStop();
        }

        private void RepositionCloseAndStop()
        {
            FollowAudio(ItemCommandStage.RepositionPausingAudio);
            RepositionStop();
        }

        private void RepositionStop()
        {
            StopRun();
            if (State.Pending != ItemWork.None)
            {
                SetStage(ItemCommandStage.Stopping);
                return;
            }

            RepositionAfterStop();
        }

        // Nothing of any run is pending from here until the next run starts.
        private void RepositionAfterStop()
        {
            State = State with
            {
                Run = State.Run + 1,
                Status = ItemRunStatus.Stopped,
                Floor = TimeSpan.Zero,
            };

            if ((State.Resources & ItemResources.Pacer) != 0)
                Emit(new ItemRuntimeAction.FlushPacer());

            if (State.HasAudio && State.Played)
                Await(ItemCommandStage.RepositionDeactivatingAudio, new ItemRuntimeAction.DeactivateAudio());
            else
                RepositionSeek();
        }

        private void RepositionSeek() =>
            Await(
                ItemCommandStage.RepositionSeekingDemux,
                new ItemRuntimeAction.SeekDemux(Command.Reposition!.Value.Target)
            );

        private void RepositionCommit()
        {
            var reposition = Command.Reposition!.Value;
            Emit(new ItemRuntimeAction.ResetDecoders());
            Emit(new ItemRuntimeAction.SeatClocks(reposition.Target, Shared: State.Attached));
            State = State with
            {
                Status = ItemRunStatus.Idle,
                Floor = reposition.Target,
                DetachedOrigin = State.Attached ? State.DetachedOrigin : reposition.Target,
            };

            // Only an item that has played activated the sink, and only one that has played plays on.
            if (State.HasAudio && State.Played)
                Await(ItemCommandStage.RepositionActivatingAudio, new ItemRuntimeAction.ActivateAudio());
            else if (Relaunches)
                RepositionLaunch();
            else
                RepositionDone();
        }

        // A reposition that would play on starts no run once the item has faulted: it holds, and
        // the owner ends the item. A run started here would decode into the pacer that failed.
        private bool Relaunches => Command.Reposition!.Value.Relaunch && !State.Faulted;

        private void RepositionLaunch() =>
            AwaitStartRun(
                ItemCommandStage.RepositionStartingRun,
                Command.Reposition!.Value.Graph,
                holdForSettle: Settles
            );

        private void RepositionSettle()
        {
            if (!Settles)
            {
                RepositionDone();
                return;
            }

            // The settle releases the hold however it ends.
            State = State with { Command = Command with { SettleHeld = false } };
            Await(ItemCommandStage.RepositionSettling, new ItemRuntimeAction.Settle());
        }

        // Only a wallclock master advances while the decoder walks to the target, so only it is
        // reseated on the destination frame, and only the pacer can hold that frame.
        private bool Settles =>
            (State.Resources & (ItemResources.Pacer | ItemResources.Clock)) == (ItemResources.Pacer | ItemResources.Clock);

        private void RepositionDone()
        {
            State = State with { Command = Command with { Reposition = null } };
            switch (Command.Kind)
            {
                case ItemCommandKind.Play:
                    ContinuePlay();
                    break;
                case ItemCommandKind.WarmUp:
                    ContinueWarmUp();
                    break;
                default:
                    Finish();
                    break;
            }
        }

        // ── Work ────────────────────────────────────────────────────────────

        private void OnWorkEnded(ItemRuntimeInput.WorkEnded ended)
        {
            // Only the current run has work pending. Anything else is not work this runtime holds.
            if (
                ended.Run != State.Run
                || ended.Work is not (ItemWork.Pump or ItemWork.Graph or ItemWork.Recovery or ItemWork.EmptyEnd)
            )
            {
                return;
            }

            if (State.IsStartingRun)
            {
                if ((WorkOf(State) & ~State.EndedBeforeStart & ended.Work) != ItemWork.None)
                    OnWorkEndedBeforeStart(ended);
                return;
            }

            if ((State.Pending & ended.Work) == ItemWork.None)
                return;

            State = State with { Pending = State.Pending & ~ended.Work };

            if (ended.Ending == ItemWorkEnding.Faulted)
                OnWorkFault(ended);

            // A run that played to its end, and that nothing has stopped, has ended. One a stop
            // overtook is not reported: the stop is replacing it. Nor is one of an item that has
            // faulted: its owner is ending it.
            if (
                ended.Work is ItemWork.Graph or ItemWork.EmptyEnd
                && ended.Ending == ItemWorkEnding.Completed
                && State.Status == ItemRunStatus.Running
                && !State.Faulted
            )
            {
                Emit(new ItemRuntimeAction.RaiseEndOfStream(ended.Run));
            }

            if (State.Command is { Stage: ItemCommandStage.WaitingForFirstFrame } && ended.Work is ItemWork.Pump or ItemWork.Graph)
                Finish();

            if (State.Pending != ItemWork.None || State.Status != ItemRunStatus.Stopping)
                return;

            State = State with { Status = ItemRunStatus.Stopped };
            if (State.Phase == ItemRuntimePhase.Disposing)
                FinishDispose();
            else if (State.Command is { Stage: ItemCommandStage.Stopping })
                RepositionAfterStop();
        }

        // Work of a run whose start is still under way has ended. The run is live and nothing has
        // stopped it, so its fault and its end of stream are reported now, as for a running run; the
        // start's outcome takes the work off what is pending.
        private void OnWorkEndedBeforeStart(ItemRuntimeInput.WorkEnded ended)
        {
            State = State with { EndedBeforeStart = State.EndedBeforeStart | ended.Work };

            if (ended.Ending == ItemWorkEnding.Faulted)
                OnWorkFault(ended);
            if (
                ended.Work is ItemWork.Graph or ItemWork.EmptyEnd
                && ended.Ending == ItemWorkEnding.Completed
                && !State.Faulted
            )
            {
                Emit(new ItemRuntimeAction.RaiseEndOfStream(ended.Run));
            }
        }

        // A lateness-recovery walk's fault is recoverable: the walk is an optimisation, and the run
        // plays on. Any other piece's is the item's.
        private void OnWorkFault(ItemRuntimeInput.WorkEnded ended)
        {
            if (ended.Work == ItemWork.Recovery)
                Emit(new ItemRuntimeAction.ReportFault(ended.Run, ended.Work, ended.Error));
            else
                OnItemFault(ended.Run, ended.Work, ended.Error);
        }

        // The item has faulted. Its first fault is reported, and no end of stream follows it (#824).
        // Its run is stopped, so a pump parked on a full queue that a faulted graph no longer
        // drains ends (#713): at once, or, while an action of the command in progress is under way,
        // as soon as none is. The command goes on as it would have, and its caller hears how it
        // went; the owner hears of the fault.
        private void OnItemFault(int run, ItemWork work, Exception? error)
        {
            if (State.Phase == ItemRuntimePhase.Disposed || State.Faulted)
                return;

            State = State with { Faulted = true, StopOwed = true };
            Emit(new ItemRuntimeAction.ReportFault(run, work, error));
        }

        // Stops the run a fault stopped, once no action is under way. A warm-up waiting for a first
        // frame the stopped run will not decode is over, as when the run ends before one.
        public void StopIfOwed()
        {
            if (!State.StopOwed || State.IsBusy)
                return;

            State = State with { StopOwed = false };
            StopRun();
            if (State.Command is { Stage: ItemCommandStage.WaitingForFirstFrame })
                Finish();
        }

        private void OnFirstFrame()
        {
            State = State with { FirstFrameSeen = true };
            if (State.Command is { Stage: ItemCommandStage.WaitingForFirstFrame })
                Finish();
        }

        // Cancels the current run, once: its work goes on until it observes the cancellation.
        private void StopRun()
        {
            if (State.Status != ItemRunStatus.Running)
                return;

            Emit(new ItemRuntimeAction.CancelRun(State.Run));
            State = State with
            {
                Status = State.Pending == ItemWork.None ? ItemRunStatus.Stopped : ItemRunStatus.Stopping,
            };
        }

        private void AwaitStartRun(ItemCommandStage stage, ItemGraphPolicy graph, bool holdForSettle)
        {
            // A run that starts at or past the item's end, after a seek, plays to the end of the source.
            var end = State.ItemEnd is { } itemEnd && State.Floor < itemEnd ? itemEnd : (TimeSpan?)null;
            Await(
                stage,
                new ItemRuntimeAction.StartRun(State.Run, WorkOf(State), graph, State.Floor, end, holdForSettle)
            );
        }

        // ── Disposal ────────────────────────────────────────────────────────

        private void BeginDispose()
        {
            State = State with { Phase = ItemRuntimePhase.Disposing, DisposeRequested = false };

            // So an operator blocked at a gate unblocks and sees the cancellation. A detached runtime
            // keeps them shut: what waits there would reach the sinks it shares with the item playing.
            if (State.Attached)
                OpenGates();

            StopRun();
            if (State.Pending == ItemWork.None)
                FinishDispose();
        }

        private void FinishDispose()
        {
            var resources = State.Resources;

            // The pacer first, now that nothing delivers into it, then what the item shares, then the
            // rest in reverse allocation order.
            if ((resources & ItemResources.Pacer) != 0)
                Emit(new ItemRuntimeAction.Free(ItemResources.Pacer));
            if (State.HasAudio && (State.Attached || State.AudioActive))
                Emit(new ItemRuntimeAction.DeactivateAudio());
            if (State.Attached)
                Emit(new ItemRuntimeAction.StopClock());
            // The rest is the runtime's own, so the playlist need not wait for it to go on (#780).
            Emit(new ItemRuntimeAction.ReleaseShared());
            foreach (var resource in FreedAfterThePacer)
            {
                if ((resources & resource) != 0)
                    Emit(new ItemRuntimeAction.Free(resource));
            }

            State = State with
            {
                Phase = ItemRuntimePhase.Disposed,
                Resources = ItemResources.None,
                AudioActive = false,
            };
            Emit(new ItemRuntimeAction.CompleteDispose());
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private void Begin(ItemCommand command) => State = State with { Command = command };

        private void SetStage(ItemCommandStage stage) =>
            State = State with { Command = Command with { Stage = stage } };

        private void Await(ItemCommandStage stage, ItemRuntimeAction action)
        {
            SetStage(stage);
            _awaited = action;
        }

        private void Emit(ItemRuntimeAction action) => _actions.Add(action);

        private void OpenGates()
        {
            Emit(new ItemRuntimeAction.OpenGates());
            State = State with { GatesOpen = true };
        }

        private void CloseGates()
        {
            Emit(new ItemRuntimeAction.CloseGates());
            State = State with { GatesOpen = false };
        }

        private void Complete(int command, ItemCommandResult result) =>
            Emit(new ItemRuntimeAction.CompleteCommand(command, result));

        private void Finish() => End(Command, ItemCommandResult.Ok);

        // Ends a command, handing its caller the result. A run it started to settle, and did not,
        // would hold its destination frame until the pacer's backstop gave up on it.
        private void End(ItemCommand command, ItemCommandResult result)
        {
            if (command.SettleHeld)
                Emit(new ItemRuntimeAction.ReleaseSettle());
            Complete(command.Id, result);
            State = State with { Command = null };
            StopIfOwed();
            if (State.DisposeRequested)
                BeginDispose();
        }
    }
}
