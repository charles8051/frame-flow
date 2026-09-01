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
        var clock = new PlaybackClock(new ManualTimeSource());
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
        var clock = new PlaybackClock(new ManualTimeSource());
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
        // can fire fractionally before SeekAsync lands — poll briefly for it.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref session.SeekCalls) < 1 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
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
        var clock = new PlaybackClock(new ManualTimeSource());
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
        var clock = new PlaybackClock(new ManualTimeSource());
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
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref session.SeekCalls) < 1 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
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

        // Give any erroneous extra projection a chance to surface, then assert none did.
        await Task.Delay(50);
        Assert.Equal(PlaybackState.Playing, controller.State);
        Assert.Equal(1, publicStates.Count(s => s == PlaybackState.Playing));
    }

    // ── Fakes ───────────────────────────────────────────────────────────

    private sealed class ManualTimeSource : ITimeSource
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

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
            return ValueTask.CompletedTask;
        }

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
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
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
