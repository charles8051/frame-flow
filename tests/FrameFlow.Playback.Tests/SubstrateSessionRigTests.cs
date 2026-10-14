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
        Assert.Equal(Teardown.Order(), rig.Log.Of("Free(").Order());
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

        Assert.Empty(rig.Log.Violations);
        Assert.Equal(["Seek(2)"], rig.Log.Of("Seek("));
        Assert.True(rig.Log.IndexOf("Graph ended") < rig.Log.IndexOf("Seek("), "The demuxer was sought before the run's graph ended.");
        Assert.Equal(2, rig.Media.Runs);
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
    public async Task AnEndOfStreamJustBeforeASeek_CarriesTheRunThatEnded()
    {
        await using var rig = ItemRuntimeRig.Create();
        await rig.PlayingAsync();
        rig.Media.Latest(ItemWork.Pump).Complete();
        rig.Media.Latest(ItemWork.Graph).Complete();
        await rig.EndOfStream().WaitAsync(Bound);

        await rig.Session.SeekAsync(TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Bound);
        (await rig.Media.Started(ItemWork.Pump, 2)).Complete();
        (await rig.Media.Started(ItemWork.Graph, 2)).Complete();
        await rig.EndOfStream(2).WaitAsync(Bound);

        Assert.Equal([0, 1], rig.EndsOfStream);
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
        var pump = await rig.Media.Started(ItemWork.Pump);

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

        rig.Session.Attach();
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
}
