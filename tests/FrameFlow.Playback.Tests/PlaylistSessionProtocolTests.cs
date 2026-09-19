namespace FrameFlow.Playback.Tests;

/// <summary>
/// Table tests of <see cref="PlaylistSessionProtocol.Step"/>. Each row names an abstract state and an
/// input, and the decision the first step must make. No shell, no runtime and no await.
/// </summary>
/// <remarks>
/// <para>
/// The abstraction is the one decision 7 of <c>docs/adr/ADR-0076-playlist-session-protocol.md</c> names: the
/// run state; the item slot's kind, and whether the item has played; whether a jump is pending or
/// an advance latched; and whether an input's generation and run are the current ones. The queue
/// behind each row is fixed: its current item is <c>a</c>, and what an advance takes next is chosen
/// per table.
/// </para>
/// <para>
/// The multi-step sequences, and what each rule was settled for, are in
/// <c>PlaylistSessionTranscriptTests</c>.
/// </para>
/// </remarks>
public sealed class PlaylistSessionProtocolTests
{
    private const int CurrentGeneration = 3;
    private const int CurrentRun = 5;
    private const int Command = 7;

    private static readonly MediaInfo Info = new("test", TimeSpan.FromSeconds(3), [], []);

    /// <summary>
    /// <see cref="PlaylistRunState"/>, which is internal, for the rows of public theories.
    /// </summary>
    public enum Run
    {
        NotStarted,
        Playing,
        Paused,
        Ended,
    }

    /// <summary>The item slot's kind.</summary>
    public enum Slot
    {
        None,
        Unplayed,
        Played,
    }

    /// <summary>What comes after the current item in the queue behind a row.</summary>
    public enum Next
    {
        /// <summary>Another item: an advance rebuilds.</summary>
        Other,

        /// <summary>Nothing: an advance ends the queue.</summary>
        Nothing,

        /// <summary>The same item under <see cref="RepeatMode.One"/>: an advance replays.</summary>
        Replay,
    }

    /// <summary>The decision a step makes, read from what it asks of the shell.</summary>
    public enum Decision
    {
        /// <summary>Nothing happens, apart from a log line.</summary>
        Drop,

        /// <summary>The advance is latched on the queue for the first Play.</summary>
        Latch,

        /// <summary>The controller is handed a fatal error.</summary>
        Fatal,

        /// <summary>The runtime is rewound in place for a replay.</summary>
        Rewind,

        /// <summary>A skipped item is paused before the queue ends.</summary>
        PauseThenEnd,

        /// <summary>The queue ends at once, keeping the item.</summary>
        End,

        /// <summary>The runtime is disposed so another can be opened.</summary>
        Rebuild,

        /// <summary>With no runtime to dispose, the clock stops and the next item opens.</summary>
        Open,

        /// <summary>A command's call on the runtime.</summary>
        ItemCall,

        /// <summary>A command completes without touching the runtime.</summary>
        Complete,
    }

    private sealed record FakeSource(string DisplayName) : IMediaSource
    {
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    // ── Rows ────────────────────────────────────────────────────────────────

    public static TheoryData<Run, Slot, Decision> EndOfStreamFromTheCurrentRun =>
        new()
        {
            { Run.NotStarted, Slot.Unplayed, Decision.Latch },
            { Run.NotStarted, Slot.Played, Decision.Latch },
            { Run.Playing, Slot.Unplayed, Decision.Rebuild },
            { Run.Playing, Slot.Played, Decision.Rebuild },
            { Run.Paused, Slot.Unplayed, Decision.Rebuild },
            { Run.Paused, Slot.Played, Decision.Rebuild },
            { Run.Ended, Slot.Unplayed, Decision.Drop },
            { Run.Ended, Slot.Played, Decision.Drop },
        };

    [Theory]
    [MemberData(nameof(EndOfStreamFromTheCurrentRun))]
    public void EndOfStream_FromTheCurrentRun(Run run, Slot slot, Decision expected)
    {
        var (state, queue) = Setup(run, slot, Next.Other);

        var (_, after, step) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));

        Assert.Equal(expected, Classify(queue, after, step));
        if (expected == Decision.Latch)
            Assert.Equal(PlaylistAdvance.EndOfStream, after.LatchedAdvance);
    }

    public static TheoryData<Run, Slot, Next, Decision> EndOfStreamByWhatComesNext =>
        new()
        {
            // An item that reached its end has stopped: the queue ends with no pause.
            { Run.Playing, Slot.Played, Next.Nothing, Decision.End },
            { Run.Paused, Slot.Played, Next.Nothing, Decision.End },
            // Only a playing item that has played is rewound in place.
            { Run.Playing, Slot.Played, Next.Replay, Decision.Rewind },
            { Run.Playing, Slot.Unplayed, Next.Replay, Decision.Rebuild },
            { Run.Paused, Slot.Played, Next.Replay, Decision.Rebuild },
        };

    [Theory]
    [MemberData(nameof(EndOfStreamByWhatComesNext))]
    public void EndOfStream_ByWhatComesNext(
        Run run,
        Slot slot,
        Next next,
        Decision expected
    )
    {
        var (state, queue) = Setup(run, slot, next);

        var (_, after, step) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));

        Assert.Equal(expected, Classify(queue, after, step));
    }

    public static TheoryData<Run, Slot, Next, Decision> SkipsRequestedAtTheCurrentGeneration =>
        new()
        {
            { Run.NotStarted, Slot.Unplayed, Next.Other, Decision.Latch },
            { Run.Playing, Slot.Played, Next.Other, Decision.Rebuild },
            { Run.Paused, Slot.Played, Next.Other, Decision.Rebuild },
            { Run.Ended, Slot.Played, Next.Other, Decision.Drop },
            // A skip on the last item pauses it first if it has played, playing or paused.
            { Run.Playing, Slot.Played, Next.Nothing, Decision.PauseThenEnd },
            { Run.Paused, Slot.Played, Next.Nothing, Decision.PauseThenEnd },
            { Run.Playing, Slot.Unplayed, Next.Nothing, Decision.End },
            { Run.Paused, Slot.Unplayed, Next.Nothing, Decision.End },
            // A skip under One on the only item wraps back to it, which is a replay: a played
            // item is rewound in place.
            { Run.Playing, Slot.Played, Next.Replay, Decision.Rewind },
        };

    [Theory]
    [MemberData(nameof(SkipsRequestedAtTheCurrentGeneration))]
    public void Skip_RequestedAtTheCurrentGeneration(
        Run run,
        Slot slot,
        Next next,
        Decision expected
    )
    {
        var (state, queue) = Setup(run, slot, next);

        var (_, after, step) = Step(state, queue, new PlaylistSessionInput.SkipRequested(CurrentGeneration, R(run)));

        Assert.Equal(expected, Classify(queue, after, step));
        if (expected == Decision.Latch)
            Assert.Equal(PlaylistAdvance.Skip, after.LatchedAdvance);
    }

    public static TheoryData<Run, Next, Decision> FaultsFromTheCurrentGeneration =>
        new()
        {
            // Before the first Play, a fault is the first item failing to start.
            { Run.NotStarted, Next.Other, Decision.Fatal },
            { Run.Playing, Next.Other, Decision.Rebuild },
            { Run.Paused, Next.Other, Decision.Rebuild },
            { Run.Ended, Next.Other, Decision.Drop },
            // A faulted item is never rewound in place, and never kept at the end.
            { Run.Playing, Next.Replay, Decision.Rebuild },
            { Run.Playing, Next.Nothing, Decision.Rebuild },
        };

    [Theory]
    [MemberData(nameof(FaultsFromTheCurrentGeneration))]
    public void Fault_FromTheCurrentGeneration(Run run, Next next, Decision expected)
    {
        var (state, queue) = Setup(run, Slot.Played, next);

        var (after, _, step) = Step(
            state,
            queue,
            new PlaylistSessionInput.Fault(CurrentGeneration, new InvalidOperationException("boom"), TimeSpan.Zero)
        );

        Assert.Equal(expected, Classify(queue, queue, step));
        if (expected is Decision.Rebuild)
        {
            Assert.IsType<PlaylistSessionAction.ReportItemFailed>(Assert.Single(step.Actions));
            Assert.Equal(CurrentGeneration, after.LastFaultedGeneration);
        }
        if (expected is Decision.Fatal)
            Assert.True(after.GaveUp);
    }

    private static readonly Dictionary<string, PlaylistSessionInput> Stale = new()
    {
        ["end-of-stream from an older generation"] = new PlaylistSessionInput.EndOfStream(
            CurrentGeneration - 1,
            CurrentRun
        ),
        ["end-of-stream from an older run"] = new PlaylistSessionInput.EndOfStream(
            CurrentGeneration,
            CurrentRun - 1
        ),
        ["fault from an older generation"] = new PlaylistSessionInput.Fault(
            CurrentGeneration - 1,
            new InvalidOperationException(),
            TimeSpan.Zero
        ),
        ["skip requested at an older generation"] = new PlaylistSessionInput.SkipRequested(
            CurrentGeneration - 1,
            PlaylistRunState.Playing
        ),
        ["skip requested while Ended"] = new PlaylistSessionInput.SkipRequested(
            CurrentGeneration,
            PlaylistRunState.Ended
        ),
    };

    public static TheoryData<string> StaleInputs => new(Stale.Keys);

    [Theory]
    [MemberData(nameof(StaleInputs))]
    public void StaleInputs_AreDropped_InEveryRunStateAndSlot(string name)
    {
        var input = Stale[name];
        foreach (var run in Enum.GetValues<PlaylistRunState>())
        {
            foreach (var slot in Enum.GetValues<Slot>())
            {
                var (state, queue) = Setup(run, slot, Next.Other);
                var (after, afterQueue, step) = Step(state, queue, input);

                Assert.True(
                    Classify(queue, afterQueue, step) == Decision.Drop,
                    $"{run}, {slot}: {Classify(queue, afterQueue, step)}"
                );
                Assert.Equal(state, after);
            }
        }
    }

    [Fact]
    public void AnEndOfStream_WithNoItem_IsDropped()
    {
        foreach (var run in Enum.GetValues<PlaylistRunState>())
        {
            var (state, queue) = Setup(run, Slot.None, Next.Other);
            var (_, after, step) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
            Assert.Equal(Decision.Drop, Classify(queue, after, step));
        }
    }

    [Fact]
    public void ASecondFault_FromTheGenerationAlreadyHandled_IsDropped()
    {
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Other);
        state = state with { LastFaultedGeneration = CurrentGeneration };

        var (_, after, step) = Step(
            state,
            queue,
            new PlaylistSessionInput.Fault(CurrentGeneration, new InvalidOperationException(), TimeSpan.Zero)
        );

        Assert.Equal(Decision.Drop, Classify(queue, after, step));
    }

    public static TheoryData<Run, bool, Decision> JumpRequests =>
        new()
        {
            { Run.NotStarted, true, Decision.Drop },
            { Run.Playing, true, Decision.Rebuild },
            { Run.Paused, true, Decision.Rebuild },
            { Run.Ended, true, Decision.Drop },
            // A jump an advance has already taken leaves nothing pending.
            { Run.Playing, false, Decision.Drop },
            { Run.Paused, false, Decision.Drop },
        };

    [Theory]
    [MemberData(nameof(JumpRequests))]
    public void JumpRequested(Run run, bool pending, Decision expected)
    {
        var (state, queue) = Setup(run, Slot.Played, Next.Other, jumpPending: pending);

        var (_, after, step) = Step(state, queue, new PlaylistSessionInput.JumpRequested());

        Assert.Equal(expected, Classify(queue, after, step));
    }

    [Fact]
    public void AfterGivingUp_NotificationsAreDropped_AndCommandsComplete()
    {
        foreach (var run in Enum.GetValues<PlaylistRunState>())
        {
            var (state, queue) = Setup(run, Slot.Played, Next.Other, jumpPending: true);
            state = state with { GaveUp = true };

            foreach (var input in Notifications(run))
            {
                var (_, after, step) = Step(state, queue, input);
                Assert.True(Classify(queue, after, step) == Decision.Drop, $"{run}, {input}");
            }

            // The controller is about to dispose the session, so nothing starts an item. A
            // session gives up only after it has loaded, so Initialize is not among these.
            foreach (var input in Commands().Where(c => c is not PlaylistSessionInput.Initialize))
            {
                var (_, _, step) = Step(state, queue, input);
                Assert.Null(step.Awaited);
                var complete = Assert.IsType<PlaylistSessionAction.CompleteCommand>(Assert.Single(step.Actions));
                Assert.Equal(PlaylistOutcome.Ok, complete.Result.Kind);
            }
        }
    }

    [Fact]
    public void WhileDisposing_NotificationsAreDropped_AndCommandsComplete()
    {
        foreach (var run in Enum.GetValues<PlaylistRunState>())
        {
            var (state, queue) = Setup(run, Slot.Played, Next.Other, jumpPending: true);
            var disposing = new PlaylistStepContext(Disposing: true);

            foreach (var input in Notifications(run))
            {
                var (_, after, step) = PlaylistSessionProtocol.Step(state, queue, input, disposing);
                // A skip requested before the first Play is latched for the next session.
                var expected =
                    run == PlaylistRunState.NotStarted && input is PlaylistSessionInput.SkipRequested
                        ? Decision.Latch
                        : Decision.Drop;
                Assert.True(Classify(queue, after, step) == expected, $"{run}, {input}");
            }

            foreach (var input in Commands())
            {
                var (_, _, step) = PlaylistSessionProtocol.Step(state, queue, input, disposing);
                Assert.Null(step.Awaited);
                var complete = Assert.IsType<PlaylistSessionAction.CompleteCommand>(Assert.Single(step.Actions));
                Assert.Equal(PlaylistOutcome.Ok, complete.Result.Kind);
            }
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────

    public static TheoryData<Run, Slot, Decision, Run> Plays =>
        new()
        {
            // A Play at Ended was dispatched before the controller saw the end-of-stream.
            { Run.Ended, Slot.Played, Decision.Complete, Run.Ended },
            { Run.NotStarted, Slot.Unplayed, Decision.ItemCall, Run.Playing },
            { Run.Paused, Slot.Played, Decision.ItemCall, Run.Playing },
            { Run.Playing, Slot.None, Decision.Complete, Run.Playing },
        };

    [Theory]
    [MemberData(nameof(Plays))]
    public void Play(Run run, Slot slot, Decision expected, Run runAfter)
    {
        var (state, queue) = Setup(run, slot, Next.Other);

        var (after, afterQueue, step) = Step(state, queue, new PlaylistSessionInput.Play(Command));

        Assert.Equal(expected, Classify(queue, afterQueue, step));
        Assert.Equal(R(runAfter), after.Run);
        if (expected == Decision.ItemCall)
            Assert.Equal(new PlaylistSessionAction.PlayItem(Command), step.Awaited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Play_TakesALatchedAdvance(bool endOfStream)
    {
        var (state, queue) = Setup(PlaylistRunState.NotStarted, Slot.Unplayed, Next.Other);
        queue = queue.LatchAdvance(endOfStream ? PlaylistAdvance.EndOfStream : PlaylistAdvance.Skip);

        var (after, afterQueue, step) = Step(state, queue, new PlaylistSessionInput.Play(Command));

        Assert.Equal(Decision.Rebuild, Classify(queue, afterQueue, step));
        Assert.Null(afterQueue.LatchedAdvance);
        Assert.Equal(PlaylistRunState.Playing, after.Run);
    }

    [Fact]
    public void Play_TakesAPendingJump()
    {
        var (state, queue) = Setup(PlaylistRunState.Paused, Slot.Played, Next.Other, jumpPending: true);

        var (_, afterQueue, step) = Step(state, queue, new PlaylistSessionInput.Play(Command));

        Assert.Equal(Decision.Rebuild, Classify(queue, afterQueue, step));
        Assert.False(afterQueue.HasPendingJump);
    }

    public static TheoryData<Run, Run> Pauses =>
        new()
        {
            { Run.Playing, Run.Paused },
            { Run.Paused, Run.Paused },
            // Only a playing session pauses.
            { Run.NotStarted, Run.NotStarted },
            { Run.Ended, Run.Ended },
        };

    [Theory]
    [MemberData(nameof(Pauses))]
    public void Pause_RecordsPausedOnlyFromPlaying_AndPausesTheItem(Run run, Run runAfter)
    {
        var (state, queue) = Setup(run, Slot.Played, Next.Other);

        var (after, _, step) = Step(state, queue, new PlaylistSessionInput.Pause(Command));

        Assert.Equal(R(runAfter), after.Run);
        Assert.Equal(new PlaylistSessionAction.PauseItem(Command), step.Awaited);
    }

    [Theory]
    [InlineData(Run.NotStarted, true)]
    [InlineData(Run.Playing, true)]
    [InlineData(Run.Paused, true)]
    // A seek at Ended was dispatched before the controller saw the end-of-stream.
    [InlineData(Run.Ended, false)]
    public void Seek_ReachesTheItemExceptAtEnded(Run run, bool reaches)
    {
        var (state, queue) = Setup(run, Slot.Played, Next.Other);
        var position = TimeSpan.FromSeconds(1);

        var (_, afterQueue, step) = Step(state, queue, new PlaylistSessionInput.Seek(Command, position));

        Assert.Equal(reaches ? Decision.ItemCall : Decision.Complete, Classify(queue, afterQueue, step));
        if (reaches)
            Assert.Equal(new PlaylistSessionAction.SeekItem(position, Command), step.Awaited);
    }

    [Fact]
    public void WarmUp_LeavesEnded_OnlyOnceTheItemHasWarmed()
    {
        var (state, queue) = Setup(PlaylistRunState.Ended, Slot.Played, Next.Other);

        var (warming, q1, first) = Step(state, queue, new PlaylistSessionInput.WarmUp(Command));
        Assert.Equal(new PlaylistSessionAction.WarmUpItem(Command), first.Awaited);
        Assert.Equal(PlaylistRunState.Ended, warming.Run);

        var (failed, _, _) = Step(warming, q1, new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, CurrentRun, Error: new InvalidOperationException()));
        Assert.Equal(PlaylistRunState.Ended, failed.Run);

        var (warmed, _, done) = Step(warming, q1, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok, CurrentRun));
        Assert.Equal(PlaylistRunState.Paused, warmed.Run);
        Assert.True(done.Done);
    }

    [Fact]
    public void Initialize_WithNothingQueued_FailsTheCommand()
    {
        var (_, _, step) = Step(
            PlaylistSessionState.Initial,
            PlaylistQueue.Create([], RepeatMode.Off),
            new PlaylistSessionInput.Initialize(Command)
        );

        Assert.Null(step.Awaited);
        var complete = Assert.IsType<PlaylistSessionAction.CompleteCommand>(Assert.Single(step.Actions));
        Assert.Equal(PlaylistOutcome.Failed, complete.Result.Kind);
        Assert.IsType<InvalidOperationException>(complete.Result.Error);
    }

    public static TheoryData<string, Run, Run> CommandsWithNoItem =>
        new()
        {
            // A queue that ended on a failed item holds no item, and the controller can still seek
            // out of Ended, play and pause. The warm-up still leaves Ended.
            { "WarmUp", Run.Ended, Run.Paused },
            { "WarmUp", Run.Paused, Run.Paused },
            { "Pause", Run.Playing, Run.Paused },
            { "Pause", Run.Paused, Run.Paused },
            { "Seek", Run.Paused, Run.Paused },
            { "Rewind", Run.Playing, Run.Playing },
        };

    [Theory]
    [MemberData(nameof(CommandsWithNoItem))]
    public void ACommand_WithNoItem_CompletesWithoutAnItemCall(string command, Run run, Run runAfter)
    {
        var (state, queue) = Setup(run, Slot.None, Next.Other);

        var (after, afterQueue, step) = Step(state, queue, CommandNamed(command));

        Assert.Equal(Decision.Complete, Classify(queue, afterQueue, step));
        Assert.Equal(R(runAfter), after.Run);
    }

    [Fact]
    public void ACommandsItemCall_CarriesItsCommand_AndAnAdvancesDoesNot()
    {
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Other);

        var (_, _, rewind) = Step(state, queue, new PlaylistSessionInput.Rewind(Command));
        Assert.Equal(new PlaylistSessionAction.RewindItem(Command), rewind.Awaited);

        // An end-of-stream under One replays in place with no command's token.
        var (replayState, replayQueue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);
        var (_, _, replay) = Step(replayState, replayQueue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
        Assert.Equal(new PlaylistSessionAction.RewindItem(Command: null), replay.Awaited);
    }

    // ── Outcomes ────────────────────────────────────────────────────────────

    [Fact]
    public void AnInPlaceRewindThatFails_RebuildsTheItem()
    {
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);
        var (rewinding, q1, _) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));

        var (_, _, step) = Step(
            rewinding,
            q1,
            new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, CurrentRun + 1, Error: new InvalidOperationException())
        );

        Assert.IsType<PlaylistSessionAction.Log>(Assert.Single(step.Actions));
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);
    }

    [Fact]
    public void AnItemThatFailsToOpen_IsDisposed_Reported_AndTheNextIsOpened()
    {
        // An advance with no item to dispose opens at once. A latched skip taken by Play is one.
        var (state, queue) = Setup(PlaylistRunState.NotStarted, Slot.None, Next.Other, extraItems: 2);
        queue = queue.LatchAdvance(PlaylistAdvance.Skip);
        var (opening, q1, open) = Step(state, queue, new PlaylistSessionInput.Play(Command));
        var openB = Assert.IsType<PlaylistSessionAction.OpenItem>(open.Awaited);
        Assert.Equal("b", openB.Source.DisplayName);
        Assert.Null(openB.Command);

        var (discarding, q2, failed) = Step(
            opening,
            q1,
            new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, Error: new InvalidOperationException("boom"))
        );
        Assert.IsType<PlaylistSessionAction.DisposeItem>(failed.Awaited);
        Assert.Null(discarding.Item);

        var (_, _, next) = Step(discarding, q2, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok));
        var report = Assert.IsType<PlaylistSessionAction.ReportItemFailed>(Assert.Single(next.Actions));
        Assert.Equal("b", report.Item.Source.DisplayName);
        var openC = Assert.IsType<PlaylistSessionAction.OpenItem>(next.Awaited);
        Assert.Equal("c", openC.Source.DisplayName);
        Assert.Equal(openB.Generation + 1, openC.Generation);
    }

    [Fact]
    public void AStartedItem_IsReportedThenRaised_AndTheQueueIsReadAgain()
    {
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Other);
        var (s, q, step) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);

        (s, q, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok));
        Assert.IsType<PlaylistSessionAction.StopClock>(Assert.Single(step.Actions));
        Assert.IsType<PlaylistSessionAction.OpenItem>(step.Awaited);

        (s, q, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok, 0, Info));
        Assert.IsType<PlaylistSessionAction.WarmUpItem>(step.Awaited);
        (s, q, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok));
        Assert.IsType<PlaylistSessionAction.PlayItem>(step.Awaited);
        (s, q, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok));

        Assert.Collection(
            step.Actions,
            a => Assert.IsType<PlaylistSessionAction.ReportCurrentItemChanged>(a),
            a => Assert.IsType<PlaylistSessionAction.RaiseTransition>(a)
        );
        Assert.Null(step.Awaited);
        Assert.False(step.Done);

        // A transition subscriber jumps back to a before the continue.
        (q, _) = q.RequestJump(q.Playlist[0]);
        (_, _, step) = Step(s, q, new PlaylistSessionInput.Continue());
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);
    }

    [Fact]
    public void AnOpenThatSucceedsDuringDisposal_IsKept_ForTheDisposeInputToDispose()
    {
        var disposing = new PlaylistStepContext(Disposing: true);

        // An advance's open: disposal begins while it is in flight, and it succeeds anyway.
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Other);
        var (s, q, step) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
        (s, q, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok));
        Assert.IsType<PlaylistSessionAction.OpenItem>(step.Awaited);

        (s, q, step) = PlaylistSessionProtocol.Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok, 0, Info), disposing);
        Assert.True(step.Done);
        Assert.Empty(step.Actions);
        Assert.NotNull(s.Item);
        Assert.Null(s.Work);

        (s, _, step) = PlaylistSessionProtocol.Step(s, q, new PlaylistSessionInput.Dispose(), disposing);
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);
        Assert.Null(s.Item);

        // Initialize's open, in the same race: the command completes, nothing is attached or
        // raised, and the Dispose input disposes the runtime.
        var a = new PlaylistItem(new FakeSource("a"));
        (s, q, step) = Step(PlaylistSessionState.Initial, PlaylistQueue.Create([a], RepeatMode.Off), new PlaylistSessionInput.Initialize(Command));
        Assert.IsType<PlaylistSessionAction.OpenItem>(step.Awaited);

        (s, q, step) = PlaylistSessionProtocol.Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok, 0, Info), disposing);
        var complete = Assert.IsType<PlaylistSessionAction.CompleteCommand>(Assert.Single(step.Actions));
        Assert.Equal(PlaylistOutcome.Ok, complete.Result.Kind);
        Assert.NotNull(s.Item);

        (s, _, step) = PlaylistSessionProtocol.Step(s, q, new PlaylistSessionInput.Dispose(), disposing);
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);
        Assert.Null(s.Item);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisposalDuringAnInPlaceReplay_EndsTheAdvance_AndKeepsTheItemForDispose(bool rewound)
    {
        var disposing = new PlaylistStepContext(Disposing: true);
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);
        var (s, q, step) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
        Assert.Equal(new PlaylistSessionAction.RewindItem(Command: null), step.Awaited);
        var replaying = q;

        // Disposal begins while the rewind is in flight. Whether the rewind succeeds or fails, no
        // transition is raised and nothing is rebuilt.
        var outcome = rewound
            ? new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok, CurrentRun + 1)
            : new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, CurrentRun + 1, Error: new InvalidOperationException());
        (s, q, step) = PlaylistSessionProtocol.Step(s, q, outcome, disposing);

        Assert.True(step.Done);
        Assert.Null(step.Awaited);
        Assert.Empty(step.Actions);
        Assert.Null(s.Work);
        Assert.Equal(replaying, q);
        Assert.Equal(CurrentRun + 1, s.Item?.KnownRun);

        (s, _, step) = PlaylistSessionProtocol.Step(s, q, new PlaylistSessionInput.Dispose(), disposing);
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);
        Assert.Null(s.Item);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLastItemFailing_EndsTheQueue_WithNoItemKept(bool faults)
    {
        var boom = new InvalidOperationException("boom");
        var actions = new List<PlaylistSessionAction>();
        PlaylistSessionState s;
        PlaylistQueue q;
        PlaylistSessionStep step;

        if (faults)
        {
            // a, the last item, faults while it plays.
            (s, q) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Nothing);
            (s, q, step) = Record(
                Step(s, q, new PlaylistSessionInput.Fault(CurrentGeneration, boom, TimeSpan.Zero))
            );
        }
        else
        {
            // a ends, and b, the last item, fails to open.
            (s, q) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Other);
            (s, q, step) = Record(Step(s, q, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun)));
            (s, q, step) = Record(Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok)));
            Assert.IsType<PlaylistSessionAction.OpenItem>(step.Awaited);
            (s, q, step) = Record(
                Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, Error: boom))
            );
        }

        // A failed item is disposed rather than kept at the end.
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);
        (s, _, step) = Record(Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok)));

        Assert.True(step.Done);
        Assert.Null(step.Awaited);
        Assert.Equal(PlaylistRunState.Ended, s.Run);
        Assert.Null(s.Item);
        Assert.Collection(
            actions.Where(a => a is PlaylistSessionAction.ReportItemFailed or PlaylistSessionAction.ReportEndOfStream),
            a =>
                AssertItemFailed(
                    a,
                    faults ? "a" : "b",
                    faults
                        ? PlaylistItemFailure.FaultedDuringPlayback
                        : PlaylistItemFailure.CouldNotStart,
                    boom
                ),
            a => Assert.IsType<PlaylistSessionAction.ReportEndOfStream>(a)
        );

        (PlaylistSessionState, PlaylistQueue, PlaylistSessionStep) Record(
            (PlaylistSessionState State, PlaylistQueue Queue, PlaylistSessionStep Step) result
        )
        {
            actions.AddRange(result.Step.Actions);
            return result;
        }
    }

    [Fact]
    public void AKeptItemWhosePauseFails_IsDisposed_AndTheQueueStillEnds()
    {
        // A skip on the last item pauses it before the queue ends, so it does not go on presenting.
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Nothing);
        var (s, q, step) = Step(
            state,
            queue,
            new PlaylistSessionInput.SkipRequested(CurrentGeneration, PlaylistRunState.Playing)
        );
        Assert.Equal(new PlaylistSessionAction.PauseItem(Command: null), step.Awaited);

        // An item that could not be paused must not present while the controller says Ended.
        var boom = new InvalidOperationException("boom");
        (s, q, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, CurrentRun, Error: boom));
        Assert.Equal(
            new PlaylistSessionAction.Log(PlaylistSessionLog.KeptItemPauseFailed, Source: "a", Error: boom),
            Assert.Single(step.Actions)
        );
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);
        Assert.Null(s.Item);

        var (ended, _, end) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok));
        Assert.IsType<PlaylistSessionAction.ReportEndOfStream>(Assert.Single(end.Actions));
        Assert.True(end.Done);
        Assert.Equal(PlaylistRunState.Ended, ended.Run);

        // Disposal that begins while the item is disposed ends the advance, and the end is not reported.
        var (_, _, disposed) = PlaylistSessionProtocol.Step(
            s,
            q,
            new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok),
            new PlaylistStepContext(Disposing: true)
        );
        Assert.Empty(disposed.Actions);
        Assert.True(disposed.Done);
    }

    [Fact]
    public void AnItemWaitingForPlay_ThatFailsToStart_AtTheFailureLimit_GivesUp()
    {
        // An advance opened the item while paused, so Play starts it.
        var (state, queue) = Setup(PlaylistRunState.Paused, Slot.Unplayed, Next.Other);
        state = state with { Item = state.Item! with { AwaitsPlay = true } };
        queue = AtTheFailureLimit(queue);

        var (s, q, step) = Step(state, queue, new PlaylistSessionInput.Play(Command));
        Assert.Equal(new PlaylistSessionAction.PlayItem(Command), step.Awaited);

        var boom = new InvalidOperationException("boom");
        (s, _, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, CurrentRun, Error: boom));

        // The controller is handed the fatal error, so the Play itself completes.
        Assert.Collection(
            step.Actions,
            a => AssertItemFailed(a, "a", PlaylistItemFailure.CouldNotStart, boom),
            a => Assert.Same(boom, Assert.IsType<PlaylistSessionAction.ReportFatal>(a).Error.InnerException),
            a => Assert.Equal(new PlaylistSessionAction.CompleteCommand(Command, PlaylistCommandResult.Ok), a)
        );
        Assert.True(step.Done);
        Assert.Null(step.Awaited);
        Assert.True(s.GaveUp);
    }

    [Fact]
    public void ALaterSessionsFirstItemThatFailsToOpen_IsPassedOver()
    {
        // The queue outlives a session. Once the player has started an item, a new session's first
        // item that cannot be opened is passed over, as an advance passes over one.
        var (_, queue) = Setup(PlaylistRunState.NotStarted, Slot.None, Next.Other, extraItems: 2);
        Assert.True(queue.AnyStarted);

        var (s, q, step) = Step(PlaylistSessionState.Initial, queue, new PlaylistSessionInput.Initialize(Command));
        var openB = Assert.IsType<PlaylistSessionAction.OpenItem>(step.Awaited);
        Assert.Equal("b", openB.Source.DisplayName);

        var boom = new InvalidOperationException("boom");
        (s, q, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, Error: boom));
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);

        (s, q, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok));
        AssertItemFailed(
            Assert.Single(step.Actions),
            "b",
            PlaylistItemFailure.CouldNotStart,
            boom
        );
        var openC = Assert.IsType<PlaylistSessionAction.OpenItem>(step.Awaited);
        Assert.Equal("c", openC.Source.DisplayName);
        Assert.Equal(Command, openC.Command);
        Assert.Equal(openB.Generation + 1, openC.Generation);

        (s, _, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok, 0, Info));
        Assert.Collection(
            step.Actions,
            a => Assert.IsType<PlaylistSessionAction.AttachToCoordinator>(a),
            a => Assert.Equal("c", Assert.IsType<PlaylistSessionAction.RaiseTransition>(a).Item.Source.DisplayName),
            a => Assert.Equal(new PlaylistSessionAction.CompleteCommand(Command, PlaylistCommandResult.Ok), a)
        );
        Assert.True(step.Done);
        Assert.Equal("c", s.Item?.Item.Source.DisplayName);
    }

    /// <summary>Why a session's first item that fails to open fails the load.</summary>
    public enum LoadFailure
    {
        /// <summary>No item has started in the player, so this fails as a single source's would.</summary>
        FirstItemEver,

        /// <summary>The open was cancelled, which is not passed over.</summary>
        Cancelled,

        /// <summary>The item was passed over, and nothing is left to open under Off.</summary>
        NothingLeft,

        /// <summary>The item was passed over, and the failure guard gives up.</summary>
        AtTheFailureLimit,
    }

    [Theory]
    [InlineData(LoadFailure.FirstItemEver)]
    [InlineData(LoadFailure.Cancelled)]
    [InlineData(LoadFailure.NothingLeft)]
    [InlineData(LoadFailure.AtTheFailureLimit)]
    public void AFirstItemThatFailsToOpen_FailsTheLoad(LoadFailure why)
    {
        var boom = new InvalidOperationException("boom");
        var cancellation = new OperationCanceledException();
        var queue = why switch
        {
            LoadFailure.FirstItemEver => PlaylistQueue.Create(
                [new PlaylistItem(new FakeSource("a")), new PlaylistItem(new FakeSource("b"))],
                RepeatMode.Off
            ),
            LoadFailure.NothingLeft => Setup(PlaylistRunState.NotStarted, Slot.None, Next.Other).Queue,
            LoadFailure.AtTheFailureLimit => AtTheFailureLimit(
                Setup(PlaylistRunState.NotStarted, Slot.None, Next.Other, extraItems: 2).Queue
            ),
            _ => Setup(PlaylistRunState.NotStarted, Slot.None, Next.Other, extraItems: 2).Queue,
        };

        var (s, q, step) = Step(PlaylistSessionState.Initial, queue, new PlaylistSessionInput.Initialize(Command));
        Assert.IsType<PlaylistSessionAction.OpenItem>(step.Awaited);
        var failure = why == LoadFailure.Cancelled
            ? new PlaylistSessionInput.Outcome(PlaylistOutcome.Cancelled, Error: cancellation)
            : new PlaylistSessionInput.Outcome(PlaylistOutcome.Failed, Error: boom);
        (s, q, step) = Step(s, q, failure);
        Assert.IsType<PlaylistSessionAction.DisposeItem>(step.Awaited);

        (s, _, step) = Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok));

        Assert.True(step.Done);
        Assert.Null(step.Awaited);
        Assert.Null(s.Item);
        Assert.DoesNotContain(step.Actions, a => a is PlaylistSessionAction.ReportFatal);
        Assert.Equal(
            why is LoadFailure.NothingLeft or LoadFailure.AtTheFailureLimit,
            step.Actions.Any(a => a is PlaylistSessionAction.ReportItemFailed)
        );

        var result = Assert.IsType<PlaylistSessionAction.CompleteCommand>(step.Actions[^1]).Result;
        switch (why)
        {
            case LoadFailure.FirstItemEver:
                Assert.Equal(PlaylistOutcome.Failed, result.Kind);
                Assert.Same(boom, result.Error);
                break;
            case LoadFailure.Cancelled:
                Assert.Equal(PlaylistOutcome.Cancelled, result.Kind);
                Assert.Same(cancellation, result.Error);
                break;
            case LoadFailure.NothingLeft:
                Assert.Equal(PlaylistOutcome.Failed, result.Kind);
                Assert.Equal("No playlist item could be opened.", result.Error?.Message);
                Assert.Same(boom, result.Error?.InnerException);
                break;
            case LoadFailure.AtTheFailureLimit:
                Assert.Equal(PlaylistOutcome.Failed, result.Kind);
                Assert.Contains("gave up", result.Error?.Message);
                Assert.Same(boom, result.Error?.InnerException);
                break;
        }
    }

    // ── Loops (decision 5 of docs/adr/ADR-0075-looping-on-both-players.md) ───────────

    [Fact]
    public void ALoopRewoundInPlace_ReportsItsCount_WhenTheRewindSucceeds()
    {
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);

        var first = RunToIdle(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
        Assert.Contains(first.Awaited, a => a is PlaylistSessionAction.RewindItem { Command: null });
        Assert.Collection(
            first.Actions.Where(a => a is PlaylistSessionAction.RaiseTransition or PlaylistSessionAction.ReportLoopRestarted),
            a => Assert.IsType<PlaylistSessionAction.RaiseTransition>(a),
            a => AssertLoopRestarted(a, 1)
        );
        Assert.Equal(1, first.State.LoopCount);

        var run = first.State.Item!.KnownRun;
        var second = RunToIdle(first.State, first.Queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, run));
        AssertLoopRestarted(Assert.Single(second.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>()), 2);
    }

    [Fact]
    public void ALoopThatRebuilds_ReportsWhenTheItemIsBackAtItsStart()
    {
        // The rewind fails, so the item is rebuilt, and the loop is reported when its Play succeeds.
        var (playing, playingQueue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);
        var fellBack = RunToIdle(
            playing,
            playingQueue,
            new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun),
            fail: a => a is PlaylistSessionAction.RewindItem
        );
        Assert.Equal(
            new[] { typeof(PlaylistSessionAction.RewindItem), typeof(PlaylistSessionAction.DisposeItem), typeof(PlaylistSessionAction.OpenItem), typeof(PlaylistSessionAction.WarmUpItem), typeof(PlaylistSessionAction.PlayItem) },
            fellBack.Awaited.Select(a => a.GetType())
        );
        AssertLoopRestarted(Assert.Single(fellBack.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>()), 1);

        // A loop that ends while paused is rebuilt, and reported when the new item's warm-up succeeds.
        // The Play that follows reports nothing more.
        var (paused, pausedQueue) = Setup(PlaylistRunState.Paused, Slot.Played, Next.Replay);
        var rebuilt = RunToIdle(paused, pausedQueue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
        Assert.Equal(
            new[] { typeof(PlaylistSessionAction.DisposeItem), typeof(PlaylistSessionAction.OpenItem), typeof(PlaylistSessionAction.WarmUpItem) },
            rebuilt.Awaited.Select(a => a.GetType())
        );
        AssertLoopRestarted(Assert.Single(rebuilt.Actions.OfType<PlaylistSessionAction.ReportLoopRestarted>()), 1);

        var played = RunToIdle(rebuilt.State, rebuilt.Queue, new PlaylistSessionInput.Play(Command));
        Assert.Contains(played.Awaited, a => a is PlaylistSessionAction.PlayItem);
        Assert.DoesNotContain(played.Actions, a => a is PlaylistSessionAction.ReportLoopRestarted);
        Assert.Equal(1, played.State.LoopCount);
    }

    public static TheoryData<string> NotLoops => new(NotALoop.Keys);

    private static readonly Dictionary<string, Func<(PlaylistSessionState State, PlaylistQueue Queue, List<PlaylistSessionAction> Actions)>> NotALoop = new()
    {
        // Under One on the only item a skip wraps back to it and rewinds in place, but it is a skip.
        ["a skip"] = () =>
        {
            var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);
            return Result(RunToIdle(state with { LoopCount = 2 }, queue, new PlaylistSessionInput.SkipRequested(CurrentGeneration, PlaylistRunState.Playing)));
        },
        ["a jump"] = () =>
        {
            var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Other, jumpPending: true);
            return Result(RunToIdle(state with { LoopCount = 2 }, queue, new PlaylistSessionInput.JumpRequested()));
        },
        // Under One a fault replays the item, by a rebuild. A faulted item did not play to its end.
        ["a fault's rebuild"] = () =>
        {
            var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);
            return Result(RunToIdle(state with { LoopCount = 2 }, queue, new PlaylistSessionInput.Fault(CurrentGeneration, new InvalidOperationException(), TimeSpan.FromSeconds(10))));
        },
        // The repeat's open fails, so the item opened after it is a failed start's successor.
        ["a repeat that fails to start"] = () =>
        {
            var (state, queue) = Setup(PlaylistRunState.Paused, Slot.Played, Next.Replay);
            var opens = 0;
            return Result(RunToIdle(state with { LoopCount = 2 }, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun), fail: a => a is PlaylistSessionAction.OpenItem && opens++ == 0));
        },
        ["an end-of-stream latched before the first Play"] = () =>
        {
            var (state, queue) = Setup(PlaylistRunState.NotStarted, Slot.Unplayed, Next.Replay);
            return Result(RunToIdle(state, queue.LatchAdvance(PlaylistAdvance.EndOfStream), new PlaylistSessionInput.Play(Command)));
        },
        // Two items of one source object, back to back: a replay decision, but a different item.
        ["a hand-off to another item of the same source"] = () =>
        {
            var source = new FakeSource("a");
            var a = new PlaylistItem(source);
            var a2 = new PlaylistItem(source);
            var queue = PlaylistQueue.Create([a, a2], RepeatMode.Off);
            (queue, _) = queue.TakeStart();
            (queue, _) = queue.ReportCurrent(a, Info);
            var state = PlaylistSessionState.Initial with
            {
                Run = PlaylistRunState.Playing,
                Generation = CurrentGeneration,
                LoopCount = 2,
                Item = new PlaylistItemSlot(CurrentGeneration, a, Played: true, AwaitsPlay: false, CurrentRun, Info),
            };
            return Result(RunToIdle(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun)));
        },
        ["a repeat completed during disposal"] = () =>
        {
            var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);
            var (s, q, step) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
            var disposing = new PlaylistStepContext(Disposing: true);
            var (after, afterQueue, done) = PlaylistSessionProtocol.Step(s, q, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok, CurrentRun + 1), disposing);
            return (after, afterQueue, [.. step.Actions, .. done.Actions]);
        },
    };

    [Theory]
    [MemberData(nameof(NotLoops))]
    public void ANonLoopStart_ReportsNoLoop_AndEndsTheCount(string name)
    {
        var (state, _, actions) = NotALoop[name]();

        Assert.DoesNotContain(actions, a => a is PlaylistSessionAction.ReportLoopRestarted);
        if (name != "a repeat completed during disposal")
        {
            Assert.Contains(actions, a => a is PlaylistSessionAction.RaiseTransition);
            Assert.Equal(0, state.LoopCount);
        }
    }

    [Fact]
    public void LoopUnderWay_HoldsFromTheLoopsTakeUntilItsInputIsHandled()
    {
        var (state, queue) = Setup(PlaylistRunState.Playing, Slot.Played, Next.Replay);
        Assert.False(state.LoopUnderWay);

        var (rewinding, q1, _) = Step(state, queue, new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun));
        Assert.True(rewinding.LoopUnderWay);

        var (started, q2, _) = Step(rewinding, q1, new PlaylistSessionInput.Outcome(PlaylistOutcome.Ok, CurrentRun + 1));
        Assert.True(started.LoopUnderWay);

        var (handled, _, done) = Step(started, q2, new PlaylistSessionInput.Continue());
        Assert.True(done.Done);
        Assert.False(handled.LoopUnderWay);

        // A skip's rewind is not a loop.
        var (skipping, _, _) = Step(state, queue, new PlaylistSessionInput.SkipRequested(CurrentGeneration, PlaylistRunState.Playing));
        Assert.IsType<PlaylistSessionWork.Replaying>(skipping.Work);
        Assert.False(skipping.LoopUnderWay);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed record Drive(
        PlaylistSessionState State,
        PlaylistQueue Queue,
        List<PlaylistSessionAction> Actions,
        List<PlaylistSessionAction> Awaited
    );

    private static (PlaylistSessionState, PlaylistQueue, List<PlaylistSessionAction>) Result(Drive drive) =>
        (drive.State, drive.Queue, drive.Actions);

    /// <summary>
    /// Steps <paramref name="input"/> until it is handled, answering each awaited item call: an open
    /// succeeds with metadata, a rewind or seek advances the run, and any call
    /// <paramref name="fail"/> picks fails. Collects the immediate actions and the awaited calls.
    /// </summary>
    private static Drive RunToIdle(
        PlaylistSessionState state,
        PlaylistQueue queue,
        PlaylistSessionInput input,
        Func<PlaylistSessionAction, bool>? fail = null
    )
    {
        var actions = new List<PlaylistSessionAction>();
        var awaited = new List<PlaylistSessionAction>();
        var (s, q, step) = Step(state, queue, input);
        for (var guard = 0; guard < 50; guard++)
        {
            actions.AddRange(step.Actions);
            if (step.Awaited is { } call)
            {
                awaited.Add(call);
                var run = s.Item?.KnownRun ?? 0;
                PlaylistSessionInput.Outcome outcome = fail?.Invoke(call) == true
                    ? new(PlaylistOutcome.Failed, run, Error: new InvalidOperationException("boom"))
                    : call switch
                    {
                        PlaylistSessionAction.OpenItem => new(PlaylistOutcome.Ok, 0, Info),
                        PlaylistSessionAction.RewindItem or PlaylistSessionAction.SeekItem => new(PlaylistOutcome.Ok, run + 1),
                        _ => new(PlaylistOutcome.Ok, run),
                    };
                (s, q, step) = Step(s, q, outcome);
            }
            else if (!step.Done)
            {
                (s, q, step) = Step(s, q, new PlaylistSessionInput.Continue());
            }
            else
            {
                return new Drive(s, q, actions, awaited);
            }
        }
        throw new InvalidOperationException("The input was not handled within 50 steps.");
    }

    private static (PlaylistSessionState State, PlaylistQueue Queue, PlaylistSessionStep Step) Step(
        PlaylistSessionState state,
        PlaylistQueue queue,
        PlaylistSessionInput input
    ) => PlaylistSessionProtocol.Step(state, queue, input, default);

    /// <summary>
    /// A state and queue for a row. The queue's current item is <c>a</c>, taken and started. What an
    /// advance takes next is <paramref name="next"/>.
    /// </summary>
    private static (PlaylistSessionState State, PlaylistQueue Queue) Setup(
        PlaylistRunState run,
        Slot slot,
        Next next,
        bool jumpPending = false,
        int extraItems = 1
    )
    {
        var a = new PlaylistItem(new FakeSource("a"));
        var others = new[] { "b", "c", "d" }
            .Take(extraItems)
            .Select(n => new PlaylistItem(new FakeSource(n)));

        var queue = next switch
        {
            Next.Other => PlaylistQueue.Create([a, .. others], RepeatMode.Off),
            Next.Nothing => PlaylistQueue.Create([a], RepeatMode.Off),
            _ => PlaylistQueue.Create([a], RepeatMode.One),
        };
        (queue, _) = queue.TakeStart();
        (queue, _) = queue.ReportCurrent(a, Info);
        if (jumpPending)
            (queue, _) = queue.RequestJump(queue.Playlist.Count > 1 ? queue.Playlist[1] : a);

        var item = slot == Slot.None
            ? null
            : new PlaylistItemSlot(CurrentGeneration, a, slot == Slot.Played, AwaitsPlay: false, CurrentRun, Info);

        return (
            PlaylistSessionState.Initial with { Run = run, Item = item, Generation = CurrentGeneration },
            queue
        );
    }

    private static PlaylistRunState R(Run run) => (PlaylistRunState)(int)run;

    /// <summary>The queue after as many failures in a row as the failure guard tolerates.</summary>
    private static PlaylistQueue AtTheFailureLimit(PlaylistQueue queue)
    {
        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            (queue, _) = queue.ItemFailed(TimeSpan.Zero, TimeSpan.Zero);
        return queue;
    }

    private static PlaylistSessionInput CommandNamed(string name) =>
        Commands().Single(c => c.GetType().Name == name);

    private static (PlaylistSessionState State, PlaylistQueue Queue) Setup(
        Run run,
        Slot slot,
        Next next,
        bool jumpPending = false,
        int extraItems = 1
    ) => Setup(R(run), slot, next, jumpPending, extraItems);

    private static IEnumerable<PlaylistSessionInput> Notifications(PlaylistRunState run) =>
        [
            new PlaylistSessionInput.EndOfStream(CurrentGeneration, CurrentRun),
            new PlaylistSessionInput.Fault(CurrentGeneration, new InvalidOperationException(), TimeSpan.Zero),
            new PlaylistSessionInput.SkipRequested(CurrentGeneration, run),
            new PlaylistSessionInput.JumpRequested(),
        ];

    private static IEnumerable<PlaylistSessionInput> Commands() =>
        [
            new PlaylistSessionInput.Initialize(Command),
            new PlaylistSessionInput.WarmUp(Command),
            new PlaylistSessionInput.Play(Command),
            new PlaylistSessionInput.Pause(Command),
            new PlaylistSessionInput.Seek(Command, TimeSpan.FromSeconds(1)),
            new PlaylistSessionInput.Rewind(Command),
        ];

    /// <summary>Reads the decision a step made from what it asks of the shell.</summary>
    private static Decision Classify(PlaylistQueue before, PlaylistQueue after, PlaylistSessionStep step)
    {
        if (step.Actions.OfType<PlaylistSessionAction.ReportFatal>().Any())
            return Decision.Fatal;

        switch (step.Awaited)
        {
            case PlaylistSessionAction.RewindItem { Command: null }:
                return Decision.Rewind;
            case PlaylistSessionAction.PauseItem { Command: null }:
                return Decision.PauseThenEnd;
            case PlaylistSessionAction.DisposeItem:
                return Decision.Rebuild;
            case PlaylistSessionAction.OpenItem { Command: null }:
                return Decision.Open;
            case not null:
                return Decision.ItemCall;
        }

        if (step.Actions.OfType<PlaylistSessionAction.ReportEndOfStream>().Any())
            return Decision.End;
        if (step.Actions.OfType<PlaylistSessionAction.CompleteCommand>().Any())
            return Decision.Complete;
        if (after.LatchedAdvance is not null && before.LatchedAdvance is null)
            return Decision.Latch;

        Assert.True(step.Done, "A step with nothing awaited and nothing decided must be done.");
        Assert.All(step.Actions, a => Assert.IsType<PlaylistSessionAction.Log>(a));
        return Decision.Drop;
    }
    /// <summary>
    /// A failure report names the item that failed, not just its display name. Asserting on the
    /// fields rather than on record equality is deliberate: <c>PlaylistItem</c> compares by
    /// reference, so a value comparison would need the exact instance the queue built and would
    /// say nothing about which item the report picked.
    /// </summary>
    private static void AssertItemFailed(
        PlaylistSessionAction action,
        string source,
        PlaylistItemFailure what,
        Exception? error
    )
    {
        var failed = Assert.IsType<PlaylistSessionAction.ReportItemFailed>(action);
        Assert.Equal(source, failed.Item.Source.DisplayName);
        Assert.Equal(what, failed.What);
        Assert.Same(error, failed.Error);
    }

    /// <summary>A loop report names the item that looped, and counts consecutive loops of it.</summary>
    private static void AssertLoopRestarted(PlaylistSessionAction action, int count)
    {
        var loop = Assert.IsType<PlaylistSessionAction.ReportLoopRestarted>(action);
        Assert.Equal(count, loop.LoopCount);
        Assert.NotNull(loop.Item);
    }
}
