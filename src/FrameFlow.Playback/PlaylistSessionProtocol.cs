// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// The playlist session's decisions as a pure step function: a state, the queue and one input give
/// the next state, the next queue, and the actions the shell must perform. It owns no IO, no
/// <c>await</c>, no clock, no lock and no mutable state. <see cref="PlaylistSession"/> is its shell.
/// </summary>
/// <remarks>
/// <para>
/// <b>One input at a time.</b> The shell takes an input from its channel and steps it. It performs
/// the step's immediate actions, then awaits the step's awaited action, if any, and steps its
/// <see cref="PlaylistSessionInput.Outcome"/>. A step with no awaited action that is not done is
/// followed by <see cref="PlaylistSessionInput.Continue"/>. The input is handled when a step is done,
/// and only then does the shell take the next input. An advance therefore runs from its take to its
/// settled item, including the jumps taken on the way, with no other input in between.
/// </para>
/// <para>
/// <b>An advance follows the controller.</b> The next item plays only while the controller is
/// playing. While it is paused, the next item is opened and warmed and waits for Play. Nothing
/// advances before the first Play: the advance is latched on the queue for it. Once the queue has
/// ended, advances are dropped.
/// </para>
/// <para>
/// <b>Same-source replay.</b> When the decision is <see cref="PlaylistQueue.NextKind.Replay"/>, the
/// controller is playing and the item has played and not failed, the runtime is rewound in place
/// rather than rebuilt, so the loop seam costs about one frame. If the rewind fails, the item is
/// rebuilt. An item that has not played is always rebuilt: the in-place rewind is built for a loop
/// reached while playing.
/// </para>
/// <para>
/// <b>The end of the queue.</b> An item that ends the queue without failing is kept, so at Ended it
/// can be sought and played again. A skipped item is paused first, so it does not go on presenting.
/// A failed item is disposed.
/// </para>
/// <para>
/// <b>Failures.</b> An item that cannot be opened, warmed or started, or faults while it plays, is
/// reported and skipped. <see cref="PlaylistFailureGuard"/> decides when the session gives up and
/// hands the controller a fatal error instead. A fault before the first Play is the first item
/// failing to start, and goes to the controller as a single source's would.
/// </para>
/// <para>
/// <b>Disposal.</b> While <see cref="PlaylistStepContext.Disposing"/> is set, a command completes as
/// a no-op, a notification is dropped, and an advance ends at its next step. Nothing is reported to
/// the controller.
/// </para>
/// </remarks>
internal static class PlaylistSessionProtocol
{
    /// <summary>Steps <paramref name="input"/>.</summary>
    public static (
        PlaylistSessionState State,
        PlaylistQueue Queue,
        PlaylistSessionStep Step
    ) Step(
        PlaylistSessionState state,
        PlaylistQueue queue,
        PlaylistSessionInput input,
        PlaylistStepContext context
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(input);

        var stepper = new Stepper(state, queue, context);
        stepper.Handle(input);
        return (stepper.State, stepper.Queue, stepper.Build());
    }

    /// <summary>How an item's run came to an end.</summary>
    private enum ItemEnding
    {
        EndOfStream,
        Skip,
        Fault,
        FailedStart,
    }

    /// <summary>
    /// Builds one step. Local to a single call of <see cref="Step"/>, so the function stays pure.
    /// </summary>
    private sealed class Stepper(
        PlaylistSessionState state,
        PlaylistQueue queue,
        PlaylistStepContext context
    )
    {
        private readonly ImmutableArray<PlaylistSessionAction>.Builder _actions =
            ImmutableArray.CreateBuilder<PlaylistSessionAction>();
        private PlaylistSessionAction? _awaited;
        private bool _done;

        public PlaylistSessionState State { get; private set; } = state;

        public PlaylistQueue Queue { get; private set; } = queue;

        private bool Disposing => context.Disposing;

        public PlaylistSessionStep Build() =>
            new(_actions.ToImmutable(), _awaited, _awaited is null && _done);

        public void Handle(PlaylistSessionInput input)
        {
            if (State.Work is { } work)
            {
                switch (input)
                {
                    case PlaylistSessionInput.Outcome outcome:
                        OnOutcome(work, outcome);
                        return;
                    case PlaylistSessionInput.Continue when work is PlaylistSessionWork.Started started:
                        AfterAdvancePass(started.Advance);
                        return;
                    default:
                        // The shell feeds only an outcome or a continue while an input is under way.
                        // A command that arrives anyway fails rather than wait forever.
                        FailUnexpected(input);
                        return;
                }
            }

            switch (input)
            {
                case PlaylistSessionInput.Initialize i:
                    OnInitialize(i.Command);
                    break;
                case PlaylistSessionInput.WarmUp w:
                    OnWarmUp(w.Command);
                    break;
                case PlaylistSessionInput.Play p:
                    OnPlay(p.Command);
                    break;
                case PlaylistSessionInput.Pause p:
                    OnPause(p.Command);
                    break;
                case PlaylistSessionInput.Seek s:
                    OnSeek(s.Command, s.Position);
                    break;
                case PlaylistSessionInput.Rewind r:
                    OnRewind(r.Command);
                    break;
                case PlaylistSessionInput.Dispose:
                    OnDispose();
                    break;
                case PlaylistSessionInput.EndOfStream e:
                    OnEndOfStream(e.Generation, e.Run);
                    break;
                case PlaylistSessionInput.Fault f:
                    OnFault(f.Generation, f.Error, f.PlayedFor);
                    break;
                case PlaylistSessionInput.SkipRequested s:
                    OnSkipRequested(s.Generation, s.RunAtRequest);
                    break;
                case PlaylistSessionInput.JumpRequested:
                    OnJumpRequested();
                    break;
                default:
                    // An outcome or continue with nothing under way means nothing.
                    Done();
                    break;
            }
        }

        // ── Commands ────────────────────────────────────────────────────────

        private void OnInitialize(int command)
        {
            if (Disposing)
            {
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            // The queue is the authority. On the first load it gives the source the controller was
            // given; on a replay from Ended, the item the replay reserved.
            var (queue, item) = Queue.TakeStart();
            Queue = queue;
            if (item is null)
            {
                Complete(
                    command,
                    PlaylistCommandResult.Failed(
                        new InvalidOperationException("The playlist has nothing queued to play.")
                    )
                );
                return;
            }

            Await(
                new PlaylistSessionAction.OpenItem(State.Generation, item.Source, command),
                new PlaylistSessionWork.InitializeOpening(command, item)
            );
        }

        private void OnWarmUp(int command)
        {
            if (Disposing)
            {
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            if (State.Item is not null)
            {
                Await(
                    new PlaylistSessionAction.WarmUpItem(command),
                    new PlaylistSessionWork.ItemCommand(command, PlaylistItemCommand.WarmUp)
                );
                return;
            }

            LeaveEndedAfterWarmUp();
            Complete(command, PlaylistCommandResult.Ok);
        }

        private void OnPlay(int command)
        {
            // A Play at Ended was dispatched before the controller saw the end-of-stream on its
            // way, and the controller ends when it does.
            if (Disposing || State.Run == PlaylistRunState.Ended)
            {
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            State = State with { Run = PlaylistRunState.Playing };

            // A skip or end-of-stream latched before this Play, or a jump waiting for it, takes
            // effect now. A jump supersedes a latched skip, so both are consumed.
            var (queue, latched) = Queue.ConsumeLatchedAdvance();
            Queue = queue;
            if (Queue.HasPendingJump || latched is not null)
            {
                AdvancePass(
                    command,
                    latched == PlaylistAdvance.EndOfStream && !Queue.HasPendingJump
                        ? ItemEnding.EndOfStream
                        : ItemEnding.Skip
                );
                return;
            }

            if (State.Item is null)
            {
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            Await(
                new PlaylistSessionAction.PlayItem(command),
                new PlaylistSessionWork.ItemCommand(command, PlaylistItemCommand.Play)
            );
        }

        private void OnPause(int command)
        {
            if (Disposing)
            {
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            // Only a playing session pauses. At Ended the controller is about to end on the
            // end-of-stream already on its way.
            if (State.Run == PlaylistRunState.Playing)
                State = State with { Run = PlaylistRunState.Paused };

            if (State.Item is null)
            {
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            Await(
                new PlaylistSessionAction.PauseItem(command),
                new PlaylistSessionWork.ItemCommand(command, PlaylistItemCommand.Pause)
            );
        }

        private void OnSeek(int command, TimeSpan position)
        {
            // A seek out of Ended has already warmed up, which left Ended. A seek at Ended was
            // dispatched before the controller saw the end-of-stream, and must not start the kept
            // item.
            if (Disposing || State.Item is null || State.Run == PlaylistRunState.Ended)
            {
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            Await(
                new PlaylistSessionAction.SeekItem(position, command),
                new PlaylistSessionWork.ItemCommand(command, PlaylistItemCommand.Seek)
            );
        }

        private void OnRewind(int command)
        {
            if (Disposing || State.Item is null)
            {
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            Await(
                new PlaylistSessionAction.RewindItem(command),
                new PlaylistSessionWork.ItemCommand(command, PlaylistItemCommand.Rewind)
            );
        }

        private void OnDispose()
        {
            if (State.Item is null)
            {
                Done();
                return;
            }

            State = State with { Item = null };
            Await(new PlaylistSessionAction.DisposeItem(), new PlaylistSessionWork.Disposing());
        }

        // ── Notifications and requests ──────────────────────────────────────

        private void OnEndOfStream(int generation, int run)
        {
            if (Disposing || State.GaveUp || generation != State.Generation)
            {
                Done();
                return;
            }

            // The end-of-stream came from a run that a seek or rewind has since replaced. The item
            // is playing again, so acting on it would end the item that was just sought.
            if (State.Item is not { } item || run != item.KnownRun)
            {
                Emit(
                    new PlaylistSessionAction.Log(
                        PlaylistSessionLog.StaleEndOfStream,
                        Run: run,
                        CurrentRun: State.Item?.KnownRun ?? -1
                    )
                );
                Done();
                return;
            }

            switch (State.Run)
            {
                case PlaylistRunState.Ended:
                    Emit(new PlaylistSessionAction.Log(PlaylistSessionLog.AdvanceIgnoredAtEnd));
                    Done();
                    return;
                case PlaylistRunState.NotStarted:
                    // Nothing has played. The advance waits for the first Play, and keeps why.
                    Queue = Queue.LatchAdvance(PlaylistAdvance.EndOfStream);
                    Done();
                    return;
            }

            AdvancePass(command: null, ItemEnding.EndOfStream);
        }

        private void OnFault(int generation, Exception error, TimeSpan playedFor)
        {
            if (Disposing || State.GaveUp || generation != State.Generation)
            {
                Done();
                return;
            }

            if (State.Run == PlaylistRunState.Ended)
            {
                Emit(
                    new PlaylistSessionAction.Log(
                        PlaylistSessionLog.AdvanceIgnoredAtEnd,
                        Faulted: true
                    )
                );
                Done();
                return;
            }

            if (generation == State.LastFaultedGeneration)
            {
                Done();
                return;
            }
            State = State with { LastFaultedGeneration = generation };

            var source = State.Item?.Item.Source.DisplayName ?? "(unknown)";

            if (State.Run == PlaylistRunState.NotStarted)
            {
                // Nothing has played, so this is the first item failing to start, as a single
                // source's would.
                State = State with { GaveUp = true };
                Emit(new PlaylistSessionAction.ReportFatal(error));
                Done();
                return;
            }

            Emit(
                new PlaylistSessionAction.ReportItemFailed(
                    source,
                    PlaylistItemFailure.FaultedDuringPlayback,
                    error
                )
            );

            var (queue, giveUp) = Queue.ItemFailed(playedFor, State.Item?.Duration ?? TimeSpan.Zero);
            Queue = queue;
            if (giveUp)
            {
                GiveUp(error);
                Done();
                return;
            }

            AdvancePass(command: null, ItemEnding.Fault);
        }

        private void OnSkipRequested(int generation, PlaylistRunState runAtRequest)
        {
            if (Disposing)
            {
                // A skip requested before the first Play waits on the queue, where the next
                // session's first Play takes it, as it would have if it had been latched at once.
                if (
                    runAtRequest == PlaylistRunState.NotStarted
                    && State.Run == PlaylistRunState.NotStarted
                )
                {
                    Queue = Queue.LatchAdvance(PlaylistAdvance.Skip);
                }
                Done();
                return;
            }

            // A skip requested once the queue had ended has nothing to end, even if a seek out of
            // Ended has warmed up since. Play from Ended plays whatever is queued.
            if (runAtRequest == PlaylistRunState.Ended)
            {
                Emit(new PlaylistSessionAction.Log(PlaylistSessionLog.AdvanceIgnoredAtEnd));
                Done();
                return;
            }

            // Tagged with the generation current at the request, so a skip and a natural
            // end-of-stream that race collapse into one advance. A skip is never stale by run: a
            // seek between the request and the advance does not cancel it.
            if (State.GaveUp || generation != State.Generation)
            {
                Done();
                return;
            }

            switch (State.Run)
            {
                case PlaylistRunState.Ended:
                    Emit(new PlaylistSessionAction.Log(PlaylistSessionLog.AdvanceIgnoredAtEnd));
                    Done();
                    return;
                case PlaylistRunState.NotStarted:
                    // Nothing has played: the skip takes effect when the first Play does.
                    Queue = Queue.LatchAdvance(PlaylistAdvance.Skip);
                    Done();
                    return;
            }

            AdvancePass(command: null, ItemEnding.Skip);
        }

        private void OnJumpRequested()
        {
            // The jump waits on the queue. At Ended and before the first Play, the next Play, or
            // the replay the controller loads, takes it. A jump an advance has already taken
            // leaves nothing pending.
            if (
                Disposing
                || State.GaveUp
                || !Queue.HasPendingJump
                || State.Run is PlaylistRunState.Ended or PlaylistRunState.NotStarted
            )
            {
                Done();
                return;
            }

            AdvancePass(command: null, ItemEnding.Skip);
        }

        // ── Outcomes ────────────────────────────────────────────────────────

        private void OnOutcome(PlaylistSessionWork work, PlaylistSessionInput.Outcome outcome)
        {
            State = State with { Work = null };

            switch (work)
            {
                case PlaylistSessionWork.InitializeOpening w:
                    OnInitializeOpened(w, outcome);
                    break;
                case PlaylistSessionWork.InitializeDiscarding w:
                    OnInitializeDiscarded(w);
                    break;
                case PlaylistSessionWork.ItemCommand w:
                    OnItemCommandFinished(w, outcome);
                    break;
                case PlaylistSessionWork.Disposing:
                    Done();
                    break;
                case PlaylistSessionWork.Replaying w:
                    OnReplayed(w, outcome);
                    break;
                case PlaylistSessionWork.EndPausing w:
                    OnEndPaused(w, outcome);
                    break;
                case PlaylistSessionWork.EndDiscarding w:
                    if (Disposing)
                        EndAdvance(w.Advance);
                    else
                        EndQueue(w.Advance);
                    break;
                case PlaylistSessionWork.DisposingOld w:
                    // The clock stops even while disposing; the open that would follow does not.
                    AfterOldDisposed(w.Advance, w.Pending);
                    break;
                case PlaylistSessionWork.Opening w:
                    OnOpened(w, outcome);
                    break;
                case PlaylistSessionWork.Warming w:
                    OnWarmed(w, outcome);
                    break;
                case PlaylistSessionWork.Starting w:
                    OnStartFinished(w, outcome);
                    break;
                case PlaylistSessionWork.DiscardingFailedStart w:
                    OnFailedStartDiscarded(w);
                    break;
                case PlaylistSessionWork.Started started:
                    // A started pass awaits a continue, not an outcome. End it, completing its
                    // command.
                    EndAdvance(started.Advance);
                    break;
                default:
                    Done();
                    break;
            }
        }

        private void OnInitializeOpened(
            PlaylistSessionWork.InitializeOpening work,
            PlaylistSessionInput.Outcome outcome
        )
        {
            if (outcome.Kind != PlaylistOutcome.Ok)
            {
                Await(
                    new PlaylistSessionAction.DisposeItem(),
                    new PlaylistSessionWork.InitializeDiscarding(work.Command, work.Item, outcome)
                );
                return;
            }

            State = State with
            {
                Item = new PlaylistItemSlot(
                    State.Generation,
                    work.Item,
                    Played: false,
                    AwaitsPlay: false,
                    outcome.Run,
                    outcome.Info
                ),
            };

            if (!Disposing)
            {
                Emit(new PlaylistSessionAction.AttachToCoordinator());
                var (queue, index) = Queue.ReportCurrent(work.Item, outcome.Info);
                Queue = queue;
                Emit(
                    new PlaylistSessionAction.RaiseTransition(
                        work.Item,
                        outcome.Info,
                        index,
                        Wrapped: false
                    )
                );
            }

            Complete(work.Command, PlaylistCommandResult.Ok);
        }

        private void OnInitializeDiscarded(PlaylistSessionWork.InitializeDiscarding work)
        {
            var failure = work.Failure;

            // Once the player has started any item, a new session's first item that cannot be
            // opened is passed over as an advance passes over one. Only the player's very first
            // item fails the load, like a single source's. A cancelled open is not passed over.
            if (failure.Kind != PlaylistOutcome.Failed || !Queue.AnyStarted || Disposing)
            {
                Complete(work.Command, PlaylistCommandResult.From(failure));
                return;
            }

            Emit(
                new PlaylistSessionAction.ReportItemFailed(
                    work.Item.Source.DisplayName,
                    PlaylistItemFailure.CouldNotStart,
                    failure.Error
                )
            );

            var (queue, giveUp) = Queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero);
            Queue = queue;
            if (giveUp)
            {
                Complete(work.Command, PlaylistCommandResult.Failed(GiveUpException(failure.Error)));
                return;
            }

            var (next, decision) = Queue.DecideNext(PlaylistAdvance.FailedStart);
            Queue = next;
            if (decision.Kind == PlaylistQueue.NextKind.End)
            {
                Complete(
                    work.Command,
                    PlaylistCommandResult.Failed(
                        new InvalidOperationException("No playlist item could be opened.", failure.Error)
                    )
                );
                return;
            }

            State = State with { Generation = State.Generation + 1 };
            Await(
                new PlaylistSessionAction.OpenItem(
                    State.Generation,
                    decision.Item!.Source,
                    work.Command
                ),
                new PlaylistSessionWork.InitializeOpening(work.Command, decision.Item)
            );
        }

        private void OnItemCommandFinished(
            PlaylistSessionWork.ItemCommand work,
            PlaylistSessionInput.Outcome outcome
        )
        {
            KeepRun(outcome);

            // A command still running when disposal began completes as a no-op, as one that
            // arrives during disposal does.
            if (Disposing)
            {
                Complete(work.Command, PlaylistCommandResult.Ok);
                return;
            }

            switch (work.Kind)
            {
                case PlaylistItemCommand.WarmUp when outcome.Kind == PlaylistOutcome.Ok:
                    LeaveEndedAfterWarmUp();
                    break;

                case PlaylistItemCommand.Play when outcome.Kind == PlaylistOutcome.Ok:
                    if (State.Item is { } played)
                        State = State with { Item = played with { Played = true, AwaitsPlay = false } };
                    break;

                case PlaylistItemCommand.Play
                    when outcome.Kind == PlaylistOutcome.Failed
                        && State.Item is { AwaitsPlay: true }:
                    // An item an advance opened while paused starts here. Failing to start is what
                    // it would have done inside the advance, so it is handled the same way. A
                    // cancelled Play gets the cancellation, and the item stays.
                    ItemFailedToStart(work.Command, outcome.Error);
                    return;
            }

            Complete(work.Command, PlaylistCommandResult.From(outcome));
        }

        private void ItemFailedToStart(int command, Exception? error)
        {
            Emit(
                new PlaylistSessionAction.ReportItemFailed(
                    State.Item!.Item.Source.DisplayName,
                    PlaylistItemFailure.CouldNotStart,
                    error
                )
            );

            var (queue, giveUp) = Queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero);
            Queue = queue;
            if (giveUp)
            {
                GiveUp(error);
                Complete(command, PlaylistCommandResult.Ok);
                return;
            }

            AdvancePass(command, ItemEnding.FailedStart);
        }

        // ── Advance ─────────────────────────────────────────────────────────

        /// <summary>One pass of an advance: takes the next item, and settles it or ends the queue.</summary>
        private void AdvancePass(int? command, ItemEnding how)
        {
            // A fault and a failed start both leave a runtime that is not replayed in place or kept.
            var failed = how is ItemEnding.Fault or ItemEnding.FailedStart;

            // An item that reached its end or was skipped ended without failing.
            if (!failed)
                Queue = Queue.ItemEnded();

            var advance = new PlaylistAdvanceRun(command, State.Run == PlaylistRunState.Playing);

            // Decide what plays next before any teardown, so a same-source replay can reuse the live
            // runtime instead of rebuilding it.
            var (queue, decision) = Queue.DecideNext(ToAdvance(how));
            Queue = queue;

            if (
                advance.Playing
                && State.Item is { Played: true }
                && !failed
                && decision.Kind == PlaylistQueue.NextKind.Replay
            )
            {
                Await(
                    new PlaylistSessionAction.RewindItem(Command: null),
                    new PlaylistSessionWork.Replaying(advance, decision)
                );
                return;
            }

            if (!failed && State.Item is { } item && decision.Kind == PlaylistQueue.NextKind.End)
            {
                // Only a skip needs the pause. An item that reached its end has stopped, and pausing
                // it would hold back the audio still queued on the device. An item that has not
                // played has not opened its gates.
                if (how == ItemEnding.Skip && item.Played)
                {
                    Await(
                        new PlaylistSessionAction.PauseItem(Command: null),
                        new PlaylistSessionWork.EndPausing(advance)
                    );
                    return;
                }

                EndQueue(advance);
                return;
            }

            Rebuild(advance, decision);
        }

        private void OnReplayed(PlaylistSessionWork.Replaying work, PlaylistSessionInput.Outcome outcome)
        {
            KeepRun(outcome);

            if (Disposing)
            {
                EndAdvance(work.Advance);
                return;
            }

            if (outcome.Kind != PlaylistOutcome.Ok)
            {
                Emit(
                    new PlaylistSessionAction.Log(
                        PlaylistSessionLog.ReplayFellBack,
                        Source: State.Item?.Item.Source.DisplayName ?? "(unknown)",
                        Error: outcome.Error
                    )
                );
                Rebuild(work.Advance, work.Decision);
                return;
            }

            // The item may be a different item of the same source, such as a back-to-back
            // duplicate. An in-place replay keeps the runtime, so the controller hears nothing.
            var slot = State.Item!;
            State = State with { Item = slot with { Item = work.Decision.Item! } };
            RaiseStart(work.Advance, work.Decision.Item!, slot.Info, work.Decision.Wrapped);
        }

        private void OnEndPaused(PlaylistSessionWork.EndPausing work, PlaylistSessionInput.Outcome outcome)
        {
            KeepRun(outcome);

            if (outcome.Kind == PlaylistOutcome.Ok || Disposing)
            {
                if (Disposing)
                    EndAdvance(work.Advance);
                else
                    EndQueue(work.Advance);
                return;
            }

            // The item must not go on presenting while the controller says Ended, so it is
            // disposed instead, as it was before items were kept.
            Emit(
                new PlaylistSessionAction.Log(
                    PlaylistSessionLog.KeptItemPauseFailed,
                    Source: State.Item?.Item.Source.DisplayName ?? "(unknown)",
                    Error: outcome.Error
                )
            );
            State = State with { Item = null };
            Await(new PlaylistSessionAction.DisposeItem(), new PlaylistSessionWork.EndDiscarding(work.Advance));
        }

        private void Rebuild(PlaylistAdvanceRun advance, PlaylistQueue.NextDecision decision)
        {
            // Tear down the item that ended, faulted or was skipped. It stops its graph and
            // deactivates audio; the sinks are never disposed.
            if (State.Item is not null)
            {
                State = State with { Item = null };
                Await(
                    new PlaylistSessionAction.DisposeItem(),
                    new PlaylistSessionWork.DisposingOld(advance, decision)
                );
                return;
            }

            AfterOldDisposed(advance, decision);
        }

        private void AfterOldDisposed(PlaylistAdvanceRun advance, PlaylistQueue.NextDecision pending)
        {
            // Rebase the position clock so the next item plays from zero.
            Emit(new PlaylistSessionAction.StopClock());
            OpenNext(advance, pending);
        }

        private void OpenNext(PlaylistAdvanceRun advance, PlaylistQueue.NextDecision pending)
        {
            if (Disposing)
            {
                EndAdvance(advance);
                return;
            }

            if (pending.Kind == PlaylistQueue.NextKind.End)
            {
                EndQueue(advance);
                return;
            }

            State = State with { Generation = State.Generation + 1 };
            Await(
                new PlaylistSessionAction.OpenItem(State.Generation, pending.Item!.Source, Command: null),
                new PlaylistSessionWork.Opening(advance, pending)
            );
        }

        private void OnOpened(PlaylistSessionWork.Opening work, PlaylistSessionInput.Outcome outcome)
        {
            if (outcome.Kind != PlaylistOutcome.Ok)
            {
                DiscardFailedStart(work.Advance, work.Pending.Item!, outcome);
                return;
            }

            State = State with
            {
                Item = new PlaylistItemSlot(
                    State.Generation,
                    work.Pending.Item!,
                    Played: false,
                    AwaitsPlay: !work.Advance.Playing,
                    outcome.Run,
                    outcome.Info
                ),
            };

            if (Disposing)
            {
                EndAdvance(work.Advance);
                return;
            }

            Await(
                new PlaylistSessionAction.WarmUpItem(Command: null),
                new PlaylistSessionWork.Warming(work.Advance, work.Pending)
            );
        }

        private void OnWarmed(PlaylistSessionWork.Warming work, PlaylistSessionInput.Outcome outcome)
        {
            KeepRun(outcome);

            if (Disposing)
            {
                EndAdvance(work.Advance);
                return;
            }

            if (outcome.Kind != PlaylistOutcome.Ok)
            {
                State = State with { Item = null };
                DiscardFailedStart(work.Advance, work.Pending.Item!, outcome);
                return;
            }

            if (!work.Advance.Playing)
            {
                StartSettled(work.Advance, work.Pending);
                return;
            }

            Await(
                new PlaylistSessionAction.PlayItem(Command: null),
                new PlaylistSessionWork.Starting(work.Advance, work.Pending)
            );
        }

        private void OnStartFinished(PlaylistSessionWork.Starting work, PlaylistSessionInput.Outcome outcome)
        {
            KeepRun(outcome);

            if (Disposing)
            {
                EndAdvance(work.Advance);
                return;
            }

            if (outcome.Kind != PlaylistOutcome.Ok)
            {
                State = State with { Item = null };
                DiscardFailedStart(work.Advance, work.Pending.Item!, outcome);
                return;
            }

            State = State with { Item = State.Item! with { Played = true } };
            StartSettled(work.Advance, work.Pending);
        }

        private void StartSettled(PlaylistAdvanceRun advance, PlaylistQueue.NextDecision pending)
        {
            // A successful start does not reset the failure count; see PlaylistFailureGuard. The
            // controller's duration and metadata follow the new item, reported before the transition
            // so a transition subscriber sees the controller already describing it.
            var info = State.Item!.Info;
            Emit(new PlaylistSessionAction.ReportCurrentItemChanged(info));
            RaiseStart(advance, pending.Item!, info, pending.Wrapped);
        }

        private void DiscardFailedStart(
            PlaylistAdvanceRun advance,
            PlaylistItem item,
            PlaylistSessionInput.Outcome failure
        ) =>
            Await(
                new PlaylistSessionAction.DisposeItem(),
                new PlaylistSessionWork.DiscardingFailedStart(advance, item, failure)
            );

        private void OnFailedStartDiscarded(PlaylistSessionWork.DiscardingFailedStart work)
        {
            if (Disposing)
            {
                EndAdvance(work.Advance);
                return;
            }

            Emit(
                new PlaylistSessionAction.ReportItemFailed(
                    work.Item.Source.DisplayName,
                    PlaylistItemFailure.CouldNotStart,
                    work.Failure.Error
                )
            );

            var (queue, giveUp) = Queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero);
            Queue = queue;
            if (giveUp)
            {
                GiveUp(work.Failure.Error);
                EndAdvance(work.Advance);
                return;
            }

            // The queue moved past the failed item when it was taken, so this takes the one after
            // it, under every repeat mode.
            var (next, decision) = Queue.DecideNext(PlaylistAdvance.FailedStart);
            Queue = next;
            OpenNext(work.Advance, decision);
        }

        private void RaiseStart(
            PlaylistAdvanceRun advance,
            PlaylistItem item,
            MediaInfo? info,
            bool wrapped
        )
        {
            var (queue, index) = Queue.ReportCurrent(item, info);
            Queue = queue;
            Emit(new PlaylistSessionAction.RaiseTransition(item, info, index, wrapped));

            // The next step reads the queue again, after a transition subscriber has run.
            State = State with { Work = new PlaylistSessionWork.Started(advance) };
        }

        /// <summary>
        /// Takes a jump recorded while the pass was under way, before the input ends, so the item the
        /// pass started does not play through.
        /// </summary>
        private void AfterAdvancePass(PlaylistAdvanceRun advance)
        {
            State = State with { Work = null };

            if (
                !Disposing
                && !State.GaveUp
                && State.Run is PlaylistRunState.Playing or PlaylistRunState.Paused
                && Queue.HasPendingJump
            )
            {
                AdvancePass(advance.Command, ItemEnding.Skip);
                return;
            }

            EndAdvance(advance);
        }

        private void EndQueue(PlaylistAdvanceRun advance)
        {
            State = State with { Run = PlaylistRunState.Ended };
            Emit(new PlaylistSessionAction.ReportEndOfStream());
            EndAdvance(advance);
        }

        private void EndAdvance(PlaylistAdvanceRun advance)
        {
            if (advance.Command is { } command)
                Complete(command, PlaylistCommandResult.Ok);
            else
                Done();
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private void LeaveEndedAfterWarmUp()
        {
            // The controller warms up on load and on a seek out of Ended, and settles in Paused
            // straight after the seek's warm-up, so a skip issued once it is Paused sees the
            // session paused.
            if (State.Run == PlaylistRunState.Ended)
                State = State with { Run = PlaylistRunState.Paused };
        }

        private void KeepRun(PlaylistSessionInput.Outcome outcome)
        {
            if (State.Item is { } item)
                State = State with { Item = item with { KnownRun = outcome.Run } };
        }

        private void GiveUp(Exception? last)
        {
            State = State with { GaveUp = true };
            Emit(new PlaylistSessionAction.ReportFatal(GiveUpException(last)));
        }

        private InvalidOperationException GiveUpException(Exception? last) =>
            new(
                $"Playlist advance gave up after {Queue.ConsecutiveFailures} "
                    + "consecutive item failures.",
                last
            );

        private static PlaylistAdvance ToAdvance(ItemEnding how) =>
            how switch
            {
                ItemEnding.EndOfStream => PlaylistAdvance.EndOfStream,
                ItemEnding.Skip => PlaylistAdvance.Skip,
                ItemEnding.Fault => PlaylistAdvance.Fault,
                _ => PlaylistAdvance.FailedStart,
            };

        private void Emit(PlaylistSessionAction action) => _actions.Add(action);

        private void Await(PlaylistSessionAction action, PlaylistSessionWork work)
        {
            _awaited = action;
            State = State with { Work = work };
        }

        private void Complete(int command, PlaylistCommandResult result)
        {
            Emit(new PlaylistSessionAction.CompleteCommand(command, result));
            Done();
        }

        private void Done() => _done = true;

        private void FailUnexpected(PlaylistSessionInput input)
        {
            int? command = input switch
            {
                PlaylistSessionInput.Initialize i => i.Command,
                PlaylistSessionInput.WarmUp w => w.Command,
                PlaylistSessionInput.Play p => p.Command,
                PlaylistSessionInput.Pause p => p.Command,
                PlaylistSessionInput.Seek s => s.Command,
                PlaylistSessionInput.Rewind r => r.Command,
                _ => null,
            };

            if (command is { } id)
            {
                _actions.Add(
                    new PlaylistSessionAction.CompleteCommand(
                        id,
                        PlaylistCommandResult.Failed(
                            new InvalidOperationException(
                                $"'{input}' arrived while another input was under way."
                            )
                        )
                    )
                );
            }
            Done();
        }
    }
}
