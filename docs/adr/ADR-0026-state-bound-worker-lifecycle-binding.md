# ADR-0026: State-Bound Worker Lifecycle Binding

**Status:** Accepted. Partially superseded by ADR-0036 on the decode
side: the lifecycle binding documented here remains how *playback*
sink pumps couple to the state machine, but decode workers under
`IDecodedMediaStream` are bound to the stream's lifetime rather than to
a playback state.
**Date:** 2026-04-08
**Supersedes:** None (extends ADR-0022, ADR-0023)
**Related:** ADR-0009 (threading and concurrency), ADR-0013 (cancellation token propagation), ADR-0020 (lifecycle decoupled from processing), ADR-0022 (long-lived workers with pause gate), ADR-0023 (hierarchical state machine with channel dispatch), ADR-0024 (playback controller as public API surface), ADR-0036 (decode/playback decoupling — lifts decode workers out of this ADR's scope)

## Context

### Manual worker lifecycle management in PlaybackController

ADR-0023 introduced a Stateless-backed hierarchical state machine with channel-serialized dispatch. ADR-0022 established long-lived workers with a pause gate for the pipeline. The `PlaybackController` implementation (ADR-0024) connects these two layers, but the connection is entirely manual.

Today, every state that needs to start, stop, pause, or resume a worker does so through hand-written `OnEntry`/`OnExit` actions:

```csharp
// Playing.OnEntry — first play vs resume branching
_playback.Configure(PlaybackState.Playing)
    .OnEntryAsync(async () =>
    {
        if (_session is not null)
        {
            if (!_renderersStarted)
            {
                await _session.StartRenderersAsync();
                _renderersStarted = true;
            }
            else
            {
                await _session.ResumeAsync();
            }
        }
        StartPositionTicker();
    });

// Paused.OnEntry — must remember to stop ticker and pause session
_playback.Configure(PlaybackState.Paused)
    .OnEntryFromAsync(PlaybackTrigger.Pause, async () =>
    {
        StopPositionTicker();
        if (_session is not null)
            await _session.PauseAsync();
    })
    .OnEntryFrom(PlaybackTrigger.BufferReady, () =>
    {
        StopPositionTicker();
    });
```

This pattern has five structural problems that have already manifested or will manifest as the controller grows.

### Problem 1: Scattered lifecycle calls

The position ticker is started in `Playing.OnEntry` and stopped in six different places: `Paused.OnEntry`, `Rebuffering.OnEntry`, `Ended.OnEntry`, `Stopped.OnEntry`, `Error.OnEntry`, and `DisposeAsync`. Every new state that can be reached from Playing must remember to include `StopPositionTicker()`. Forgetting produces a ticker that outlives its intended state — emitting stale position updates after playback has stopped.

### Problem 2: The `_renderersStarted` flag

The controller maintains a `bool _renderersStarted` field solely to distinguish first-play (call `StartRenderersAsync`) from resume (call `ResumeAsync`). This is a manual encoding of "has this state been entered before in this session?" — lifecycle bookkeeping that should be structural, not a field.

### Problem 3: Null-checking `_session` in every action

Every `OnEntry`/`OnExit` action guards with `if (_session is not null)`. This is defensive code for a condition that should be structurally impossible: if the state machine is in `Playing`, a session must exist. The null checks exist because there is no formal binding between session lifetime and state lifetime.

### Problem 4: No cleanup guarantee on error paths

When a fatal error occurs, the controller transitions to `Error`. The `Error.OnEntry` action stops the ticker and emits the error, but does not dispose the session. The faulted session — with potentially corrupted workers, held GPU textures, or open file handles — remains alive until the user explicitly calls `Reset()` or `Stop()`. If the user never does, resources leak until the controller is disposed.

This was identified during code review as a gap: the session should be torn down on error entry, or at minimum its pipeline CTS should be cancelled.

### Problem 5: Dispatch loop blocked during seek

The seek operation in the dispatch loop is synchronous through all seeking states:

```csharp
case SeekCommand sc:
    await _seeking.FireAsync(SeekTrigger.SeekRequested);
    await _seeking.FireAsync(SeekTrigger.FlushStarted);
    await _session.SeekAsync(sc.Position);  // blocks dispatch loop
    await _seeking.FireAsync(SeekTrigger.SeekCompleted);
    break;
```

While `_session.SeekAsync` runs (flush workers, drain queues, reposition demuxer, restart workers — potentially 100–500ms), the dispatch loop cannot process any other command. A `StopAsync` or `PauseAsync` from the UI queues up behind the seek. This makes the controller unresponsive during seeks.

### Problem 6: Rebuffering explicitly pauses instead of stalling naturally

The `Rebuffering.OnEntry` action calls `_session.PauseAsync()`, which closes the pause gate. ADR-0022 designed the pause gate for user-initiated pauses. Buffer underruns should stall naturally: workers block on empty `Channel<T>` queues, the audio clock freezes because the sound card has no data, and the video renderer blocks because the clock isn't advancing. No explicit pause needed.

By reusing the pause gate for rebuffering, the controller couples two distinct concerns (user pause and buffer stall) into one mechanism. This means the demuxer — which should keep running to refill buffers — is also blocked by the gate. The pipeline cannot recover from a rebuffer until the state machine transitions back to Playing and calls `ResumeAsync`, which re-opens the gate.

### The emerging pattern

All six problems share a root cause: **worker lifecycle is coupled to state machine configuration through procedural code rather than declarative binding**. Adding a new state, a new worker, or a new exit path requires modifying multiple `OnEntry`/`OnExit` actions in coordination. This does not scale as the controller adds more orthogonal regions (playback rate, DRM, casting) and more workers (buffer monitor, license renewal, cast keep-alive).

### The proposed abstraction

A `BindWorker` extension on Stateless's `StateConfiguration` that declaratively ties a worker's lifetime to a state:

- Entering the state constructs and starts the worker.
- Exiting the state stops and disposes the worker.
- The pairing is guaranteed — there is no code path that enters without starting or exits without stopping.

The initial prototype is documented in a standalone design note. This ADR evaluates the prototype, identifies deficiencies, and specifies the hardened version for FrameFlow.

## Decision

### 1. Introduce `IStateBoundWorker` as a lifecycle contract

```csharp
/// <summary>
/// A background worker whose lifetime is bound to a state machine state.
/// Created on state entry, started immediately, stopped on state exit,
/// then disposed.
/// </summary>
public interface IStateBoundWorker : IAsyncDisposable
{
    /// <summary>
    /// Begin execution. The implementation should start its internal loop
    /// and return promptly. Long-running work should be spawned internally.
    /// The token is cancelled when the owning state is exited.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cooperatively stop execution. Called after the cancellation token
    /// has been signalled. Implementations should drain any pending work
    /// and return within a reasonable time. If this method does not return
    /// within the shutdown timeout, the binding will abandon the task and
    /// proceed with disposal.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}
```

Key differences from the prototype:
- `IAsyncDisposable` is required. Workers may hold native resources (GPU surfaces, audio device handles, file handles per ADR-0005) that need explicit disposal beyond just stopping.
- `StopAsync` receives a `CancellationToken` that will be cancelled after the shutdown timeout, not `CancellationToken.None`.

### 2. Replace closure-captured state with `WorkerBinding<TWorker>`

The prototype captures mutable state (`cts`, `runningTask`, `worker`) in a closure. This is fragile under re-entrant transitions and makes testing difficult. The binding is instead an explicit object:

```csharp
internal sealed class WorkerBinding<TWorker> where TWorker : IStateBoundWorker
{
    private readonly Func<TWorker> _factory;
    private readonly Func<TWorker, Exception, Task>? _onError;
    private readonly TimeSpan _shutdownTimeout;

    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private TWorker? _worker;
    private int _state; // 0=idle, 1=running, 2=stopping

    public WorkerBinding(
        Func<TWorker> factory,
        Func<TWorker, Exception, Task>? onError = null,
        TimeSpan? shutdownTimeout = null)
    {
        _factory = factory;
        _onError = onError;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return; // guard against double-start from re-entrant transition

        _cts = new CancellationTokenSource();
        _worker = _factory();

        _runningTask = RunWorkerAsync(_worker, _cts.Token);
    }

    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
            return; // not running — nothing to stop

        var cts = _cts;
        var task = _runningTask;
        var worker = _worker;

        if (cts is null || worker is null)
            return;

        // Signal cancellation
        cts.Cancel();

        // Cooperative stop with timeout
        using var shutdownCts = new CancellationTokenSource(_shutdownTimeout);
        try
        {
            await worker.StopAsync(shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timed out — worker is uncooperative.
            // Fall through to disposal — do not await the task indefinitely.
        }

        // Await the running task (should be completed or near-complete)
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(_shutdownTimeout);
            }
            catch (TimeoutException)
            {
                // Abandon — task is leaked but we cannot block shutdown.
            }
            catch (OperationCanceledException) { }
        }

        // Dispose worker and CTS
        await worker.DisposeAsync();
        cts.Dispose();

        _cts = null;
        _runningTask = null;
        _worker = default;

        Interlocked.Exchange(ref _state, 0); // ready for re-entry
    }

    private async Task RunWorkerAsync(TWorker worker, CancellationToken ct)
    {
        try
        {
            await worker.StartAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_onError is not null)
                await _onError(worker, ex);
        }
    }
}
```

Design properties:

| Property | How |
|----------|-----|
| **No double-start** | `Interlocked.CompareExchange` guards `StartAsync` |
| **No double-stop** | `Interlocked.CompareExchange` guards `StopAsync` |
| **Re-entrant safe** | `StopAsync` resets state to `0`; subsequent `StartAsync` creates fresh worker |
| **Shutdown timeout** | `StopAsync` and task await both respect a configurable timeout |
| **Error identity** | `_onError` callback receives the worker instance, not just the exception |
| **Disposal** | Worker is `DisposeAsync`'d after stopping, before CTS disposal |
| **No `Task.Run`** | Worker's `StartAsync` runs directly on the dispatch loop; the worker spawns its own long-running loop internally if needed |

### 3. Extension method wires `WorkerBinding<T>` to Stateless

```csharp
public static class StatelessWorkerExtensions
{
    public static StateMachine<TState, TTrigger>.StateConfiguration
        BindWorker<TState, TTrigger, TWorker>(
            this StateMachine<TState, TTrigger>.StateConfiguration config,
            Func<TWorker> factory,
            Func<TWorker, Exception, Task>? onError = null,
            TimeSpan? shutdownTimeout = null)
        where TWorker : IStateBoundWorker
    {
        var binding = new WorkerBinding<TWorker>(factory, onError, shutdownTimeout);

        return config
            .OnEntryAsync(() => binding.StartAsync())
            .OnExitAsync(() => binding.StopAsync());
    }
}
```

The extension is a one-liner: create a `WorkerBinding`, attach `StartAsync` to `OnEntry`, attach `StopAsync` to `OnExit`. All lifecycle invariants are inside `WorkerBinding`, not in closures.

### 4. Two tiers of lifecycle binding

Not all workers need full stop/start on every state transition. The pipeline's audio and video renderers should pause/resume between Playing ↔ Paused, not tear down and rebuild. Two tiers are defined:

| Tier | Use case | State exit behavior | State re-entry behavior | Example |
|------|----------|--------------------|-----------------------|---------|
| **Full lifecycle** | Worker lifetime equals state lifetime | Stop + dispose | Create new instance + start | Position ticker, DRM renewal, buffer monitor, cast keep-alive |
| **Pause gate** | Worker survives across Paused/Rebuffering | Pause (gate closes) | Resume (gate opens) | Audio renderer, video renderer, demuxer |

Full lifecycle workers use `BindWorker`. Pause gate workers continue to use the `AsyncManualResetEvent` from ADR-0022, managed by the `PlaybackSession`. The two mechanisms are orthogonal and do not interfere.

### 5. Concrete application to PlaybackController

#### Position ticker (full lifecycle)

The position ticker is a self-contained worker that reads `IPlaybackClock.Position` every ~250ms and pushes to an `IObserver<TimeSpan>`. It has no dependencies on the session or pipeline. Binding it to `Playing` eliminates the manual `StartPositionTicker` / `StopPositionTicker` calls from six `OnEntry` actions.

```csharp
_playback.Configure(PlaybackState.Playing)
    .BindWorker(
        () => new PositionTickerWorker(_clock, _positionTickSubject),
        onError: (worker, ex) =>
        {
            // Position ticker failure is non-fatal — log and continue.
            _logger.LogWarning(ex, "Position ticker faulted");
            return Task.CompletedTask;
        });
```

Fields removed: `_tickerCts`, `_tickerTask`.
Methods removed: `StartPositionTicker()`, `StopPositionTicker()`.
`OnEntry` actions removed from: `Paused`, `Rebuffering`, `Ended`, `Stopped`, `Error`.

#### Session cleanup on error (full lifecycle)

Today, entering `Error` does not dispose the session. A `WorkerBinding` approach is not directly applicable here (the session is not a single worker), but the same principle applies: the session's lifetime should be structurally tied to the non-error states. The recommended fix, informed by the lifecycle-binding principle, is:

```csharp
_playback.Configure(PlaybackState.Error)
    .OnEntryFromAsync(_errorTrigger, async err =>
    {
        StopPositionTicker();
        _errorSubject.OnNext(err);
        await DisposeSessionAsync();  // ← was missing
    });
```

This is not a `BindWorker` call but follows the same "state exit = resource release" philosophy.

#### Future workers (full lifecycle)

| Worker | Bound to state | Purpose |
|--------|---------------|---------|
| `BufferHealthMonitor` | `Playing`, `Rebuffering` | Monitors queue depths, fires `BufferUnderrun` / `BufferReady` |
| `DrmLicenseRenewal` | `Playing` (when `_drm.State == KeysUsable`) | Background renewal before TTL expiry |
| `CastKeepAlive` | Any state while `_cast.State == Connected` | Heartbeat to cast receiver |
| `LiveEdgeTracker` | `Playing` (when `_live.State == LiveAtEdge`) | Monitors live edge drift |

Each of these will be an `IStateBoundWorker` implementation with `BindWorker` on the appropriate state.

### 6. Seek responsiveness improvement

The seek-blocking-dispatch-loop problem (Problem 5) is not directly solved by `BindWorker` but is informed by the same lifecycle-binding thinking. The fix decouples seek execution from the dispatch loop:

```csharp
case SeekCommand sc:
    if (_session is null)
    {
        cmd.Completion.TrySetResult(Result.Fail(
            ErrorCategory.InvalidOperation, "No active session"));
        continue;
    }
    await _seeking.FireAsync(SeekTrigger.SeekRequested);
    await _seeking.FireAsync(SeekTrigger.FlushStarted);
    // Start seek but don't await — session posts SeekCompleted when done
    _ = _session.SeekAsync(sc.Position).ContinueWith(_ =>
        PostInternalAsync(PlaybackTrigger.SeekCompleted),
        TaskContinuationOptions.OnlyOnRanToCompletion);
    break;
```

The seek operation runs on the session's own thread/task. When it completes, it posts `SeekCompleted` back through the command channel via `PostInternalAsync`. The dispatch loop remains responsive during the seek — `StopAsync` or `PauseAsync` can interrupt immediately.

This requires the session's `SeekAsync` to be cancellation-aware: if a `Stop` arrives while seeking, the session CTS cancellation should abort the seek.

### 7. Rebuffering model correction

Rebuffering should not close the pause gate. The entry action is removed:

```csharp
// Before (problematic):
_playback.Configure(PlaybackState.Rebuffering)
    .OnEntryAsync(async () =>
    {
        if (_session is not null)
            await _session.PauseAsync();  // closes pause gate — blocks demuxer too
    });

// After (corrected):
_playback.Configure(PlaybackState.Rebuffering)
    .OnEntry(() =>
    {
        // No action needed. Workers stall naturally on empty Channel<T> queues.
        // Audio clock freezes (sound card has no data).
        // Video renderer blocks (clock isn't advancing).
        // Demuxer keeps running — fetching data to refill the buffers.
        // Session fires OnBufferReady when queue depth recovers.
    });
```

The pause gate (ADR-0022) remains exclusively for user-initiated pause. Buffer stalls are emergent from the pipeline's channel backpressure, which is the design intent from ADR-0022 and ADR-0023.

## Consequences

### Positive

- **Deterministic worker lifecycle.** `BindWorker` guarantees that entering a state starts the worker and exiting stops it. There is no code path that can skip either side.
- **Reduced scatter.** The position ticker goes from 6 manual start/stop sites to 1 declarative binding. Each future worker (buffer monitor, DRM renewal, cast keep-alive) is a one-liner rather than a coordination effort across multiple `OnEntry`/`OnExit` actions.
- **Eliminated bookkeeping fields.** `_renderersStarted`, `_tickerCts`, `_tickerTask` are replaced by structural lifecycle management inside `WorkerBinding`.
- **Shutdown timeout prevents hung shutdowns.** The prototype's `CancellationToken.None` on `StopAsync` is replaced with a configurable timeout. A misbehaving worker cannot block the state machine indefinitely.
- **Error identity.** The `onError` callback receives the worker instance, enabling targeted recovery (e.g., restart a specific worker vs. transition to Error).
- **Testable.** `WorkerBinding<T>` can be unit-tested independently: verify start/stop sequencing, double-start guard, timeout behavior, disposal ordering.

### Negative

- **`BindWorker` occupies the `OnEntry`/`OnExit` slots.** Stateless allows multiple `OnEntry` actions on a single state, so this is not exclusive — other actions can be chained. However, the order of multiple `OnEntryAsync` calls is the order they were configured, which requires attention.
- **Full-lifecycle binding adds startup latency on state re-entry.** Creating a new `PositionTickerWorker` on every Playing entry is cheap (it's a `PeriodicTimer` + delegate), but heavier workers (if bound this way) would pay construction cost. This is why renderers use the pause gate tier, not `BindWorker`.
- **`WorkerBinding<T>` leaks a task if shutdown times out.** The binding logs and proceeds, but the abandoned task runs until it finishes or the process exits. This is a deliberate trade-off: blocking shutdown indefinitely is worse than a leaked task. The timeout should be tuned per worker.
- **One more abstraction to understand.** Developers must learn the two-tier model (full lifecycle vs. pause gate) and choose correctly. The rule of thumb is simple: if the worker is cheap to construct and has no shared state with other workers, use `BindWorker`. If the worker is expensive to construct or must maintain state across pause/resume (decoders, renderers), use the pause gate.

### Neutral

- ADR-0022's pause gate model is unchanged. `BindWorker` is a complementary mechanism, not a replacement. The pause gate controls worker execution within a state; `BindWorker` controls worker existence across states.
- ADR-0023's dispatch loop is unchanged. `BindWorker`'s `OnEntry`/`OnExit` callbacks execute on the dispatch loop thread, maintaining the single-threaded guarantee.
- ADR-0013's cancellation semantics are preserved. The `CancellationToken` passed to `StartAsync` is cancelled on state exit; the token passed to `StopAsync` is cancelled on shutdown timeout.

## Alternatives Considered

### Keep manual `OnEntry`/`OnExit` calls

This is the status quo. It works for three workers (session, ticker, renderers) but does not scale. Each new worker adds start/stop calls to multiple states. The position ticker alone touches six `OnEntry` actions. With 5+ workers across orthogonal regions, the coordination burden becomes a source of bugs.

### Use `IHostedService` / `BackgroundService`

Rejected. `BackgroundService` is designed for host-managed lifetime (start on app start, stop on app shutdown). State-machine-driven workers need external lifecycle control (start when entering a state, stop when leaving). The execution model is fundamentally different. Wrapping `BackgroundService` as an adapter adds ceremony without benefit.

### Bind workers to Stateless's `OnTransitioning` / `OnTransitioned` hooks

Rejected. These hooks are observational — they fire after the transition has occurred and cannot prevent it. They do not guarantee entry/exit pairing under exceptions. They execute too late to provide lifecycle guarantees. `OnEntry`/`OnExit` are the correct attachment points because they are part of the transition itself.

### Single monolithic `SessionWorkerManager` that subscribes to state changes

Considered. A single object that observes `PlaybackStateChanged` and starts/stops workers based on the new state. This avoids extending Stateless but moves lifecycle logic out of the state machine configuration into a parallel observer. The result is the same scattered coordination problem in a different location, plus a race between the observer and the state machine's own actions.

### Extend `BindWorker` to support pause/resume as a third mode

Considered for future work. A `WorkerBindingOptions.PauseOnExit` variant that calls `PauseAsync` / `ResumeAsync` instead of `StopAsync` / `StartAsync` on state transitions would unify the two tiers. This is deferred because the pause gate (ADR-0022) is already proven and the renderers' lifecycle is managed by `PlaybackSession`, not the controller. Revisit if the controller directly manages renderer lifecycle in the future.

## Migration Path

### Phase 1: Position ticker (immediate)

1. Implement `IStateBoundWorker` interface.
2. Implement `WorkerBinding<T>` with shutdown timeout and double-start guard.
3. Implement `BindWorker` extension method.
4. Implement `PositionTickerWorker : IStateBoundWorker`.
5. Replace `StartPositionTicker()` / `StopPositionTicker()` / `_tickerCts` / `_tickerTask` with a single `BindWorker` call on `PlaybackState.Playing`.
6. Remove manual `StopPositionTicker()` calls from `Paused`, `Rebuffering`, `Ended`, `Stopped`, `Error`, and `DisposeAsync`.
7. Add `await DisposeSessionAsync()` to `Error.OnEntry`.
8. Unit test `WorkerBinding<T>` independently.

### Phase 2: Seek responsiveness (immediate)

1. Change `SeekCommand` handling to fire seeking state transitions and start the seek without awaiting.
2. Have the session post `SeekCompleted` (or `FatalError` on failure) back through `PostInternalAsync`.
3. Verify that `StopAsync` during a seek cancels the seek via session CTS.

### Phase 3: Rebuffering correction (immediate)

1. Remove `_session.PauseAsync()` from `Rebuffering.OnEntry`.
2. Verify that the pipeline stalls naturally on empty queues during buffer underrun.
3. Verify that the demuxer continues fetching data (not blocked by pause gate) so buffers can refill.

### Phase 4: Future workers (incremental)

Add `IStateBoundWorker` implementations as orthogonal regions are built. Each is a `BindWorker` call on the appropriate state — no changes to the binding infrastructure.
