namespace FrameFlow.Playback.Tests;

/// <summary>
/// Transcripts of <see cref="PlaylistSession"/> over fake item runtimes: a sequence of controller
/// calls, item notifications and queue requests, and the effects the session must produce.
/// <see cref="PlaylistSessionRig"/> describes the transcript's lines.
/// </summary>
/// <remarks>
/// These pinned the session's behaviour before it was rewritten as a pure protocol, in step 1 of the
/// <c>docs/adr/ADR-0076-playlist-session-protocol.md</c>, and ran unchanged against the rewrite in
/// step 3. Each one names the change whose review settled it. The integration tests for the same
/// changes stay, for the mechanism: decode, present and rewind. The rules themselves are tabled in
/// <c>PlaylistSessionProtocolTests</c>.
/// </remarks>
public sealed class PlaylistSessionTranscriptTests
{
    private static readonly InvalidOperationException Boom = new("boom");

    [Fact]
    public async Task CurrentItem_TracksTheStartedItem_AndIsNullBeforeTheFirst()
    {
        // The controller pulls CurrentItem on a position tick to name a stalled item, rather than
        // being pushed it, because a stall is detected by the controller's own fold and not
        // reported by the session. So it has to answer for the FIRST item too, which the
        // current-item-changed callback does not fire for.
        await using var rig = PlaylistSessionRig.Create(RepeatMode.Off, "a", "b");

        Assert.Null(rig.Session.Presentation.CurrentItem);

        await rig.Session.InitializeAsync(rig.PlaylistItem("a").Source);
        await rig.Session.WarmUpAsync();
        await rig.Session.PlayAsync();
        Assert.Same(rig.PlaylistItem("a"), rig.Session.Presentation.CurrentItem);

        rig.Runtime("a#1").RaiseEndOfStream();
        await rig.SettleAsync();
        Assert.Same(rig.PlaylistItem("b"), rig.Session.Presentation.CurrentItem);
    }

    [Fact]
    public async Task LoadPlayHandOffAndEnd()
    {
        await using var rig = PlaylistSessionRig.Create(RepeatMode.Off, "a", "b");

        await rig.Session.InitializeAsync(rig.PlaylistItem("a").Source);
        await rig.Session.WarmUpAsync();
        await rig.Session.PlayAsync();
        Assert.Equal(["a#1.Open", "transition(a)", "a#1.WarmUp", "a#1.Play"], rig.TakeLog());

        rig.Runtime("a#1").RaiseEndOfStream();
        await rig.SettleAsync();
        Assert.Equal(
            [
                "a#1.Dispose",
                "clock.Stop",
                "b#1.Open",
                "b#1.WarmUp",
                "b#1.Play",
                "ctl.ItemChanged(b)",
                "transition(b)",
            ],
            rig.TakeLog()
        );

        rig.Runtime("b#1").RaiseEndOfStream();
        await rig.SettleAsync();
        Assert.Equal(["ctl.EndOfStream"], rig.TakeLog());
    }

    /// <summary>
    /// #191 (#180): a first item that faulted before the first play was skipped while the
    /// controller was still loading. It goes to the controller as a single source's fault would.
    /// A Play the controller sent before it saw that error does not start the item; the explorer in
    /// step 4 of the protocol ADR found that one did.
    /// </summary>
    [Fact]
    public async Task FaultBeforeTheFirstPlay_IsFatal_AndNothingAdvances()
    {
        await using var rig = await PlaylistSessionRig.LoadedAsync(RepeatMode.Off, "a", "b");

        rig.Runtime("a#1").RaiseFault(Boom);
        await rig.SettleAsync();

        Assert.Equal(["ctl.Fatal(boom)"], rig.TakeLog());

        await rig.Session.PlayAsync();
        await rig.SettleAsync();

        Assert.Empty(rig.TakeLog());
    }

    /// <summary>
    /// #194 (#182): a Play queued behind the advance that ended the queue replaced <c>Ended</c>,
    /// which let a later skip start an item while the controller said <c>Ended</c>.
    /// </summary>
    [Fact]
    public async Task PlayQueuedBehindTheEndOfTheQueue_LeavesTheSessionEnded()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a");

        // A skip on the last item pauses it before it ends the queue. The pause is held, so the
        // Play reaches the session while that advance is still under way.
        var pause = rig.Hold("a", ItemOp.Pause);
        rig.Coordinator.RequestSkip();
        await pause.EnteredAsync();
        var play = rig.Session.PlayAsync();
        pause.Release();
        await play;
        await rig.SettleAsync();

        Assert.Equal(["a#1.Pause", "ctl.EndOfStream"], rig.TakeLog());

        rig.Enqueue("c");
        rig.Coordinator.RequestSkip();
        await rig.SettleAsync();

        Assert.Empty(rig.TakeLog());
    }

    /// <summary>
    /// #194 (#182): as <see cref="PlayQueuedBehindTheEndOfTheQueue_LeavesTheSessionEnded"/>, for a
    /// Pause.
    /// </summary>
    [Fact]
    public async Task PauseQueuedBehindTheEndOfTheQueue_LeavesTheSessionEnded()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a");

        var hold = rig.Hold("a", ItemOp.Pause);
        rig.Coordinator.RequestSkip();
        await hold.EnteredAsync();
        var pause = rig.Session.PauseAsync();
        hold.Release();
        await pause;
        await rig.SettleAsync();

        Assert.Equal(["a#1.Pause", "ctl.EndOfStream", "a#1.Pause"], rig.TakeLog());

        rig.Enqueue("c");
        rig.Coordinator.RequestSkip();
        await rig.SettleAsync();

        Assert.Empty(rig.TakeLog());
    }

    /// <summary>
    /// #197 (#170): a seek that reaches the session at <c>Ended</c> without the warm-up of a seek
    /// out of <c>Ended</c> was dispatched before the controller saw the end-of-stream, and is
    /// dropped.
    /// </summary>
    [Fact]
    public async Task SeekQueuedBehindTheEndOfTheQueue_IsDropped()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a");

        var pause = rig.Hold("a", ItemOp.Pause);
        rig.Coordinator.RequestSkip();
        await pause.EnteredAsync();
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1));
        pause.Release();
        await seek;
        await rig.SettleAsync();

        Assert.Equal(["a#1.Pause", "ctl.EndOfStream"], rig.TakeLog());
    }

    /// <summary>
    /// #194 (#182): the gate was released before a warm-up finished. The warm-up of a seek out of
    /// <c>Ended</c> holds it throughout, so a jump requested meanwhile waits, and the kept item
    /// is not replaced while it warms.
    /// </summary>
    [Fact]
    public async Task WarmUpOutOfEnded_HoldsOffAJumpUntilItFinishes()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        rig.Runtime("a#1").RaiseEndOfStream();
        await rig.SettleAsync();
        rig.Runtime("b#1").RaiseEndOfStream();
        await rig.SettleAsync();
        Assert.Contains("ctl.EndOfStream", rig.TakeLog());

        var hold = rig.Hold("b", ItemOp.WarmUp);
        var warmUp = rig.Session.WarmUpAsync();
        await hold.EnteredAsync();
        Assert.Equal(JumpRequest.Pending, rig.Coordinator.RequestJump(rig.PlaylistItem("a")));

        // The jump has not replaced the item while it warms.
        Assert.Equal(["b#1.WarmUp"], rig.TakeLog());

        hold.Release();
        await warmUp;
        await rig.SettleAsync();

        Assert.Equal(
            [
                "b#1.Dispose",
                "clock.Stop",
                "a#2.Open",
                "a#2.WarmUp",
                "ctl.ItemChanged(a)",
                "transition(a)",
            ],
            rig.TakeLog()
        );
    }

    /// <summary>
    /// #194 (#182): the seek out of <c>Ended</c> is recorded at the end of its warm-up. A skip
    /// requested during that warm-up was requested at <c>Ended</c>, so it is dropped, even though the
    /// session is paused by the time it is handled.
    /// </summary>
    [Fact]
    public async Task SkipDuringTheWarmUpOutOfEnded_IsDropped()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        rig.Runtime("a#1").RaiseEndOfStream();
        await rig.SettleAsync();
        rig.Runtime("b#1").RaiseEndOfStream();
        await rig.SettleAsync();
        Assert.Contains("ctl.EndOfStream", rig.TakeLog());

        var hold = rig.Hold("b", ItemOp.WarmUp);
        var warmUp = rig.Session.WarmUpAsync();
        await hold.EnteredAsync();
        rig.Coordinator.RequestSkip();
        hold.Release();
        await warmUp;
        await rig.SettleAsync();

        Assert.Equal(["b#1.WarmUp"], rig.TakeLog());
    }

    /// <summary>
    /// #194 (#182): a skip latched before the first play under <see cref="RepeatMode.One"/> rewound
    /// an item that had never played, which left its clocks stopped. The first play rebuilds it.
    /// </summary>
    [Fact]
    public async Task LatchedSkipUnderOne_RebuildsTheUnplayedItem()
    {
        await using var rig = await PlaylistSessionRig.LoadedAsync(RepeatMode.One, "a");

        rig.Coordinator.RequestSkip();
        await rig.SettleAsync();
        Assert.Empty(rig.TakeLog());

        await rig.Session.PlayAsync();
        await rig.SettleAsync();

        Assert.Equal(
            [
                "a#1.Dispose",
                "clock.Stop",
                "a#2.Open",
                "a#2.WarmUp",
                "a#2.Play",
                "ctl.ItemChanged(a)",
                "transition(a, wrapped)",
            ],
            rig.TakeLog()
        );
    }

    /// <summary>
    /// #194 (#182): an item an advance opened while paused could fail to start inside
    /// <see cref="PlaylistSession.PlayAsync"/> and escape it. It is reported and skipped as a
    /// failed start inside an advance is.
    /// </summary>
    [Fact]
    public async Task DeferredStartFailure_IsSkippedLikeAFailedStart()
    {
        await using var rig = await PausedOnSecondItemAsync();

        rig.Fail("b", ItemOp.Play, Boom);
        await rig.Session.PlayAsync();
        await rig.SettleAsync();

        Assert.Equal(
            [
                "b#1.Play",
                "ctl.ItemFailed(b,CouldNotStart,Playlist item 'b' could not be started: boom)",
                "b#1.Dispose",
                "clock.Stop",
                "c#1.Open",
                "c#1.WarmUp",
                "c#1.Play",
                "ctl.ItemChanged(c)",
                "transition(c)",
            ],
            rig.TakeLog()
        );
    }

    /// <summary>
    /// #194 (#182): a cancelled Play of an item waiting to start was counted as an item failure.
    /// The caller gets the cancellation, and the item stays.
    /// </summary>
    [Fact]
    public async Task CancelledPlay_OfAnItemWaitingToStart_KeepsTheItem()
    {
        await using var rig = await PausedOnSecondItemAsync();

        var hold = rig.Hold("b", ItemOp.Play);
        using var cts = new CancellationTokenSource();
        var play = rig.Session.PlayAsync(cts.Token);
        await hold.EnteredAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await play);
        await rig.SettleAsync();
        Assert.Equal(["b#1.Play"], rig.TakeLog());

        await rig.Session.PlayAsync();
        await rig.SettleAsync();
        Assert.Equal(["b#1.Play"], rig.TakeLog());
    }

    /// <summary>
    /// #199 (#171): a jump requested while an advance starts its item is taken as soon as that item
    /// has started, in the same advance. A Pause that arrived first waits for it, and pauses the
    /// jump's target.
    /// </summary>
    [Fact]
    public async Task JumpDuringAnAdvance_IsTakenBeforeAWaitingPause()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b", "c");

        var play = rig.Hold("b", ItemOp.Play);
        rig.Runtime("a#1").RaiseEndOfStream();
        await play.EnteredAsync();
        var pause = rig.Session.PauseAsync();
        Assert.Equal(JumpRequest.Pending, rig.Coordinator.RequestJump(rig.PlaylistItem("c")));
        play.Release();
        await pause;
        await rig.SettleAsync();

        Assert.Equal(JumpTakenBeforeTheWaitingPause, rig.TakeLog());
    }

    /// <summary>
    /// #199 (#171): as <see cref="JumpDuringAnAdvance_IsTakenBeforeAWaitingPause"/>, for a jump a
    /// <c>SourceTransitioned</c> subscriber requests when the advanced item starts.
    /// </summary>
    [Fact]
    public async Task JumpFromATransitionSubscriber_IsTakenBeforeAWaitingPause()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b", "c");
        rig.OnSourceTransitioned(t =>
        {
            if (t.Item.Source.DisplayName == "b")
                rig.Coordinator.RequestJump(rig.PlaylistItem("c"));
        });

        var play = rig.Hold("b", ItemOp.Play);
        rig.Runtime("a#1").RaiseEndOfStream();
        await play.EnteredAsync();
        var pause = rig.Session.PauseAsync();
        play.Release();
        await pause;
        await rig.SettleAsync();

        Assert.Equal(JumpTakenBeforeTheWaitingPause, rig.TakeLog());
    }

    /// <summary>
    /// #199 (#171): a jump requested while the gate is held by work that does not look for jumps,
    /// as it is just after an advance's last check, is taken by the advance its own request starts.
    /// </summary>
    [Fact]
    public async Task JumpDuringASeek_IsTakenWhenTheSeekCompletes()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b", "c");

        var hold = rig.Hold("a", ItemOp.Seek);
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1));
        await hold.EnteredAsync();
        Assert.Equal(JumpRequest.Pending, rig.Coordinator.RequestJump(rig.PlaylistItem("c")));
        hold.Release();
        await seek;
        await rig.SettleAsync();

        Assert.Equal(
            [
                "a#1.Seek(00:00:01)",
                "a#1.Dispose",
                "clock.Stop",
                "c#1.Open",
                "c#1.WarmUp",
                "c#1.Play",
                "ctl.ItemChanged(c)",
                "transition(c)",
            ],
            rig.TakeLog()
        );
    }

    /// <summary>
    /// #197 (#170): an end-of-stream raised by a run that a seek then stopped is stale, and one
    /// raised by the run after the seek is not.
    /// </summary>
    [Fact]
    public async Task EndOfStreamRaisedDuringASeek_IsDropped_AndOneAfterItIsNot()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");

        var hold = rig.Hold("a", ItemOp.Seek);
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1));
        await hold.EnteredAsync();
        rig.Runtime("a#1").RaiseEndOfStream();
        hold.Release();
        await seek;
        await rig.SettleAsync();

        Assert.Equal(["a#1.Seek(00:00:01)"], rig.TakeLog());

        rig.Runtime("a#1").RaiseEndOfStream();
        await rig.SettleAsync();

        Assert.Equal(
            [
                "a#1.Dispose",
                "clock.Stop",
                "b#1.Open",
                "b#1.WarmUp",
                "b#1.Play",
                "ctl.ItemChanged(b)",
                "transition(b)",
            ],
            rig.TakeLog()
        );
    }

    /// <summary>
    /// #197 (#170): an end-of-stream raised before a seek, whose advance starts only after the
    /// seek has replaced the run, is stale. This is the race the run number was added for.
    /// </summary>
    [Fact]
    public async Task EndOfStreamWhoseAdvanceStartsAfterASeek_IsDropped()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");

        rig.DeferHops();
        rig.Runtime("a#1").RaiseEndOfStream();
        await rig.Session.SeekAsync(TimeSpan.FromSeconds(1));
        rig.StartDeferredHops();
        await rig.SettleAsync();

        Assert.Equal(["a#1.Seek(00:00:01)"], rig.TakeLog());
    }

    /// <summary>
    /// A skip requested before the advance for an end-of-stream has started ends the same item,
    /// so the two collapse into one advance.
    /// </summary>
    [Fact]
    public async Task SkipRequestedBeforeAnEndOfStreamsAdvanceStarts_AdvancesOnce()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b", "c");

        rig.DeferHops();
        rig.Runtime("a#1").RaiseEndOfStream();
        rig.Coordinator.RequestSkip();
        rig.StartDeferredHops();
        await rig.SettleAsync();

        Assert.Equal(
            [
                "a#1.Dispose",
                "clock.Stop",
                "b#1.Open",
                "b#1.WarmUp",
                "b#1.Play",
                "ctl.ItemChanged(b)",
                "transition(b)",
            ],
            rig.TakeLog()
        );
    }

    /// <summary>
    /// Step 3 of the protocol ADR: disposal cancels the item operation in flight. The open an
    /// advance was waiting on ends, its runtime is disposed, and nothing is reported. Before step 3,
    /// disposal waited for the open to finish.
    /// </summary>
    [Fact]
    public async Task DisposalDuringAnAdvancesOpen_CancelsTheOpen()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");

        var open = rig.Hold("b", ItemOp.Open);
        rig.Runtime("a#1").RaiseEndOfStream();
        await open.EnteredAsync();
        Assert.Equal(["a#1.Dispose", "clock.Stop", "b#1.Open"], rig.TakeLog());

        await rig.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["b#1.Dispose"], rig.TakeLog());
    }

    /// <summary>
    /// A skip requested before the first Play is latched on the queue, so the next session's first
    /// Play takes it. That holds when disposal starts before the session has handled the skip.
    /// </summary>
    [Fact]
    public async Task SkipBeforeTheFirstPlay_OutlivesADisposalThatStartsFirst()
    {
        await using var rig = PlaylistSessionRig.Create(RepeatMode.Off, "a", "b");
        await rig.Session.InitializeAsync(rig.PlaylistItem("a").Source);
        rig.TakeLog();

        var hold = rig.Hold("a", ItemOp.WarmUp);
        var warmUp = rig.Session.WarmUpAsync();
        await hold.EnteredAsync();
        rig.Coordinator.RequestSkip();
        await rig.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await warmUp;

        Assert.Equal(["a#1.WarmUp", "a#1.Dispose"], rig.TakeLog());
        Assert.Equal(PlaylistAdvance.Skip, rig.Coordinator.Queue.LatchedAdvance);
    }

    /// <summary>
    /// A command after disposal throws, as the disposed gate did before step 3, and two disposals at
    /// once both complete, having disposed the item once.
    /// </summary>
    [Fact]
    public async Task AfterDisposal_ACommandThrows_AndASecondDisposalCompletes()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a");

        await Task.WhenAll(rig.Session.DisposeAsync().AsTask(), rig.Session.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["a#1.Dispose"], rig.TakeLog());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rig.Session.PlayAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await rig.Session.SeekAsync(TimeSpan.FromSeconds(1))
        );
        Assert.Null(rig.Session.MediaInfo);
    }

    /// <summary>
    /// #197 (#170), decision 5 of the protocol ADR: a seek cancelled after it has advanced the run
    /// still reports the new run, so an end-of-stream raised by the run before the seek is stale.
    /// </summary>
    [Fact]
    public async Task EndOfStreamBeforeASeekCancelledAfterTheRunAdvanced_IsDropped()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");

        rig.DeferHops();
        rig.Runtime("a#1").RaiseEndOfStream();

        var hold = rig.Hold("a", ItemOp.Seek, runAdvancesFirst: true);
        using var cts = new CancellationTokenSource();
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1), cts.Token);
        await hold.EnteredAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await seek);

        rig.StartDeferredHops();
        await rig.SettleAsync();

        Assert.Equal(["a#1.Seek(00:00:01)"], rig.TakeLog());
        Assert.Equal(1, rig.Runtime("a#1").RunNumber);
    }

    private static readonly string[] JumpTakenBeforeTheWaitingPause =
    [
        "a#1.Dispose",
        "clock.Stop",
        "b#1.Open",
        "b#1.WarmUp",
        "b#1.Play",
        "ctl.ItemChanged(b)",
        "transition(b)",
        "b#1.Dispose",
        "clock.Stop",
        "c#1.Open",
        "c#1.WarmUp",
        "c#1.Play",
        "ctl.ItemChanged(c)",
        "transition(c)",
        "c#1.Pause",
    ];

    /// <summary>
    /// The looping record's decision 5: a skip requested while an in-place rewind is under way is a
    /// later input. The loop is reported first. The skip's start is not a loop and ends the run of
    /// loops, so the next loop counts from 1 again.
    /// </summary>
    [Fact]
    public async Task SkipDuringAnInPlaceRewind_TakesEffectAfterTheLoopIsReported()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.One, "a");

        var hold = rig.Hold("a", ItemOp.Rewind);
        rig.Runtime("a#1").RaiseEndOfStream();
        await hold.EnteredAsync();
        rig.Coordinator.RequestSkip();
        hold.Release();
        await rig.SettleAsync();

        Assert.Equal(
            [
                "a#1.Rewind",
                "transition(a)",
                "ctl.LoopRestarted(1,a)",
                // The skip wraps back to the only item and rewinds it in place, but it is a skip.
                "a#1.Rewind",
                "transition(a, wrapped)",
            ],
            rig.TakeLog()
        );

        rig.Runtime("a#1").RaiseEndOfStream();
        await rig.SettleAsync();

        Assert.Equal(["a#1.Rewind", "transition(a)", "ctl.LoopRestarted(1,a)"], rig.TakeLog());
    }

    /// <summary>
    /// The looping record's decision 6: removing the current item while its rewind is under way does
    /// not undo the repeat, so the session keeps expecting it until the rewind completes, and a
    /// rewind that hangs there is still watched. Once the input is handled the queue answers alone.
    /// </summary>
    [Fact]
    public async Task RemovingTheItemDuringAnInPlaceRewind_KeepsARepeatExpected_UntilTheRewindCompletes()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.One, "a");
        Assert.True(rig.Session.Presentation.ExpectsRepeat);
        var a = rig.PlaylistItem("a");

        var hold = rig.Hold("a", ItemOp.Rewind);
        rig.Runtime("a#1").RaiseEndOfStream();
        await hold.EnteredAsync();
        Assert.True(rig.Coordinator.Remove(a));

        Assert.False(rig.Coordinator.Queue.ExpectsRepeat);
        Assert.True(rig.Session.Presentation.ExpectsRepeat);

        hold.Release();
        await rig.SettleAsync();

        Assert.Equal(["a#1.Rewind", "transition(a)", "ctl.LoopRestarted(1,a)"], rig.TakeLog());
        Assert.False(rig.Session.Presentation.ExpectsRepeat);
    }

    /// <summary>
    /// A rig paused on <c>b</c> of <c>a, b, c</c>: <c>a</c> played, was paused and was skipped,
    /// so <c>b</c> is opened and warmed and waits for a Play to start it.
    /// </summary>
    private static async Task<PlaylistSessionRig> PausedOnSecondItemAsync()
    {
        var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b", "c");
        await rig.Session.PauseAsync();
        rig.Coordinator.RequestSkip();
        await rig.SettleAsync();
        Assert.Equal(
            [
                "a#1.Pause",
                "a#1.Dispose",
                "clock.Stop",
                "b#1.Open",
                "b#1.WarmUp",
                "ctl.ItemChanged(b)",
                "transition(b)",
            ],
            rig.TakeLog()
        );
        return rig;
    }
}
