using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Drives the <b>real</b> <see cref="PlaybackControllerCore"/> dispatch loop through the
/// load-bearing transition branches the architecture review (§2.1) flagged — load /
/// play / loop-vs-end / replay-from-Ended / error routing / unload — using a fully
/// in-memory fake session and clock, with <b>no FFmpeg</b>.
///
/// <para>
/// This is the coverage gap the review named: before the pure <see cref="PlaybackProtocol"/>
/// wiring, every one of these branches was exercised <i>only</i> end-to-end under
/// <c>[RequiresFfmpegAndCorpusFact]</c>. These tests run the same dispatch loop those
/// integration tests do, but against a scriptable fake, so (a) the controller's observable
/// behaviour on each branch is regression-locked FFmpeg-free, and (b) the per-trigger
/// <c>PlaybackProtocol.Advance</c> ↔ Stateless parity assertion
/// (<c>PlaybackControllerCore.AssertProtocolParity</c>, active in this Debug test build)
/// is exercised on the real decision paths — a drift between the lifted table and the live
/// config would throw here.
/// </para>
/// </summary>
public sealed class PlaybackDispatchProtocolTests
{
    private static (PlaybackControllerCore Controller, FakeSession Session) NewController(
        RepeatMode initialRepeat = RepeatMode.Off
    )
    {
        var session = new FakeSession();
        var factory = new FakeSessionFactory(session);
        var clock = new PlaybackClock(new FakeTimeProvider());
        var options = Microsoft.Extensions.Options.Options.Create(
            new FrameFlowPlaybackOptions { InitialRepeatMode = initialRepeat }
        );
        var controller = new PlaybackControllerCore(
            NullLogger<PlaybackControllerCore>.Instance,
            factory,
            clock,
            options
        );
        return (controller, session);
    }

    [Fact]
    public async Task SessionGeneration_StartsAtZero_AndIncrementsOnEveryLoad()
    {
        // The counter that tells a diagnostics consumer whether two polls straddle a load.
        // Nothing else in the snapshot reveals it: a load restarts the demux and decoder
        // counters at zero while the consumer's long-lived sink keeps climbing.
        var (controller, _) = NewController();
        await using var _d = controller;

        Assert.Equal(0, controller.GetDiagnostics().SessionGeneration);

        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);
        Assert.Equal(1, controller.GetDiagnostics().SessionGeneration);

        // Load is accepted only from Idle and Unloaded, so a reload goes through unload --
        // and the teardown advances the generation in its own right, because it zeroes every
        // counter in the snapshot.
        Assert.True((await controller.UnloadAsync()).IsSuccess);
        var unloaded = controller.GetDiagnostics();
        Assert.Equal(2, unloaded.SessionGeneration);
        Assert.Same(PipelineDiagnosticsSnapshot.Empty, unloaded.Pipeline);

        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);
        Assert.Equal(3, controller.GetDiagnostics().SessionGeneration);
    }

    [Fact]
    public async Task SnapshotsStraddlingAnUnload_CompareAsReset()
    {
        // The teardown case, which the create-only generation missed. Unload serves
        // PipelineDiagnosticsSnapshot.Empty, so every counter drops to zero at once. Holding
        // the generation across that would make the pair look subtractable, every counter
        // would read as having gone backwards, and Compare ignores backwards movement -- so a
        // torn-down pipeline would report as an unremarkable interval.
        var (controller, _) = NewController();
        await using var _d = controller;

        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);
        var loaded = controller.GetDiagnostics();

        Assert.True((await controller.UnloadAsync()).IsSuccess);
        var afterUnload = controller.GetDiagnostics();

        Assert.Same(PipelineDiagnosticsSnapshot.Empty, afterUnload.Pipeline);
        Assert.True(
            Diagnostics.DiagnosticsInterpreter.Compare(loaded, afterUnload).IsReset
        );
    }

    [Fact]
    public async Task SnapshotsStraddlingAFatalError_CompareAsReset()
    {
        // Same teardown, reached the other way. A load failure disposes the session and lands
        // in Error, which is the path a diagnostics consumer most needs to see honestly.
        var session = new FakeSession { WarmUpThrows = new InvalidOperationException("cold") };
        var clock = new PlaybackClock(new FakeTimeProvider());
        await using var controller = new PlaybackControllerCore(
            NullLogger<PlaybackControllerCore>.Instance,
            new FakeSessionFactory(session),
            clock,
            Microsoft.Extensions.Options.Options.Create(new FrameFlowPlaybackOptions())
        );

        var before = controller.GetDiagnostics();

        Assert.False((await controller.LoadAsync(new FakeSource())).IsSuccess);
        Assert.Equal(PlaybackState.Error, controller.State);

        var after = controller.GetDiagnostics();

        // Create bumped it, the teardown bumped it again.
        Assert.Equal(0, before.SessionGeneration);
        Assert.Equal(2, after.SessionGeneration);
        Assert.True(Diagnostics.DiagnosticsInterpreter.Compare(before, after).IsReset);
    }

    [Fact]
    public async Task FailedDisposal_StillPublishesTheTeardownBoundary()
    {
        // The boundary is published before the disposal is awaited, so a throwing
        // DisposeAsync cannot leave the controller serving a dead session's counters under
        // the pre-teardown generation. Publishing afterwards would skip the write entirely.
        var session = new FakeSession { DisposeThrows = new InvalidOperationException("stuck") };
        var clock = new PlaybackClock(new FakeTimeProvider());
        await using var controller = new PlaybackControllerCore(
            NullLogger<PlaybackControllerCore>.Instance,
            new FakeSessionFactory(session),
            clock,
            Microsoft.Extensions.Options.Options.Create(new FrameFlowPlaybackOptions())
        );

        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);
        var loaded = controller.GetDiagnostics();
        Assert.Equal(1, loaded.SessionGeneration);

        // Unload drives the throwing disposal. However the controller reports that, the
        // diagnostics boundary must have moved.
        try
        {
            await controller.UnloadAsync();
        }
        catch (InvalidOperationException)
        {
            // The disposal fault is the controller's business; this test is about the boundary.
        }

        var after = controller.GetDiagnostics();

        Assert.Equal(2, after.SessionGeneration);
        Assert.Same(PipelineDiagnosticsSnapshot.Empty, after.Pipeline);
        Assert.True(Diagnostics.DiagnosticsInterpreter.Compare(loaded, after).IsReset);
    }

    [Fact]
    public async Task SnapshotsStraddlingALoad_CompareAsReset()
    {
        // The end-to-end shape of Decision 5: the generation the controller stamps is what
        // makes DiagnosticsInterpreter refuse to subtract across a session change. Asserted
        // here rather than only in DiagnosticsInterpreterTests, which supplies the generation
        // by hand and so cannot catch the controller failing to increment it.
        var (controller, _) = NewController();
        await using var _d = controller;

        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);
        var before = controller.GetDiagnostics();

        var within = Diagnostics.DiagnosticsInterpreter.Compare(before, controller.GetDiagnostics());
        Assert.False(within.IsReset);

        Assert.True((await controller.UnloadAsync()).IsSuccess);
        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);
        var across = Diagnostics.DiagnosticsInterpreter.Compare(before, controller.GetDiagnostics());

        Assert.True(across.IsReset);
        Assert.Equal(1, across.FromGeneration);
        // 1 -> 2 on the unload's teardown, 2 -> 3 on the new session.
        Assert.Equal(3, across.ToGeneration);
    }

    [Fact]
    public async Task Load_AutoChainsThroughLoadingToPaused_AndCreatesWarmsSession()
    {
        var (controller, session) = NewController();
        await using var _ = controller;

        var states = new List<PlaybackState>();
        using var sub = controller.PlaybackStateChanged.Subscribe(
            new Relay<StateTransition<PlaybackState>>(t => states.Add(t.Current))
        );

        var result = await controller.LoadAsync(new FakeSource());

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(PlaybackState.Paused, controller.State);
        // The shell performed the lifted action list: create → initialize → warm.
        Assert.Equal(1, session.InitializeCalls);
        Assert.Equal(1, session.WarmUpCalls);
        // Public projection collapses the loading substates: Idle observers see a single
        // Loading then Paused (no redundant Loading→Loading).
        Assert.Equal(new[] { PlaybackState.Loading, PlaybackState.Paused }, states.ToArray());
    }

    [Fact]
    public async Task Play_FromPaused_PlaysSession_AndEntersPlaying()
    {
        var (controller, session) = NewController();
        await using var _ = controller;

        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);

        Assert.Equal(PlaybackState.Playing, controller.State);
        Assert.Equal(1, session.PlayCalls);
    }

    [Fact]
    public async Task LastFrameRendered_RepeatOff_ReachesEnded()
    {
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var endedTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sub = controller.PlaybackStateChanged.Subscribe(
            new Relay<StateTransition<PlaybackState>>(t =>
            {
                if (t.Current == PlaybackState.Ended)
                    endedTcs.TrySetResult();
            })
        );

        // The pipeline reports end-of-stream — the loop-vs-end branch with repeat Off
        // routes to Ended (stop ticker + freeze clock).
        session.RaiseEndOfStream();

        await endedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackState.Ended, controller.State);
    }

    [Fact]
    public async Task LastFrameRendered_RepeatOne_LoopsWithoutLeavingPlaying()
    {
        var (controller, session) = NewController(RepeatMode.One);
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var loopTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sub = controller.LoopRestarted.Subscribe(
            new Relay<LoopRestarted>(_ => loopTcs.TrySetResult())
        );

        // End-of-stream under RepeatMode.One is the internal loop transition: it must
        // raise LoopRestarted, route a rewind through the session, and NEVER leave Playing.
        session.RaiseEndOfStream();

        await loopTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackState.Playing, controller.State);

        // The rewind runs on a background seek task (StartSeekRunner), so LoopRestarted
        // can fire fractionally before SeekAsync lands — wait for the session to see it.
        await CompletesWithin(session.FirstSeek.Task, TimeSpan.FromSeconds(5));
        Assert.True(
            Volatile.Read(ref session.SeekCalls) >= 1,
            "Loop boundary did not route a rewind to the session."
        );
        // Still Playing after the rewind completed (the internal transition never exits Playing).
        Assert.Equal(PlaybackState.Playing, controller.State);
    }

    [Fact]
    public async Task WorkerFault_RoutesToError_AndDisposesSession()
    {
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var errorTcs = new TaskCompletionSource<PlaybackError>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sub = controller.ErrorOccurred.Subscribe(
            new Relay<PlaybackError>(e => errorTcs.TrySetResult(e))
        );

        // A worker fault is the error-routing branch: FatalError from Playing → Error
        // (dispose session + raise error), regardless of current state.
        session.RaiseWorkerFault(new InvalidOperationException("boom"));

        var error = await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackState.Error, controller.State);
        Assert.Contains("boom", error.Message);
        Assert.True(session.Disposed, "Error entry did not dispose the session.");
    }

    [Fact]
    public async Task RecoverableError_IsRaised_AndPlaybackCarriesOn()
    {
        // A playlist item that fails and is skipped (#180): the error reaches ErrorOccurred,
        // and the controller neither leaves Playing nor disposes the session.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var errors = new ConcurrentQueue<PlaybackError>();
        using var sub = controller.ErrorOccurred.Subscribe(
            new Relay<PlaybackError>(errors.Enqueue)
        );

        var reported = new PlaybackError(ErrorCategory.System, "item failed");
        session.RaiseRecoverableError(reported);

        // The error is dispatched in order with commands, so a no-op command posted after it
        // completes only once it has been handled.
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Same(reported, Assert.Single(errors));
        Assert.Equal(PlaybackState.Playing, controller.State);
        Assert.False(session.Disposed, "A recoverable error disposed the session.");
    }

    [Fact]
    public async Task RecoverableError_FromAnUnloadedSession_IsDropped()
    {
        // The playlist reports from the thread pool, so a report can arrive after the
        // controller has unloaded the session that made it and loaded another.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        var unloaded = session.Callbacks;
        Assert.True((await controller.UnloadAsync()).IsSuccess);
        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);

        var errors = new ConcurrentQueue<PlaybackError>();
        using var sub = controller.ErrorOccurred.Subscribe(
            new Relay<PlaybackError>(errors.Enqueue)
        );

        unloaded.OnRecoverableError(new PlaybackError(ErrorCategory.System, "stale"));
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Empty(errors);

        // The loaded session's own reports still get through.
        session.RaiseRecoverableError(new PlaybackError(ErrorCategory.System, "current"));
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Equal("current", Assert.Single(errors).Message);
    }

    [Fact]
    public async Task CurrentItemChanged_ReplacesDurationAndMediaInfo()
    {
        // A playlist session reports each new item (#183); the controller's snapshot follows.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var next = new MediaInfo(
            ContainerName: "next",
            Duration: TimeSpan.FromSeconds(42),
            VideoStreams: [],
            AudioStreams: []
        );
        session.RaiseCurrentItemChanged(next);
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Same(next, controller.MediaInfo);
        Assert.Equal(TimeSpan.FromSeconds(42), controller.Duration);
        Assert.Equal(TimeSpan.FromSeconds(42), controller.GetDiagnostics().Duration);
        Assert.Equal(PlaybackState.Playing, controller.State);
    }

    [Fact]
    public async Task CurrentItemChanged_WhileTheCommandChannelIsFull_IsStillApplied()
    {
        // The update is state, not an event: a notification dropped for want of room in the
        // bounded command channel would leave Duration describing the previous item.
        var (controller, session) = NewController();
        await using var _ = controller;
        await controller.LoadAsync(new FakeSource());

        // Hold the dispatch loop inside Play, then fill the channel behind it. A buffer-ready
        // trigger is dropped as stale when it reaches Playing, so the filler changes nothing.
        session.PlayBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var play = controller.PlayAsync();
        await session.PlayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < CommandChannelCapacity; i++)
            session.RaiseBufferReady();

        var next = new MediaInfo(
            "next",
            TimeSpan.FromSeconds(42),
            VideoStreams: [],
            AudioStreams: []
        );
        session.RaiseCurrentItemChanged(next);

        session.PlayBlocker.SetResult();
        Assert.True((await play).IsSuccess);
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Same(next, controller.MediaInfo);
        Assert.Equal(TimeSpan.FromSeconds(42), controller.Duration);
    }

    // PlaybackControllerCore's bounded command channel.
    private const int CommandChannelCapacity = 64;

    [Fact]
    public async Task CurrentItemChanged_FromAnUnloadedSession_DoesNotDisplaceTheLoadedSessionsUpdate()
    {
        // The controller keeps one pending update. A late report from a session it has
        // unloaded, arriving after the loaded session's and before the dispatch loop applies
        // it, must not replace it.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        var unloaded = session.Callbacks;
        Assert.True((await controller.UnloadAsync()).IsSuccess);
        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);

        // Hold the dispatch loop so neither report is applied until both are in.
        session.PlayBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var play = controller.PlayAsync();
        await session.PlayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var next = new MediaInfo(
            "next",
            TimeSpan.FromSeconds(42),
            VideoStreams: [],
            AudioStreams: []
        );
        session.RaiseCurrentItemChanged(next);
        unloaded.OnCurrentItemChanged(
            new MediaInfo("stale", TimeSpan.FromSeconds(99), VideoStreams: [], AudioStreams: [])
        );

        session.PlayBlocker.SetResult();
        Assert.True((await play).IsSuccess);
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Same(next, controller.MediaInfo);
    }

    [Fact]
    public async Task CurrentItemChanged_FromAnUnloadedSession_IsDropped()
    {
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        var unloaded = session.Callbacks;
        Assert.True((await controller.UnloadAsync()).IsSuccess);
        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);
        var loadedDuration = controller.Duration;

        unloaded.OnCurrentItemChanged(
            new MediaInfo("stale", TimeSpan.FromSeconds(99), VideoStreams: [], AudioStreams: [])
        );
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Equal(loadedDuration, controller.Duration);
    }

    [Fact]
    public async Task EndOfStream_WhilePaused_EndsPlayback()
    {
        // A playlist skip on its last item while paused reports end-of-stream to a paused
        // controller (#182), and so does an end-of-stream posted just before a pause.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();
        Assert.True((await controller.PauseAsync()).IsSuccess);

        session.RaiseEndOfStream();
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Equal(PlaybackState.Ended, controller.State);
    }

    [Fact]
    public async Task WorkerFault_FromTheSessionReplayReplaced_DoesNotFaultTheNewSession()
    {
        // Replay from Ended unloads and reloads inside one dispatch command. A fatal error the
        // old session posts while it is torn down waits in the channel until that command has
        // loaded the new session, so it must be recognised as the old session's.
        var (controller, session) = NewController();
        await using var _ = controller;

        var ended = await PlayToEndedAsync(controller, session);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        Assert.Equal(PlaybackState.Playing, controller.State);

        ended.OnWorkerFaulted(new InvalidOperationException("stale"));
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Equal(PlaybackState.Playing, controller.State);
        Assert.False(session.Disposed, "A stale fault disposed the new session.");
    }

    [Fact]
    public async Task EndOfStream_FromTheSessionReplayReplaced_DoesNotEndTheNewSession()
    {
        var (controller, session) = NewController();
        await using var _ = controller;

        var ended = await PlayToEndedAsync(controller, session);
        Assert.True((await controller.PlayAsync()).IsSuccess);

        ended.OnEndOfStream();
        Assert.True((await controller.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);

        Assert.Equal(PlaybackState.Playing, controller.State);
    }

    // Loads, plays and ends the fake session, and returns the callbacks it was created with.
    private static async Task<SessionCallbacks> PlayToEndedAsync(
        PlaybackControllerCore controller,
        FakeSession session
    )
    {
        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();
        var callbacks = session.Callbacks;

        var endedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (
            controller.PlaybackStateChanged.Subscribe(
                new Relay<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Ended)
                        endedTcs.TrySetResult();
                })
            )
        )
        {
            session.RaiseEndOfStream();
            await CompletesWithin(endedTcs.Task, TimeSpan.FromSeconds(5));
        }

        Assert.Equal(PlaybackState.Ended, controller.State);
        return callbacks;
    }

    [Fact]
    public async Task Play_FromEnded_RunsReplayRecovery_BackToPlaying()
    {
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var endedTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using (
            controller.PlaybackStateChanged.Subscribe(
                new Relay<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Ended)
                        endedTcs.TrySetResult();
                })
            )
        )
        {
            session.RaiseEndOfStream();
            await endedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(PlaybackState.Ended, controller.State);

        // Play from Ended is the replay-from-Ended path: the shell unloads + reloads +
        // plays. With a live source this recovers to Playing.
        var replay = await controller.PlayAsync();

        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(PlaybackState.Playing, controller.State);
    }

    [Fact]
    public async Task Unload_FromPlaying_DisposesSession_AndReachesUnloaded()
    {
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var unload = await controller.UnloadAsync();

        Assert.True(unload.IsSuccess);
        Assert.Equal(PlaybackState.Unloaded, controller.State);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task PlayBeforeLoad_IsRejected_InvalidOperation()
    {
        // The stale/invalid-trigger drop branch routed through CanFirePlayback: Play from
        // Idle is not handled by the pure core (and not permitted by Stateless) → fail.
        var (controller, _) = NewController();
        await using var _disp = controller;

        var result = await controller.PlayAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, result.Error?.Category);
        Assert.Equal(PlaybackState.Idle, controller.State);
    }

    [Fact]
    public async Task LoadFailure_RoutesToError()
    {
        // InitializeSession faults → the shell routes FatalError as a load failure and
        // LoadAsync surfaces it; the machine lands in Error.
        var session = new FakeSession { InitializeThrows = new InvalidOperationException("nope") };
        var clock = new PlaybackClock(new FakeTimeProvider());
        await using var controller = new PlaybackControllerCore(
            NullLogger<PlaybackControllerCore>.Instance,
            new FakeSessionFactory(session),
            clock,
            Microsoft.Extensions.Options.Options.Create(new FrameFlowPlaybackOptions())
        );

        var result = await controller.LoadAsync(new FakeSource());

        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackState.Error, controller.State);
    }

    [Fact]
    public async Task WarmUpFailure_RoutesToError_AsLoadFailure()
    {
        // The second loading-fault path (distinct from InitializeSession): WarmUp faults
        // during InitialBuffering → the interpreter abandons the BufferReady auto-chain and
        // routes FatalError from InitialBuffering, so LoadAsync surfaces the failure and the
        // machine lands in Error.
        var session = new FakeSession { WarmUpThrows = new InvalidOperationException("cold") };
        var clock = new PlaybackClock(new FakeTimeProvider());
        await using var controller = new PlaybackControllerCore(
            NullLogger<PlaybackControllerCore>.Instance,
            new FakeSessionFactory(session),
            clock,
            Microsoft.Extensions.Options.Options.Create(new FrameFlowPlaybackOptions())
        );

        var result = await controller.LoadAsync(new FakeSource());

        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackState.Error, controller.State);
        // InitializeAsync succeeded; the fault came from WarmUpAsync.
        Assert.Equal(1, session.InitializeCalls);
        Assert.Equal(1, session.WarmUpCalls);
        // The Error entry disposed the session.
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Pause_FromPlaying_StopsTickerThenPausesSession_AndReachesPaused()
    {
        // The Pause cell is [StopTicker (Playing OnExit), PauseSession (Paused OnEntry)] —
        // the one transition with both an exit and an entry effect. Exercises that the
        // interpreter runs the session pause and lands the public projection at Paused.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();
        Assert.Equal(PlaybackState.Playing, controller.State);

        var pause = await controller.PauseAsync();

        Assert.True(pause.IsSuccess, pause.Error?.Message);
        Assert.Equal(PlaybackState.Paused, controller.State);
        Assert.Equal(1, session.PauseCalls);
    }

    [Fact]
    public async Task Pause_Projection_FiresAtExitEntryBoundary_BeforeEntryEffect()
    {
        // Locks the OnTransitioned projection point: Stateless raised OnTransitioned after
        // OnExit and BEFORE OnEntry (ExitAsync → State=dest → OnTransitioned → EnterStateAsync).
        // So a subscriber observing the Playing→Paused public event must see the session NOT
        // yet paused (PauseSession is the destination's OnEntry effect, which runs after the
        // projection). This is the exit→entry ordering the action interpreter preserves.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        int pauseCallsAtProjection = -1;
        using var sub = controller.PlaybackStateChanged.Subscribe(
            new Relay<StateTransition<PlaybackState>>(t =>
            {
                if (t.Current == PlaybackState.Paused)
                    pauseCallsAtProjection = Volatile.Read(ref session.PauseCalls);
            })
        );

        await controller.PauseAsync();

        Assert.Equal(PlaybackState.Paused, controller.State);
        // The projection fired before the PauseSession entry effect.
        Assert.Equal(0, pauseCallsAtProjection);
        // ...and the entry effect did run, after the projection.
        Assert.Equal(1, Volatile.Read(ref session.PauseCalls));
    }

    [Fact]
    public async Task BufferUnderrunThenReady_ReturnsToPlaying_WithoutReplayingSession()
    {
        // Rebuffering × BufferReady → Playing is [StartTicker] only — BufferReady is NOT the
        // Play trigger, so the session is not re-played (PlaySession does not run). Locks the
        // "BufferReady restarts the ticker but not playback" cell through the real dispatch.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();
        Assert.Equal(1, session.PlayCalls);

        var rebufferingTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var playingAgainTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sub = controller.PlaybackStateChanged.Subscribe(
            new Relay<StateTransition<PlaybackState>>(t =>
            {
                if (t.Current == PlaybackState.Rebuffering)
                    rebufferingTcs.TrySetResult();
                else if (t.Current == PlaybackState.Playing)
                    playingAgainTcs.TrySetResult();
            })
        );

        // Underrun: Playing → Rebuffering (stop ticker).
        session.RaiseBufferUnderrun();
        await rebufferingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackState.Rebuffering, controller.State);

        // Refill: Rebuffering → Playing (start ticker only).
        session.RaiseBufferReady();
        await playingAgainTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackState.Playing, controller.State);

        // The session was played exactly once (the original Play) — the rebuffer recovery
        // did NOT issue a second PlayAsync.
        Assert.Equal(1, Volatile.Read(ref session.PlayCalls));
    }

    [Fact]
    public async Task Seek_FromEnded_RoutesThroughPlaybackReWarm_AndLaunchesSeek()
    {
        // Seek from Ended is the parameterized-trigger + auto-chain path: the playback
        // machine routes Ended → InitialBuffering → (WarmUp) → Paused (re-warm), and the
        // shell then launches the real seek to the requested position via the seek runner.
        var (controller, session) = NewController();
        await using var _ = controller;

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var endedTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using (
            controller.PlaybackStateChanged.Subscribe(
                new Relay<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Ended)
                        endedTcs.TrySetResult();
                })
            )
        )
        {
            session.RaiseEndOfStream();
            await endedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(PlaybackState.Ended, controller.State);
        var warmUpsBeforeSeek = Volatile.Read(ref session.WarmUpCalls);

        var seek = await controller.SeekAsync(TimeSpan.FromSeconds(3));

        Assert.True(seek.IsSuccess, seek.Error?.Message);
        // The Ended-seek re-warmed (a second WarmUp via the InitialBuffering re-entry) and
        // settled back at Paused.
        Assert.Equal(PlaybackState.Paused, controller.State);
        Assert.True(
            Volatile.Read(ref session.WarmUpCalls) > warmUpsBeforeSeek,
            "Ended-seek did not re-warm through the InitialBuffering re-entry."
        );

        // The real seek to the requested position ran on the background seek runner.
        await CompletesWithin(session.FirstSeek.Task, TimeSpan.FromSeconds(5));
        Assert.True(
            Volatile.Read(ref session.SeekCalls) >= 1,
            "Ended-seek did not launch the session seek runner."
        );
    }

    [Fact]
    public async Task RepeatOneLoop_DoesNotEmitPlayingProjection_AcrossLoopBoundary()
    {
        // The loop boundary is an INTERNAL transition (RunLoopRewind, never leaves Playing).
        // Stateless never fired OnTransitioned for an internal transition, so the interpreter
        // must NOT emit a Playing→Playing public projection on a loop boundary — only the
        // initial Loading→…→Playing transitions, plus LoopRestarted.
        var (controller, session) = NewController(RepeatMode.One);
        await using var _ = controller;

        var publicStates = new ConcurrentQueue<PlaybackState>();
        using var stateSub = controller.PlaybackStateChanged.Subscribe(
            new Relay<StateTransition<PlaybackState>>(t => publicStates.Enqueue(t.Current))
        );

        await controller.LoadAsync(new FakeSource());
        await controller.PlayAsync();

        var loopTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var loopSub = controller.LoopRestarted.Subscribe(
            new Relay<LoopRestarted>(_ => loopTcs.TrySetResult())
        );

        var playingProjectionsBeforeLoop = publicStates.Count(s => s == PlaybackState.Playing);
        Assert.Equal(1, playingProjectionsBeforeLoop);

        session.RaiseEndOfStream();
        await loopTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Let any erroneous extra projection surface, then assert none did. Commands and
        // internal triggers share one serial dispatch channel, and projections are raised
        // synchronously while a command is dispatched. So a command posted now completes only
        // after the end-of-stream trigger ahead of it has finished, projections included.
        // Re-selecting the current repeat mode is a dispatched no-op, which makes it a barrier
        // with no side effects; it replaces a 50 ms sleep that only made that likely.
        var barrier = await controller.SetRepeatModeAsync(RepeatMode.One);
        Assert.True(barrier.IsSuccess);
        Assert.Equal(PlaybackState.Playing, controller.State);
        Assert.Equal(1, publicStates.Count(s => s == PlaybackState.Playing));
    }

    // Waits for a signal without throwing when it does not come, so the assertion that follows
    // reports what went wrong instead of a bare TimeoutException. The bound is a safety net: the
    // signal arrives in microseconds when the behaviour is right (ADR-0072).
    private static async Task CompletesWithin(Task signal, TimeSpan bound)
    {
        try
        {
            await signal.WaitAsync(bound);
        }
        catch (TimeoutException)
        {
            // The assertion after the call names the failure.
        }
    }

    // ── Fakes ───────────────────────────────────────────────────────────

    private sealed class FakeSource : IMediaSource
    {
        public string DisplayName => "fake";
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    private sealed class FakeSessionFactory(FakeSession session) : IPlaybackSessionFactory
    {
        private readonly FakeSession _session = session;

        public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks)
        {
            _session.Bind(callbacks);
            return _session;
        }
    }

    /// <summary>
    /// A scriptable in-memory <see cref="IPlaybackSession"/>: counts lifecycle calls,
    /// can raise the session→controller callbacks on demand (end-of-stream, worker
    /// fault), and can be told to throw from <c>InitializeAsync</c>. No FFmpeg, no real
    /// pipeline. The same instance is handed back on every <c>CreateSession</c> so a
    /// replay (which disposes + recreates) can be observed through one object.
    /// </summary>
    private sealed class FakeSession : IPlaybackSession
    {
        private SessionCallbacks _callbacks;

        public int InitializeCalls;
        public int WarmUpCalls;
        public int PlayCalls;
        public int PauseCalls;
        public int SeekCalls;
        public bool Disposed;
        public Exception? InitializeThrows;
        public Exception? WarmUpThrows;

        private static readonly MediaInfo Info = new(
            ContainerName: "fake",
            Duration: TimeSpan.FromSeconds(10),
            VideoStreams: [],
            AudioStreams: []
        );

        public void Bind(SessionCallbacks callbacks)
        {
            _callbacks = callbacks;
            // A fresh CreateSession (initial load or replay) re-arms the session.
            Disposed = false;
        }

        public void RaiseEndOfStream() => _callbacks.OnEndOfStream();

        public void RaiseWorkerFault(Exception ex) => _callbacks.OnWorkerFaulted(ex);

        public void RaiseRecoverableError(PlaybackError error) =>
            _callbacks.OnRecoverableError(error);

        public void RaiseCurrentItemChanged(MediaInfo info) =>
            _callbacks.OnCurrentItemChanged(info);

        /// <summary>The callbacks from the most recent <c>CreateSession</c>.</summary>
        public SessionCallbacks Callbacks => _callbacks;

        public void RaiseBufferUnderrun() => _callbacks.OnBufferUnderrun();

        public void RaiseBufferReady() => _callbacks.OnBufferReady();

        public MediaInfo? MediaInfo => Info;
        public TimeSpan Duration => Info.Duration;

        public ValueTask InitializeAsync(
            IMediaSource source,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref InitializeCalls);
            if (InitializeThrows is { } ex)
                throw ex;
            return ValueTask.CompletedTask;
        }

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref WarmUpCalls);
            if (WarmUpThrows is { } ex)
                throw ex;
            return ValueTask.CompletedTask;
        }

        public ValueTask PlayAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PlayCalls);
            if (PlayBlocker is { } blocker)
            {
                PlayEntered.TrySetResult();
                return new ValueTask(blocker.Task);
            }
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// When set, <see cref="PlayAsync"/> completes <see cref="PlayEntered"/> and then waits
        /// for this, holding the controller's dispatch loop inside the play.
        /// </summary>
        public TaskCompletionSource? PlayBlocker;

        public TaskCompletionSource PlayEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PauseAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PauseCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask SeekAsync(
            TimeSpan position,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref SeekCalls);
            FirstSeek.TrySetResult();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Completes on the first <see cref="SeekAsync"/>. The controller runs seeks on a
        /// background runner, so a test waits on this rather than polling
        /// <see cref="SeekCalls"/> against a deadline.
        /// </summary>
        public TaskCompletionSource FirstSeek { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, <see cref="DisposeAsync"/> throws it.</summary>
        public Exception? DisposeThrows;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (DisposeThrows is { } ex)
                throw ex;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Relay<T>(Action<T> onNext) : IObserver<T>
    {
        private readonly Action<T> _onNext = onNext;

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) => _onNext(value);
    }
}
