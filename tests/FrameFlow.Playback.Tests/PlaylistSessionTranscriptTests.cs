namespace FrameFlow.Playback.Tests;

/// <summary>
/// Transcripts of <see cref="PlaylistSession"/> over fake item runtimes: a sequence of controller
/// calls, item notifications and queue requests, and the effects the session must produce.
/// <see cref="PlaylistSessionRig"/> describes the transcript's lines.
/// </summary>
/// <remarks>
/// These pin today's behaviour before the session is rewritten as a pure protocol, step 1 of the
/// draft ADR <c>docs/adr/playlist-session-protocol.md</c>. Each one names the change whose review
/// settled it. The integration tests for the same changes stay, for the mechanism: decode, present
/// and rewind.
/// </remarks>
public sealed class PlaylistSessionTranscriptTests
{
    private static readonly InvalidOperationException Boom = new("boom");

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
    /// </summary>
    [Fact]
    public async Task FaultBeforeTheFirstPlay_IsFatal_AndNothingAdvances()
    {
        await using var rig = await PlaylistSessionRig.LoadedAsync(RepeatMode.Off, "a", "b");

        rig.Runtime("a#1").RaiseFault(Boom);
        await rig.SettleAsync();

        Assert.Equal(["ctl.Fatal(boom)"], rig.TakeLog());
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
                "ctl.RecoverableError(Playlist item 'b' could not be started: boom)",
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
            if (t.Source.DisplayName == "b")
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
