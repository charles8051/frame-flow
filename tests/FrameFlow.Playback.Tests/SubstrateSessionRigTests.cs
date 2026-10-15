// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Playback.Core;
using Step = FrameFlow.Playback.SubstrateSession.Step;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The item runtime's ordering, on the rig (ADR-0086, decision 6): <see cref="SubstrateSession"/>
/// over <see cref="FakeItemMedia"/>, which records each call that stands for a native one and
/// flags any made while a run's work is in flight. Each defect of the family the ADR names is a
/// test here: a seek whose wait is cut short and then a dispose (#707), a superseding seek during
/// an unwind, a scrub, an end of stream at the moment of a seek, a stop while the pump is parked,
/// a play after a cut-short seek (#714), a runtime prepared ahead, and an open that fails late
/// (#725).
/// </summary>
/// <remarks>
/// A run is held by work that ignores its cancellation until the test releases it, as a sink of
/// a caller's own may. The order is read from the fake's log and the session's steps, and every
/// race is driven by acting at a step the session takes or a signal the fake raises; the bound
/// only limits a failure.
/// </remarks>
public sealed class SubstrateSessionRigTests
{
    private static readonly TimeSpan Bound = ItemRuntimeRig.Bound;

    private static readonly string[] Teardown =
    [
        "Free(Pacer)",
        "Free(Pipeline)",
        "Free(AudioDecoder)",
        "Free(VideoDecoder)",
        "Free(Demux)",
    ];

    // ── #707: a cut-short wait forgets nothing ──────────────────────────────

    [Fact]
    public async Task ADisposeAfterASeekCutShortWhileWaitingForTheRun_FreesNothingUntilTheRunHasEnded()
    {
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        await rig.SeekCutShortAsync(TimeSpan.FromSeconds(1));

        var graph = rig.Media.Latest(ItemWork.Graph);
        var waiting = rig.Next(Step.WaitingForRun);
        var dispose = rig.Session.DisposeAsync().AsTask();
        await waiting.WaitAsync(Bound);
        graph.Release();
        await dispose.WaitAsync(Bound);

        Assert.Empty(rig.Log.Violations);
        Assert.Equal(Teardown, rig.Log.Of("Free("));
        Assert.True(rig.Log.IndexOf("Graph ended") < rig.Log.IndexOf("Free("), "A free came before the run's graph ended.");
        Assert.Equal(0, rig.Log.Count("Seek("));
        Assert.True(rig.Media.Quiet);
        Assert.Empty(rig.WorkerFaults);
    }

    [Fact]
    public async Task ASeekSupersedingOneCutShort_WaitsForTheRun_BeforeItSeeksTheDemuxer()
    {
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        await rig.SeekCutShortAsync(TimeSpan.FromSeconds(1));

        var graph = rig.Media.Latest(ItemWork.Graph);
        var waiting = rig.Next(Step.WaitingForRun);
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(2)).AsTask();
        await waiting.WaitAsync(Bound);
        graph.Release();
        await seek.WaitAsync(Bound);

        // The next run's work starts on the pool: counted once it has.
        await rig.Media.Started(ItemWork.Pump, 2).WaitAsync(Bound);
        await rig.Media.Started(ItemWork.Graph, 2).WaitAsync(Bound);
        Assert.Empty(rig.Log.Violations);
        Assert.Equal(["Seek(2)"], rig.Log.Of("Seek("));
        Assert.True(rig.Log.IndexOf("Graph ended") < rig.Log.IndexOf("Seek("), "The demuxer was sought before the run's graph ended.");
        Assert.Equal(2, rig.Media.Runs);
        Assert.Equal(["Settle(2)", "ReleaseSettle(2)"], rig.Log.Of("Settle(", "ReleaseSettle("));
        Assert.Equal(1, rig.Media.MostAtOnce(ItemWork.Pump));
        Assert.Equal(1, rig.Media.MostAtOnce(ItemWork.Graph));
        Assert.Equal(1, rig.Session.RunNumber);
        Assert.Empty(rig.WorkerFaults);
    }

    [Fact]
    public async Task AScrub_EndsAtItsLastTarget_WithOneUnwindAndOneSeek()
    {
        // Each seek but the last is cut short while it waits for the run, as a scrub does when it
        // sends the next seek as soon as the last is accepted.
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        for (var i = 1; i <= 8; i++)
            await rig.SeekCutShortAsync(TimeSpan.FromSeconds(0.25 * i));
        var runWhileScrubbing = rig.Session.RunNumber;

        var graph = rig.Media.Latest(ItemWork.Graph);
        var waiting = rig.Next(Step.WaitingForRun);
        var last = rig.Session.SeekAsync(TimeSpan.FromSeconds(3)).AsTask();
        await waiting.WaitAsync(Bound);
        graph.Release();
        await last.WaitAsync(Bound);

        await rig.Media.Started(ItemWork.Graph, 2).WaitAsync(Bound);
        Assert.Empty(rig.Log.Violations);
        Assert.Equal(0, runWhileScrubbing);
        Assert.Equal(1, rig.Session.RunNumber);
        Assert.Equal(["Seek(3)"], rig.Log.Of("Seek("));
        Assert.Equal(1, rig.Log.Count("ResetForSeek"));
        Assert.Equal(2, rig.Media.Runs);
        Assert.Equal(1, rig.Media.MostAtOnce(ItemWork.Graph));
        Assert.Contains("BeginRun(Rebuild, floor 3, end none, hold True)", rig.Log.Calls);
    }

    // ── End of stream and a seek ────────────────────────────────────────────

    [Fact]
    public async Task AnEndOfStreamQueuedWithASeek_IsRaisedInStepOrder_CarryingTheRunThatEnded()
    {
        // The run's end and the seek arrive in one window: the session is held closing the gates
        // for a pause while the pump and the graph end and the seek is sent. The end of stream is
        // raised in step order, before the seek is stepped, so the callback reads the number of the
        // run that ended, not the one the seek starts.
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        var pump = rig.Media.Latest(ItemWork.Pump);
        var graph = rig.Media.Latest(ItemWork.Graph);
        rig.Media.HoldCloseGates();
        var pause = rig.Session.PauseAsync().AsTask();
        await rig.Media.CloseGatesEntered.WaitAsync(Bound);

        pump.Complete();
        graph.Complete();
        await rig.LetGo("the demux pump").WaitAsync(Bound);
        await rig.LetGo("the graph").WaitAsync(Bound); // both ends are queued
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask();
        rig.Media.ReleaseCloseGates();
        await pause.WaitAsync(Bound);
        await seek.WaitAsync(Bound);
        await rig.EndOfStream().WaitAsync(Bound);

        Assert.Equal([0], rig.EndsOfStream);
        Assert.Equal([true], rig.EndsOfStreamInStep);
        Assert.Equal(1, rig.Session.RunNumber);

        // The next run's end of stream carries its own number.
        await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);
        (await rig.Media.Started(ItemWork.Pump, 2).WaitAsync(Bound)).Complete();
        (await rig.Media.Started(ItemWork.Graph, 2).WaitAsync(Bound)).Complete();
        await rig.EndOfStream(2).WaitAsync(Bound);
        Assert.Equal([0, 1], rig.EndsOfStream);
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task AnEndOfStreamDuringASettle_IsRaisedWhileTheSeekStillSettles()
    {
        // A piece of work ends while an awaited action of a command is under way: the new run plays
        // to its end before the seek's settle on its destination frame returns.
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.Media.HoldSettle();
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask();
        await rig.Media.SettleEntered.WaitAsync(Bound);

        (await rig.Media.Started(ItemWork.Pump, 2).WaitAsync(Bound)).Complete();
        (await rig.Media.Started(ItemWork.Graph, 2).WaitAsync(Bound)).Complete();
        await rig.EndOfStream().WaitAsync(Bound);
        rig.Media.ReleaseHeldSettle();
        await seek.WaitAsync(Bound);

        Assert.Equal([1], rig.EndsOfStream);
        Assert.Equal(["Settle(2)", "ReleaseSettle(2)"], rig.Log.Of("Settle(", "ReleaseSettle("));
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task ARunThatPlaysToItsEndAsASeekStopsIt_RaisesNoEndOfStream()
    {
        // The graph finishes delivering just as the seek cancels its run, and does not look at the
        // cancellation: the seek is replacing the run, so the end of stream it reached is not one.
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        rig.Media.Latest(ItemWork.Pump).Complete();

        var graph = rig.Media.Latest(ItemWork.Graph);
        var waiting = rig.Next(Step.WaitingForRun);
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask();
        await waiting.WaitAsync(Bound);
        graph.Finish();
        await seek.WaitAsync(Bound);

        Assert.Empty(rig.EndsOfStream);
        Assert.Equal(1, rig.Session.RunNumber);
        Assert.Empty(rig.Log.Violations);
    }

    // ── A stop reaches parked work ──────────────────────────────────────────

    [Fact]
    public async Task AStopWhileThePumpIsParked_ReachesIt_AndWaitsForIt()
    {
        // The pump has read to the end and its flush waits for room in a decoder queue that only
        // the run's cancellation frees. The graph waits at the gate for what the pump fed.
        await using var rig = ItemRuntimeRig.Create();
        await rig.OpenAsync();
        await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);
        var pump = await rig.Media.Started(ItemWork.Pump).WaitAsync(Bound);

        await rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Bound);

        Assert.True(pump.Ended.IsCompleted);
        Assert.True(pump.Token.IsCancellationRequested);
        Assert.True(rig.Log.IndexOf("Pump ended") < rig.Log.IndexOf("Seek("));
        Assert.Empty(rig.Log.Violations);
    }

    // ── #714: a play after a cut-short seek ─────────────────────────────────

    [Fact]
    public async Task AFirstPlayAfterASeekCutShort_RepositionsAtTheClock_AndStartsARun()
    {
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.OpenAsync();
        await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);
        await rig.SeekCutShortAsync(TimeSpan.FromSeconds(1));

        var graph = rig.Media.Latest(ItemWork.Graph);
        var waiting = rig.Next(Step.WaitingForRun);
        var play = rig.Session.PlayAsync().AsTask();
        await waiting.WaitAsync(Bound);
        graph.Release();
        await play.WaitAsync(Bound);

        Assert.Empty(rig.Log.Violations);
        Assert.Equal(["Seek(0)"], rig.Log.Of("Seek("));
        Assert.Equal(2, rig.Media.Runs);
        await rig.Media.Started(ItemWork.Graph, 2).WaitAsync(Bound);
        Assert.True(rig.Clock.IsRunning);
        Assert.True(rig.Log.LastIndexOf("OpenGates") > rig.Log.LastIndexOf("BeginRun("));
        Assert.Equal(1, rig.Session.RunNumber);
    }

    [Fact]
    public async Task AResumeAfterASeekCutShort_RepositionsWhereItPaused_AndPlaysOn()
    {
        // The controller cancels a seek in progress when a pause arrives, then pauses (#714).
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        await rig.SeekCutShortAsync(TimeSpan.FromSeconds(1));
        await rig.Session.PauseAsync().AsTask().WaitAsync(Bound);
        var pausedAt = rig.Clock.Position;

        var graph = rig.Media.Latest(ItemWork.Graph);
        var waiting = rig.Next(Step.WaitingForRun);
        var play = rig.Session.PlayAsync().AsTask();
        await waiting.WaitAsync(Bound);
        graph.Release();
        await play.WaitAsync(Bound);

        Assert.Empty(rig.Log.Violations);
        Assert.Single(rig.Log.Of("Seek("));
        Assert.Equal(2, rig.Media.Runs);
        Assert.True(rig.Clock.IsRunning);
        Assert.True(rig.Clock.Position >= pausedAt);
        Assert.True(rig.Log.LastIndexOf("OpenGates") > rig.Log.LastIndexOf("BeginRun("));
        Assert.Equal(1, rig.Session.RunNumber);
    }

    // ── #739: a seek that holds pauses the clocks ───────────────────────────

    [Fact]
    public async Task APlayAfterASeekThatHeldAtTheEnd_PresentsNoFrameBeforeTheClockReachesIt()
    {
        // An item with no audio is paced by the session's own wallclock. It plays to its end, and
        // the controller freezes the position clock, as it does on entering Ended. A seek reads the
        // clock paused and holds; a second passes; the play starts the run from the target.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var rig = await ItemRuntimeRig.Create(MediaShape.VideoOnly, clockTime: time).PlayingAsync();
        var pacing = rig.Media.Master!;
        rig.Media.Latest(ItemWork.Pump).Complete();
        rig.Media.Latest(ItemWork.Graph).Complete();
        await rig.EndOfStream().WaitAsync(Bound);
        rig.Clock.Pause();

        var target = TimeSpan.FromSeconds(2);
        await rig.Session.SeekAsync(target).AsTask().WaitAsync(Bound);
        time.Advance(TimeSpan.FromSeconds(1));
        await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);

        Assert.Contains("BeginRun(Rebuild, floor 2, end none, hold False)", rig.Log.Calls);
        Assert.Equal(target, rig.Clock.Position);
        Assert.Equal(target, pacing.Latest);

        // The pacer presents a frame once the clock it paces by reaches the frame's time.
        var frame = TimeSpan.FromMilliseconds(40);
        var presented = pacing.WaitUntilAsync(target + frame).AsTask();
        Assert.False(presented.IsCompleted, "The frame after the target was due at once: the wallclock ran on while the item was paused.");
        time.Advance(frame);
        await presented.WaitAsync(Bound);
        Assert.Empty(rig.Log.Violations);
    }

    // ── A runtime prepared ahead ────────────────────────────────────────────

    [Fact]
    public async Task ARuntimePreparedAhead_TouchesNothingItSharesUntilAttached_ThenPlaysFromWhereItWasPositioned()
    {
        await using var rig = ItemRuntimeRig.Create(detached: true);
        await rig.OpenAsync(start: TimeSpan.FromSeconds(2));
        await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);

        Assert.Empty(rig.Log.Of("Audio."));
        Assert.Empty(rig.Log.Of("OpenGates"));
        Assert.False(rig.Clock.IsRunning);
        Assert.Equal(TimeSpan.Zero, rig.Clock.Position);
        Assert.Contains("BeginRun(Rebuild, floor 2, end none, hold False)", rig.Log.Calls);

        await rig.Session.AttachAsync().AsTask().WaitAsync(Bound);
        await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);

        Assert.True(rig.Clock.IsRunning);
        Assert.True(rig.Clock.Position >= TimeSpan.FromSeconds(2));
        Assert.Equal(["Audio.Activate"], rig.Log.Of("Audio."));
        Assert.Equal(1, rig.Media.Runs);
        Assert.Empty(rig.Log.Violations);
    }

    // ── #725: an open that fails late ───────────────────────────────────────

    [Fact]
    public async Task AnOpenThatFailsOnceTheMediaIsOpen_FreesWhatItOpenedInReverseOrder_AndTheDisposeFreesNothingTwice()
    {
        await using var rig = ItemRuntimeRig.Create();
        var failure = new InvalidOperationException("The pacer cannot be built.");
        rig.Media.FailPacer = failure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.InitializeAsync(new FakeSource()).AsTask().WaitAsync(Bound)
        );
        Assert.Same(failure, thrown);
        Assert.Equal(["Free(Pipeline)", "Free(AudioDecoder)", "Free(VideoDecoder)", "Free(Demux)"], rig.Log.Of("Free("));
        Assert.Null(rig.Session.MediaInfo);

        await rig.Session.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(4, rig.Log.Count("Free("));
        Assert.Empty(rig.Log.Violations);
    }

    // ── Coalescing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ASeekArrivingWhileAnotherWaitsForTheRun_SupersedesIt_AndTheTwoCostOneUnwindAndOneSeek()
    {
        // The second seek arrives before the first's caller has given up: it ends the first,
        // Cancelled, and waits on the same stop.
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        var graph = rig.Media.Latest(ItemWork.Graph);

        var waiting = rig.Next(Step.WaitingForRun);
        var first = rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask();
        await waiting.WaitAsync(Bound);
        var second = rig.Session.SeekAsync(TimeSpan.FromSeconds(2)).AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Bound));
        graph.Release();
        await second.WaitAsync(Bound);

        Assert.Empty(rig.Log.Violations);
        Assert.Equal(["Seek(2)"], rig.Log.Of("Seek("));
        Assert.Equal(1, rig.Log.Count("Audio.Deactivate"));
        Assert.Equal(1, rig.Session.RunNumber);
        Assert.Equal(2, rig.Media.Runs);
    }

    // ── #714: a warm-up after a cut-short seek ──────────────────────────────

    [Fact]
    public async Task AWarmUpAfterASeekCutShort_RepositionsAtTheClock_BeforeItStartsARun()
    {
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.FirstFrameOnGraphStart = false;
        await rig.OpenAsync();
        var warm = rig.Session.WarmUpAsync().AsTask();
        await rig.Media.Started(ItemWork.Graph).WaitAsync(Bound);
        await rig.SeekCutShortAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warm.WaitAsync(Bound));

        var again = rig.Session.WarmUpAsync().AsTask();
        await rig.Media.Started(ItemWork.Graph, 2).WaitAsync(Bound);
        rig.Media.DecodeFirstFrame();
        await again.WaitAsync(Bound);

        Assert.Empty(rig.Log.Violations);
        Assert.Equal(["Seek(0)"], rig.Log.Of("Seek("));
        Assert.True(rig.Log.IndexOf("ResetForSeek") < rig.Log.LastIndexOf("BeginRun("), "The run started over the decoders the cancellation left.");
        Assert.Equal(1, rig.Session.RunNumber);
    }

    // ── #713: a fault cancels the rest of its run ───────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFaultInOneSideOfARun_CancelsTheOther_AndIsReportedOnce(bool graphFaults)
    {
        var (faulting, parked) = graphFaults ? (ItemWork.Graph, ItemWork.Pump) : (ItemWork.Pump, ItemWork.Graph);

        // The pump waits for room in a decoder queue that a faulted graph no longer drains, and a
        // graph waits for packets a faulted pump no longer reads. Nothing but the run's
        // cancellation ends either.
        await using var rig = ItemRuntimeRig.Create();
        await rig.PlayingAsync();
        var stuck = rig.Media.Latest(parked);
        var failure = new InvalidOperationException("boom");

        rig.Media.Latest(faulting).Fault(failure);

        Assert.Same(failure, await rig.WorkerFault.WaitAsync(Bound));
        await stuck.Ended.WaitAsync(Bound);
        Assert.True(stuck.Token.IsCancellationRequested);

        await rig.Session.DisposeAsync().AsTask().WaitAsync(Bound);
        Assert.Equal([failure], rig.WorkerFaults);
        Assert.Empty(rig.EndsOfStream);
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task AFaultInTheLatenessRecoveryWalk_IsARecoverableError_AndTheRunGoesOn()
    {
        await using var rig = ItemRuntimeRig.Create(latenessRecovery: true);
        await rig.PlayingAsync();
        var walk = await rig.Media.Started(ItemWork.Recovery).WaitAsync(Bound);
        var failure = new InvalidOperationException("boom");

        walk.Fault(failure);
        var error = await rig.RecoverableError.WaitAsync(Bound);

        Assert.Same(failure, error.Inner);
        Assert.Equal(ErrorCategory.System, error.Category);
        Assert.Contains("Lateness recovery stopped after a fault", error.Message);
        Assert.Empty(rig.WorkerFaults);
        Assert.False(rig.Media.Latest(ItemWork.Pump).Token.IsCancellationRequested);
        Assert.False(rig.Media.Latest(ItemWork.Graph).Token.IsCancellationRequested);
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACommandWhoseTokenIsCancelledWhenItIsCalled_DoesNothing()
    {
        await using var rig = ItemRuntimeRig.Create();
        await rig.PlayingAsync();
        var before = rig.Log.Calls;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Session.SeekAsync(TimeSpan.FromSeconds(1), new CancellationToken(canceled: true)).AsTask()
        );
        // A command after it is stepped after anything it could have queued.
        await rig.Session.PauseAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(before, rig.Log.Calls[..before.Length]);
        Assert.Equal(["Audio.Pause", "CloseGates"], rig.Log.Calls[before.Length..]);
        Assert.Equal(0, rig.Session.RunNumber);
    }

    [Fact]
    public async Task ACommandCancelledWhileItsActionIsUnderWay_EndsWithTheAction_AndTheNextIsNotRefused()
    {
        // The demuxer's seek does not look at its token. The seek's task completes when the seek
        // returns, not when its caller gives up, so the caller's next command finds nothing under
        // way.
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.Media.HoldSeeks();
        using var cancel = new CancellationTokenSource();
        var seeking = rig.Next(Step.SeekingDemux);
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1), cancel.Token).AsTask();
        await seeking.WaitAsync(Bound);

        var released = 0;
        var endedAfterTheSeekReturned = seek.ContinueWith(
            _ => Volatile.Read(ref released) == 1,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        cancel.Cancel();
        Volatile.Write(ref released, 1);
        rig.Media.ReleaseSeek();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seek.WaitAsync(Bound));
        Assert.True(await endedAfterTheSeekReturned.WaitAsync(Bound), "The seek's task completed before its action had ended.");
        await rig.Session.PauseAsync().AsTask().WaitAsync(Bound);
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task ADisposedRuntime_RefusesCommands()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        await rig.Session.DisposeAsync().AsTask().WaitAsync(Bound);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Session.PlayAsync().AsTask().WaitAsync(Bound));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Bound));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Session.PauseAsync().AsTask().WaitAsync(Bound));
        Assert.Empty(rig.Log.Of("Seek("));
    }

    [Fact]
    public async Task ACommandStillQueuedWhenTheDisposeCompletes_IsRefused()
    {
        // The dispose waits for the run; a seek sent meanwhile finds the runtime disposing.
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        var graph = rig.Media.Latest(ItemWork.Graph);

        var waiting = rig.Next(Step.WaitingForRun);
        var dispose = rig.Session.DisposeAsync().AsTask();
        await waiting.WaitAsync(Bound);
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => seek.WaitAsync(Bound));
        graph.Release();
        await dispose.WaitAsync(Bound);

        Assert.Empty(rig.Log.Of("Seek("));
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task ASecondOpen_IsRefused_AndOpensNothing()
    {
        await using var rig = await ItemRuntimeRig.Create().OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.InitializeAsync(new FakeSource()).AsTask().WaitAsync(Bound)
        );

        Assert.Equal(1, rig.Log.Calls.Count(c => c == "Open"));
    }

    [Fact]
    public async Task AStartAtOrPastTheEnd_IsRefused_NamingTheStart()
    {
        await using var rig = ItemRuntimeRig.Create();

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => rig.Session.InitializeAsync(new FakeSource(), TimeSpan.FromSeconds(10), null, default).AsTask().WaitAsync(Bound)
        );

        Assert.Equal("start", refused.ParamName);
        Assert.Equal(TimeSpan.FromSeconds(10), refused.ActualValue);
        Assert.StartsWith("'fake' cannot start at 00:00:10: it is 00:00:10 long.", refused.Message);
        Assert.Empty(rig.Log.Of("Seek("));
    }

    [Fact]
    public async Task ARewindPastTheEnd_IsRefused_NamingTheStart()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => rig.Session.RewindToAsync(TimeSpan.FromSeconds(12), null, default).AsTask().WaitAsync(Bound)
        );

        Assert.Equal("start", refused.ParamName);
        Assert.Empty(rig.Log.Of("Seek("));
    }

    [Fact]
    public async Task ARuntimePreparedAhead_RefusesAPlayAndAPause_UntilItIsAttached()
    {
        await using var rig = await ItemRuntimeRig.Create(detached: true).OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.PlayAsync().AsTask().WaitAsync(Bound));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.PauseAsync().AsTask().WaitAsync(Bound));

        Assert.Empty(rig.Log.Of("Audio."));
        Assert.False(rig.Clock.IsRunning);
    }

    [Fact]
    public async Task AResumeAfterARepositionFailedWithTheAudioSinkDeactivated_ActivatesIt()
    {
        // The seek deactivates the sink, then fails to activate it again: the resume activates it
        // rather than resuming a sink that would play nothing.
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.Audio.Fail = "Activate";
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Bound)
        );
        rig.Audio.Fail = null;
        var mark = rig.Log.Calls.Length;

        await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);

        var resumed = rig.Log.Calls[mark..];
        Assert.Contains("Audio.Activate", resumed);
        Assert.DoesNotContain("Audio.Resume", resumed);
        Assert.True(rig.Clock.IsRunning);
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task AnItemWithNothingToPlay_RaisesItsEndOfStreamFromItsRun()
    {
        await using var rig = await ItemRuntimeRig.Create(MediaShape.Nothing).OpenAsync();
        await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);
        await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);

        await rig.EndOfStream().WaitAsync(Bound);

        Assert.Equal([0], rig.EndsOfStream);
        Assert.Empty(rig.Log.Of("Pump started", "Graph started"));
    }

    [Fact]
    public async Task AFaultBeforeTheRunsStartHasEnded_EndsTheWorkItKeptFromStarting()
    {
        // The pump faults as it starts, and the fault stops the run before the graph is started.
        // The graph the stopped scope will not start has ended, so nothing waits for it.
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.FaultOnStart = ItemWork.Pump;
        rig.Session.OnStartingWork = async work =>
        {
            if (work == ItemWork.Graph)
                await rig.WorkerFault.WaitAsync(Bound);
        };
        await rig.OpenAsync();

        await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);
        await rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Bound);
        await rig.Session.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal([rig.Media.StartFault], rig.WorkerFaults);
        Assert.Equal(0, rig.Log.Count("Graph started"));
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task ARunThatCannotStart_FailsItsCommand_StartingNothing_AndTheNextPlayStartsOne()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        var failure = new InvalidOperationException("The graph cannot be built.");
        rig.Media.FailBeginRun = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Bound)));
        Assert.Equal(1, rig.Media.Runs);

        await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);
        await rig.Media.Started(ItemWork.Graph, 2).WaitAsync(Bound);
        Assert.Equal(2, rig.Media.Runs);
        Assert.Empty(rig.WorkerFaults);
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task AnOpenThatFails_LeavesNothingOpen_AndTheRuntimeCanOpenAgain()
    {
        await using var rig = ItemRuntimeRig.Create();
        var failure = new InvalidOperationException("The source cannot be opened.");
        rig.Opener.Fail = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.InitializeAsync(new FakeSource()).AsTask().WaitAsync(Bound)));
        Assert.Empty(rig.Log.Of("OpenPacer", "Free("));
        Assert.Null(rig.Session.MediaInfo);

        rig.Opener.Fail = null;
        await rig.OpenAsync();
        Assert.Equal(2, rig.Log.Calls.Count(c => c == "Open"));
    }

    [Fact]
    public async Task ACommandWhoseClockCannotBeRead_Fails_RatherThanWaitingForGood()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.Clock.FailPosition = true;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.PlayAsync().AsTask().WaitAsync(Bound));

        Assert.Contains("position cannot be read", thrown.Message);
        rig.Clock.FailPosition = false;
        await rig.Session.PauseAsync().AsTask().WaitAsync(Bound);
    }

    [Fact]
    public async Task AClockCallThatThrows_FailsTheItem_AndItsCommandGoesOn()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.Clock.FailPause = true;

        await rig.Session.PauseAsync().AsTask().WaitAsync(Bound);

        var fault = await rig.WorkerFault.WaitAsync(Bound);
        Assert.Contains("cannot be paused", fault.Message);
    }

    [Fact]
    public async Task AFaultCallbackThatThrows_IsCalledOnce()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.WorkerFaultedThrows = true;

        rig.Media.Latest(ItemWork.Graph).Fault(new InvalidOperationException("boom"));
        await rig.WorkerFault.WaitAsync(Bound);
        // The dispose is stepped after the fault, and completes once everything before it has been.
        await rig.Session.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Single(rig.WorkerFaults);
    }

    [Fact]
    public async Task AnAttach_CompletesOnlyOnceTheSharedClocksAreSeated()
    {
        // A runtime prepared ahead becomes the current item while the playlist is paused, and no
        // play follows. Whatever reads the position once the attach completes reads where the
        // runtime was positioned. The session is held closing its gates for a reposition when the
        // attach is sent, so the attach waits behind it.
        await using var rig = ItemRuntimeRig.Create(detached: true);
        await rig.OpenAsync(start: TimeSpan.FromSeconds(2));
        await rig.Session.WarmUpAsync().AsTask().WaitAsync(Bound);
        rig.Media.HoldCloseGates();
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(3)).AsTask();
        await rig.Media.CloseGatesEntered.WaitAsync(Bound);

        var attach = rig.Session.AttachAsync().AsTask();
        var attachedBeforeItWasStepped = attach.IsCompleted;
        rig.Media.ReleaseCloseGates();
        await attach.WaitAsync(Bound);
        var seated = rig.Clock.Position;
        await seek.WaitAsync(Bound);

        Assert.False(attachedBeforeItWasStepped, "The attach completed before the session had stepped it.");
        Assert.NotEqual(TimeSpan.Zero, seated);
        Assert.Equal(TimeSpan.FromSeconds(3), rig.Clock.Position);
        Assert.False(rig.Clock.IsRunning);
        Assert.Empty(rig.Log.Of("Audio."));
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADisposeDuringAnAction_CutsItShort_AndBeginsOnceItHasEnded()
    {
        await using var rig = ItemRuntimeRig.Create();
        rig.Opener.Hold = true;
        var open = rig.Session.InitializeAsync(new FakeSource()).AsTask();
        await rig.Opener.Entered.WaitAsync(Bound);

        await rig.Session.DisposeAsync().AsTask().WaitAsync(Bound);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open.WaitAsync(Bound));
        Assert.Empty(rig.Log.Of("OpenPacer", "Free("));
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task AWorkEndsAndIsReported_EvenWhenAHookAtItsEndThrows()
    {
        // The seek waits for the pump's end, which is reported however the hook ends. What the
        // hook threw leaves the pump's task, and the dispose throws it with the stop's faults.
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.ThrowAtStep = Step.PumpEnded;
        rig.DisposeThrows = true;

        await rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Bound);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => rig.Session.DisposeAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(1, rig.Session.RunNumber);
        Assert.Contains(Flatten(thrown), e => e.Message == "The test hook threw at PumpEnded.");
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task AClockThatCannotBeStoppedAtTheDispose_IsThrown_AndTheFreesGoOn()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.DisposeThrows = true;
        rig.Clock.FailStop = true;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.DisposeAsync().AsTask().WaitAsync(Bound));

        Assert.Contains("cannot be stopped", thrown.Message);
        Assert.Equal(Teardown, rig.Log.Of("Free("));
        Assert.Empty(rig.WorkerFaults);
    }

    [Fact]
    public async Task ACancellationCallbackThatThrowsAsASeekStopsTheRun_IsThrownByTheDispose()
    {
        // At a seek as at the dispose, what a stop's cancellation callbacks threw is the stop's own
        // fault: no command fails for it, and the dispose throws it.
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.ThrowOnCancel = ItemWork.Pump;
        await rig.PlayingAsync();
        rig.Media.ThrowOnCancel = ItemWork.None;
        rig.DisposeThrows = true;

        await rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Bound);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => rig.Session.DisposeAsync().AsTask().WaitAsync(Bound));

        Assert.Contains(rig.Media.CancelFault, Flatten(thrown));
        Assert.Empty(rig.WorkerFaults);
    }

    private static IEnumerable<Exception> Flatten(Exception error) =>
        error is AggregateException aggregate
            ? aggregate.InnerExceptions.SelectMany(Flatten)
            : [error, .. error.InnerException is { } inner ? Flatten(inner) : []];

    [Fact]
    public async Task ADeactivationThatFailsAtTheDispose_IsThrown_AndTheFreesGoOn()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        rig.DisposeThrows = true;
        rig.Audio.Fail = "Deactivate";

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.DisposeAsync().AsTask().WaitAsync(Bound));

        Assert.Contains("Deactivate failed", thrown.Message);
        Assert.Equal(Teardown, rig.Log.Of("Free("));
        Assert.Empty(rig.Log.Violations);
    }

    [Fact]
    public async Task ASecondDispose_WaitsForTheFirst_AndThrowsWhatItThrew()
    {
        await using var rig = ItemRuntimeRig.Create();
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        rig.Media.FailFree = ItemResources.Demux;
        var graph = rig.Media.Latest(ItemWork.Graph);

        var waiting = rig.Next(Step.WaitingForRun);
        var first = rig.Session.DisposeAsync().AsTask();
        await waiting.WaitAsync(Bound);
        var second = rig.Session.DisposeAsync().AsTask();
        var graphEndedFirst = second.ContinueWith(
            _ => graph.Ended.IsCompleted,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        graph.Release();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Bound));
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Bound)));
        Assert.True(await graphEndedFirst.WaitAsync(Bound), "The second dispose ended before the run the first was stopping.");
        Assert.Equal(Teardown, rig.Log.Of("Free("));
    }

    [Fact]
    public async Task AStopWhoseWorkHasNotEndedAfterFiveSeconds_NamesIt_Once()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var rig = ItemRuntimeRig.Create(time: time);
        rig.Media.IgnoresCancellation = ItemWork.Graph;
        await rig.PlayingAsync();
        var graph = rig.Media.Latest(ItemWork.Graph);

        var waiting = rig.Next(Step.WaitingForRun);
        var seek = rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask();
        await waiting.WaitAsync(Bound);
        await rig.LetGo("the demux pump").WaitAsync(Bound); // the pump observed the stop and has ended
        time.Advance(SubstrateSession.StopWarningInterval - TimeSpan.FromMilliseconds(1));
        Assert.Empty(rig.Warnings.Lines);
        time.Advance(TimeSpan.FromMilliseconds(1));
        time.Advance(SubstrateSession.StopWarningInterval);

        var warning = Assert.Single(rig.Warnings.Lines);
        Assert.Contains("run 0", warning);
        Assert.Contains("the graph", warning);
        Assert.DoesNotContain("the demux pump", warning);
        graph.Release();
        await seek.WaitAsync(Bound);
    }

    [Fact]
    public async Task ADisposeHeldByAnActionThatIgnoresItsToken_NamesIt_AndWaitsForIt()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var rig = ItemRuntimeRig.Create(time: time);
        rig.Opener.Hold = true;
        rig.Opener.IgnoresCancellation = true;
        var open = rig.Session.InitializeAsync(new FakeSource()).AsTask();
        await rig.Opener.Entered.WaitAsync(Bound);

        var dispose = rig.Session.DisposeAsync().AsTask();
        time.Advance(SubstrateSession.StopWarningInterval);

        var warning = Assert.Single(rig.Warnings.Lines);
        Assert.Contains("the open", warning);
        rig.Opener.Release();
        await dispose.WaitAsync(Bound);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open.WaitAsync(Bound));
    }
}
