# ADR-0028: Internal Layering and Ownership Cleanup

**Status:** Proposed. Refined by ADR-0036: the layering boundary
described here (controller / session / pipeline) gets a cleaner seam
between decode and playback. The decode half lifts into
`IDecodedMediaStream` in `FrameFlow.Decoding`; the playback half
(state machine, AV-sync, sinks) keeps the controller/session shape.
**Date:** 2026-04-12
**Supersedes:** None (refines ADR-0022 §structural-decomposition, ADR-0024, ADR-0026)
**Related:** ADR-0003 (audio-master sync), ADR-0005 (native resource ownership), ADR-0020 (lifecycle decoupled from processing), ADR-0021 (looped playback), ADR-0022 (long-lived workers with pause gate), ADR-0023 (hierarchical state machine), ADR-0024 (PlaybackController as public API), ADR-0025 (video sink and frame pool), ADR-0026 (state-bound worker lifecycle binding), ADR-0036 (decode/playback decoupling — refines this ADR's layering boundary)

## Context

With the public API surface stabilized (ADR-0027), the three internal layers — `PlaybackController`, `PlaybackSession`, and `PipelineController` — warrant structural review. The current implementation is functional and stable, but several ownership ambiguities and responsibility overlaps have emerged that will compound as the codebase grows.

### Intended architecture (from ADR-0024)

```
PlaybackController          (policy: state machines, command dispatch, events)
 └── PlaybackSession         (mechanism: pipeline assembly, resource lifecycle)
      └── PipelineController  (threading: gate, barrier, worker coordination)
           ├── DemuxPumpWorker
           ├── VideoSinkWorker / VideoDiscardWorker
           └── AudioDecodeWriteWorker
```

The controller decides **what** state transitions are valid. The session decides **how** to execute them (open demux, create decoders, wire sinks). The pipeline controller manages **when** workers run (gate open/close, barrier sync, cycle tracking).

This separation is coherent in intent but the implementation has drifted in six specific ways.

### Problem 1: Clock mutation is scattered across all three layers

`IPlaybackClock` is a single mutable object owned by the controller (which creates it), shared with the session (which mutates it), and read by the pipeline controller (which uses it for A/V sync).

| Layer | Clock operations |
|-------|-----------------|
| PlaybackController | Creates and holds the clock reference. Passes it to `SessionFactory.CreateSession(clock)`. |
| PlaybackSession | `_clock.Start()` in `StartRenderersAsync`, `_clock.Pause()` in `PauseAsync`, `_clock.Resume()` in `ResumeAsync`, `_clock.Seek()` in `SeekAsync`, `_clock.Stop()` in `DisposeAsync`. |
| PipelineController | `_clock.Position` in `RunVideoSinkWorkerAsync` for A/V sync delay calculation. |

There is no single owner of clock state transitions. The controller owns the object; the session mutates it; the pipeline controller reads it. If the controller ever needs to pause the clock directly (e.g., for a future buffering strategy), it would race with session-level clock mutations. Correctness today depends on the controller always calling session methods sequentially through the dispatch loop, but this is an implicit invariant with no structural enforcement.

### Problem 2: Loop restart bypasses the seek state machine

The `PlaybackController` manages seek state transitions through a dedicated `SeekState` FSM (`NotSeeking → SeekPending → SeekInProgress → NotSeeking`). But the repeat-mode internal transitions in `Playing` call `session.SeekAsync(TimeSpan.Zero)` directly:

```csharp
// PlaybackController — Playing state, RepeatMode.One internal transition
.InternalTransitionAsyncIf(PlaybackTrigger.LastFrameRendered,
    () => _repeat.State == RepeatMode.One,
    async () =>
    {
        _loopCount++;
        _loopRestartedSubject.OnNext(new LoopRestarted(_loopCount, Duration));
        if (_session is not null)
            await _session.SeekAsync(TimeSpan.Zero);
    })
```

This performs a full pipeline flush-and-reposition without transitioning the seek state machine. Consequences:

- `SeekState` stays `NotSeeking` during a loop restart seek.
- `SeekStateChanged` observers never fire. Consumers watching for seek activity to show UI spinners or disable controls will not be notified.
- `IsPlaying` (which checks `SeekingState == NotSeeking`) returns `true` during the loop restart seek, even though the pipeline is mid-flush.
- If a user-initiated seek arrives during a loop restart seek, there is no seek-state-machine protection against concurrent seeks — both the loop restart and the user seek run against the session simultaneously.

### Problem 3: Controller tracks session-internal lifecycle state

The controller maintains a `_sessionPlaybackActivated` boolean to distinguish first-play from resume:

```csharp
private async Task EnterPlayingFromPlayAsync()
{
    if (!_sessionPlaybackActivated)
    {
        await session.StartRenderersAsync();
        _sessionPlaybackActivated = true;
        return;
    }
    await session.ResumeAsync();
}
```

This is state that belongs to the session — the session knows whether its renderers have been started. Having the controller track a session-internal lifecycle stage creates a cross-boundary coupling: the controller must remember to reset `_sessionPlaybackActivated` on stop, on dispose, on error, and on reload. If any reset path is missed, a stale `true` value causes the controller to call `ResumeAsync` on a session whose renderers were never started.

### Problem 4: Mutable callback delegates create temporal coupling

`IPlaybackSession` communicates back to the controller through four mutable `Action` delegates:

```csharp
Action? OnEndOfStream { get; set; }
Action<Exception>? OnWorkerFaulted { get; set; }
Action? OnBufferReady { get; set; }
Action? OnBufferUnderrun { get; set; }
```

These are set by the controller after construction but before `InitializeAsync`:

```csharp
_session = _sessionFactory.CreateSession(_clock);
_session.OnEndOfStream = () => PostInternalAsync(PlaybackTrigger.LastFrameRendered);
_session.OnWorkerFaulted = ex => PostInternalAsync(PlaybackTrigger.FatalError, ex);
// ...
await _session.InitializeAsync(source);
```

And nulled out in two places: `PlaybackSession.DisposeAsync` (which nulls all four) and `PlaybackController.DetachSessionCallbacks` (which also nulls all four). This creates:

- **Temporal coupling**: the callbacks must be wired between construction and initialization. There is no compile-time enforcement of this ordering.
- **Dual-ownership of nulling**: both the session and the controller null the delegates during teardown, creating ambiguity about who is responsible for detachment.
- **A null-invocation window**: if a worker fires `OnEndOfStream` after `DetachSessionCallbacks` but before `DisposeAsync` completes the pipeline shutdown, the delegate is null and the event is silently lost.

### Problem 5: PipelineController combines orchestration with domain logic

`PipelineController` is ~1000 lines and contains two distinct kinds of code:

**Orchestration** (~400 lines): gate management, barrier synchronization, cycle tracking, pause/resume/flush coordination, shutdown sequencing. This is reusable infrastructure that would apply to any set of cooperative workers.

**Domain logic** (~500 lines): four worker loop bodies that contain media-specific processing:

| Worker method | Domain logic it contains |
|--------------|------------------------|
| `RunVideoSinkWorkerAsync` | A/V sync delay calculation, frame pool rent, pixel data extraction and copy (`AsCpu()`, `WriteData`), `PresentAsync` call, frame disposal ownership transfer |
| `RunVideoDecodeAndDiscardWorkerAsync` | Decode-and-discard loop for video streams with no sink |
| `RunAudioDecodeWriteWorkerAsync` | Audio block decode and `WriteAsync` to audio sink |
| `RunDemuxPumpWorkerAsync` | Demux pump orchestration, decoder finalization on EOF, cycle completion coordination |

The video sink worker alone is ~100 lines of dense domain logic: it reads `_clock.Position`, computes sync delay via `_syncStrategy`, rents from the frame pool, extracts CPU frame data, copies pixel bytes, and presents through the sink. This logic has no structural relationship to gate management — it just happens to live in the same class.

This coupling means:

- Any change to A/V sync logic requires understanding the gate/barrier/cycle infrastructure surrounding it.
- Any change to worker coordination requires navigating through domain-specific frame handling code.
- Testing sync behavior requires constructing a full `PipelineController` with all its orchestration machinery, rather than testing the sync logic in isolation.
- Adding a new worker variant (e.g., hardware-accelerated video, subtitle rendering) requires modifying the orchestration class rather than composing a new worker type.

### Problem 6: Three-hop property delegation chain

Read-only state flows through unnecessary intermediaries:

```
Consumer → PlaybackController.Position → PlaybackSession.Position → _clock.Position
Consumer → PlaybackController.Duration → PlaybackSession.Duration → _demuxSession.MediaInfo.Duration
Consumer → PlaybackController.MediaInfo → PlaybackSession.MediaInfo → _demuxSession.MediaInfo
```

The session adds no transformation or access control to these properties — it is pure pass-through. The controller already holds a reference to the clock (it creates it). For `Duration` and `MediaInfo`, the controller could hold a cached snapshot set during `InitializeAsync` completion, eliminating the chain without weakening encapsulation.

## Decision

### 1. Designate the session as the single owner of clock state transitions

All clock mutation methods (`Start`, `Pause`, `Resume`, `Seek`, `Stop`) must be called exclusively by `PlaybackSession`. The controller creates and owns the clock object but never mutates it directly. The pipeline controller reads `_clock.Position` for A/V sync but never writes to it.

**Ownership rule**: the clock is created by the controller, mutated by the session, and read by the pipeline controller. No other combination is permitted.

This is the current de-facto behavior, but it must be explicitly documented and enforced by review. The session's lifecycle methods (`StartRenderersAsync`, `PauseAsync`, `ResumeAsync`, `SeekAsync`, `DisposeAsync`) are the only call sites for clock mutations. If a future feature requires clock manipulation outside of these methods, it must go through a session method — not reach around the session to the clock directly.

The controller should remove any direct clock access for state reads that can be served through the session (e.g., `Position`). The controller may still read `_clock.Position` for its own use if doing so through the session would create unnecessary overhead, but it must not write to the clock.

### 2. Route loop restarts through the seek state machine

Loop restart seeks must go through the same code path as user-initiated seeks. Replace the inline `session.SeekAsync(TimeSpan.Zero)` in the repeat-mode internal transitions with a proper seek dispatch:

```csharp
// Before (bypasses seek FSM):
.InternalTransitionAsyncIf(PlaybackTrigger.LastFrameRendered,
    () => _repeat.State == RepeatMode.One,
    async () =>
    {
        _loopCount++;
        _loopRestartedSubject.OnNext(new LoopRestarted(_loopCount, Duration));
        if (_session is not null)
            await _session.SeekAsync(TimeSpan.Zero);
    })

// After (routes through seek FSM):
.InternalTransitionAsyncIf(PlaybackTrigger.LastFrameRendered,
    () => _repeat.State == RepeatMode.One,
    async () =>
    {
        _loopCount++;
        _loopRestartedSubject.OnNext(new LoopRestarted(_loopCount, Duration));
        if (_session is not null)
        {
            await _seeking.FireAsync(SeekTrigger.SeekRequested);
            await _seeking.FireAsync(SeekTrigger.FlushStarted);
            StartSeekRunner(_session, TimeSpan.Zero);
        }
    })
```

This ensures:

- `SeekState` transitions to `SeekInProgress` during the loop restart.
- `SeekStateChanged` observers are notified.
- `IsActivelyPresenting` (ADR-0027) returns `false` during the seek.
- The seek cancellation infrastructure (`_activeSeekCancellation`) protects against concurrent seeks during loop restart.
- A user-initiated seek that arrives during a loop restart properly cancels the in-flight loop seek.

### 3. Move first-play vs resume distinction into the session

Replace the controller's `_sessionPlaybackActivated` boolean with session-owned state. The session should expose a single `PlayAsync` method that internally decides whether to call `StartRenderersAsync` (first play) or `ResumeAsync` (subsequent plays):

```csharp
internal interface IPlaybackSession : IAsyncDisposable
{
    // Replace StartRenderersAsync + ResumeAsync with:
    /// <summary>
    /// Starts playback if renderers have not been activated, or resumes
    /// from a paused state if they have. The session tracks its own
    /// activation state internally.
    /// </summary>
    ValueTask PlayAsync(CancellationToken cancellationToken = default);

    // PauseAsync, SeekAsync, InitializeAsync, DisposeAsync unchanged.
}
```

The session tracks whether renderers have been started via a private field. The controller's `EnterPlayingFromPlayAsync` simplifies to:

```csharp
private async Task EnterPlayingFromPlayAsync()
{
    if (_session is not null)
        await _session.PlayAsync();
}
```

Fields removed from controller: `_sessionPlaybackActivated`.
Reset-on-stop/error/dispose concerns: eliminated — the session resets its own state on dispose.

### 4. Replace mutable callback delegates with a constructor-injected interface

Replace the four mutable `Action` delegates with a single callback interface injected at construction time:

```csharp
/// <summary>
/// Callback channel from <see cref="IPlaybackSession"/> to its owning controller.
/// Implementations must be non-blocking — callbacks may be invoked from worker
/// threads and must not perform synchronous state machine transitions.
/// </summary>
internal interface ISessionCallback
{
    void OnEndOfStream();
    void OnWorkerFaulted(Exception exception);
    void OnBufferReady();
    void OnBufferUnderrun();
}
```

The controller implements `ISessionCallback` privately (or via a nested adapter class) that routes each call through `PostInternalAsync`:

```csharp
private sealed class SessionCallbackAdapter : ISessionCallback
{
    private readonly PlaybackController _controller;

    public SessionCallbackAdapter(PlaybackController controller)
        => _controller = controller;

    public void OnEndOfStream()
        => _controller.PostInternalAsync(PlaybackTrigger.LastFrameRendered);

    public void OnWorkerFaulted(Exception exception)
        => _controller.PostInternalAsync(PlaybackTrigger.FatalError, exception);

    public void OnBufferReady()
        => _controller.PostInternalAsync(PlaybackTrigger.BufferReady);

    public void OnBufferUnderrun()
        => _controller.PostInternalAsync(PlaybackTrigger.BufferUnderrun);
}
```

The session factory accepts the callback at session creation time:

```csharp
IPlaybackSession CreateSession(IPlaybackClock clock, ISessionCallback callback);
```

Benefits:

- **No temporal coupling**: callbacks are wired at construction, not post-hoc.
- **Single owner of detachment**: the session holds a reference to the callback interface; there is no separate `DetachSessionCallbacks` method. On dispose, the session can null its own reference to the callback or simply stop calling it.
- **Compile-time completeness**: if a new callback is added to the interface, the adapter must implement it. With `Action` delegates, a new delegate property can be silently ignored.
- **Testability**: test harnesses can inject a recording `ISessionCallback` without constructing a full controller.

### 5. Extract worker loop bodies from PipelineController into dedicated types

Decompose `PipelineController` into an orchestrator and composable worker types. The orchestrator retains gate, barrier, cycle, and shutdown management. Each worker type encapsulates one domain-specific processing loop.

#### Proposed structure

```
PipelineController              (~400 lines — orchestration only)
 ├── gate, barrier, cycle tracking
 ├── StartAsync / PauseWorkersAsync / ResumeWorkers / FlushAndRepositionAsync / ShutdownAsync
 └── dispatches to workers via IPipelineWorker interface

IPipelineWorker
 ├── RunIterationAsync(CancellationToken cycleToken) : WorkerIterationResult
 └── (gate check, barrier signal, cycle coordination remain in PipelineController)

VideoSinkWorker : IPipelineWorker    (~80 lines)
 ├── A/V sync delay
 ├── frame pool rent + pixel copy
 └── sink present

VideoDiscardWorker : IPipelineWorker (~20 lines)
 └── decode and dispose

AudioDecodeWriteWorker : IPipelineWorker (~20 lines)
 └── decode and write to audio sink

DemuxPumpWorker : IPipelineWorker    (~30 lines)
 └── pump packets, finalize decoders on EOF
```

#### Worker interface

The worker interface should expose only the domain-specific iteration, not the gate/barrier/cycle boilerplate:

```csharp
internal interface IPipelineWorker
{
    /// <summary>
    /// Describes what this worker does for logging and diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns true if this worker participates in end-of-stream
    /// cycle completion tracking (enumeration workers: video, audio).
    /// The demux pump participates via a separate completion path.
    /// </summary>
    bool IsEnumerationWorker { get; }

    /// <summary>
    /// Runs one iteration of the worker's processing loop.
    /// Called by PipelineController after the gate opens and a
    /// cycle token is established. Returns when the iteration
    /// ends naturally (EOF), is cancelled (pause/shutdown), or faults.
    /// </summary>
    Task<WorkerIterationResult> RunAsync(CancellationToken cycleToken);
}

internal enum WorkerIterationResult
{
    /// <summary>The iteration was cancelled (pause or shutdown).</summary>
    Cancelled,

    /// <summary>The stream ended naturally.</summary>
    NaturalEnd,
}
```

The `PipelineController` runs the outer loop (gate wait → cycle token → barrier signal → call `worker.RunAsync` → handle result → repeat). Each worker only implements the inner processing body.

#### What this preserves

- The gate/barrier/cycle coordination in `PipelineController` is unchanged.
- Worker types receive their dependencies (decoder, sink, clock, sync strategy) at construction time.
- `PipelineController` constructs workers based on the available decoders and sinks, just as it currently selects which `Run*WorkerAsync` method to spawn.

#### What this enables

- **Isolated testing**: the video sink worker's sync logic can be tested by calling `RunAsync` directly with a fake decoder, fake sink, and fake clock — without constructing the full pipeline controller.
- **New worker variants**: adding a hardware-accelerated video worker or a subtitle worker means implementing `IPipelineWorker`, not modifying `PipelineController`.
- **Metrics per worker**: each worker type can own its own counters (frames decoded, frames presented, frames dropped) without sharing static fields.

### 6. Shorten the property delegation chain

The controller should read `Position` directly from the clock it already holds, and cache `MediaInfo` and `Duration` at initialization time rather than delegating through the session on every access.

#### Position

The controller already holds `_clock`. Replace:

```csharp
// Before:
public TimeSpan Position => _session?.Position ?? TimeSpan.Zero;

// After:
public TimeSpan Position => _clock.Position;
```

This eliminates the `Controller → Session → Clock` hop. The session's `Position` property can be removed from `IPlaybackSession` since no other consumer needs it.

#### Duration and MediaInfo

These are immutable once loading completes. The controller should capture them when `InitializeAsync` succeeds:

```csharp
// In the Initializing.OnEntry action, after session.InitializeAsync:
_loadedMediaInfo = _session.MediaInfo;
_loadedDuration = _session.Duration;

// Public properties:
public MediaInfo? MediaInfo => _loadedMediaInfo;
public TimeSpan Duration => _loadedDuration;
```

On stop or error, these are cleared:

```csharp
private async ValueTask DisposeSessionAsync()
{
    // ... existing disposal ...
    _loadedMediaInfo = null;
    _loadedDuration = TimeSpan.Zero;
}
```

The session's `Duration` and `MediaInfo` properties can remain for internal use, but the controller no longer delegates through them on every consumer access.

### 7. Use BindWorker for the position ticker

ADR-0026 introduced `BindWorker` for declaratively tying worker lifetime to FSM states. The position ticker is the canonical use case — it is cheap to construct, has no shared state with other workers, and its lifetime exactly matches the `Playing` state. Replace the manual `_tickerBinding` wiring:

```csharp
// Before (manual):
_playback.Configure(PlaybackState.Playing)
    .OnEntryAsync(async () =>
    {
        await _tickerBinding.StartAsync();
    })
    .OnExitAsync(() => _tickerBinding.StopAsync())

// After (declarative):
_playback.Configure(PlaybackState.Playing)
    .BindWorker<PlaybackState, PlaybackTrigger, PositionTickerWorker>(
        () => new PositionTickerWorker(_clock, _positionTickSubject, _logger),
        logger: _logger)
```

Fields removed: `_tickerBinding`.
Manual stop calls removed from: `DisposeAsync`.

This change is orthogonal to the pipeline worker decomposition (Decision 5). Pipeline workers use the pause gate tier (ADR-0026 §4) because they must survive across `Playing ↔ Paused` transitions. The ticker uses the full-lifecycle tier because it is cheap to recreate and has no state to preserve.

## Pushback

### "Designating the session as clock owner is just documentation, not enforcement."

Correct. There is no compile-time mechanism to prevent the controller from calling `_clock.Pause()`. The alternative — making the clock reference private to the session and not passing it to the controller — would require the controller to read `Position` through the session, which reintroduces the delegation chain (Problem 6). The pragmatic middle ground is: the controller holds a read-only view of the clock for `Position`, and the session holds the same reference with mutation rights. This is enforced by code review and documented convention, not by the type system. If the boundary is violated in the future, introducing an `IReadOnlyPlaybackClock` interface that omits mutation methods would be a targeted fix.

### "Routing loop restarts through the seek FSM adds latency to seamless loops."

The seek FSM transitions (`SeekRequested → FlushStarted`) are in-memory state machine operations — they take microseconds. The actual latency is in the pipeline flush and demux reposition, which already happens today. The FSM transitions add no measurable overhead. The benefit — correct seek state tracking, observer notification, and concurrent-seek protection — is worth the negligible cost.

### "Extracting worker types from PipelineController adds more files and types."

It does. `PipelineController` is currently ~1000 lines. After extraction:

| Component | Estimated size |
|-----------|---------------|
| `PipelineController` (orchestration) | ~400 lines |
| `VideoSinkWorker` | ~80 lines |
| `VideoDiscardWorker` | ~20 lines |
| `AudioDecodeWriteWorker` | ~20 lines |
| `DemuxPumpWorker` | ~30 lines |
| `IPipelineWorker` interface | ~20 lines |
| **Total** | ~570 lines |

The total is smaller than the current single class because the extracted workers do not need the gate/barrier/cycle boilerplate — the orchestrator handles that uniformly. More importantly, each file has a single responsibility and can be understood in isolation.

### "The ISessionCallback interface is ceremony for four callbacks."

Four callbacks is the current count. The interface provides compile-time completeness checking: if a fifth callback is added (e.g., `OnPlaybackRateChanged`), every implementer must handle it. With `Action` delegates, a new property can be silently ignored. The ceremony is proportional to the risk of silent omission.

### "Caching MediaInfo and Duration in the controller duplicates state."

It does, but the duplicated state is immutable once set and cleared on session dispose. The alternative — reading through the session on every access — is three hops of nullable delegation for data that never changes after load. The cache is a performance and clarity optimization, not a semantic change.

## Consequences

### Positive

- **Clock ownership is explicit.** One sentence — "the session mutates the clock" — replaces implicit knowledge scattered across three classes. Future developers know where to look when clock behavior is wrong.
- **Loop restart is safe.** Seek state machine protection eliminates the risk of concurrent loop-restart and user-initiated seeks. Observers are notified consistently regardless of seek origin.
- **Controller is thinner.** Removing `_sessionPlaybackActivated`, shortening the property delegation chain, and delegating first-play/resume to the session reduces controller complexity. The controller focuses on policy (state machines, command dispatch) and the session focuses on mechanism.
- **Callback wiring is compile-time safe.** `ISessionCallback` prevents silent omission of new callbacks and eliminates the temporal coupling of post-construction delegate assignment.
- **PipelineController becomes a reusable orchestrator.** Extracting domain logic into worker types means the gate/barrier/cycle infrastructure can coordinate any set of workers without modification. New worker variants (hardware video, subtitles) are additive.
- **Worker logic is independently testable.** Each worker can be tested with focused fakes (fake decoder, fake sink, fake clock) without constructing the full orchestration stack.

### Negative

- **More internal types.** The worker extraction adds ~5 new files. This is justified by the reduction in per-type complexity and the testability improvement.
- **Clock ownership is convention, not compiler-enforced.** A developer can still call `_clock.Pause()` from the controller. Review discipline is required.
- **ISessionCallback adds a level of indirection.** The controller's callback adapter is a small class, but it is a new type that must be understood.
- **Caching MediaInfo/Duration requires explicit cache invalidation.** The cache must be cleared on stop, error, and dispose. Missing a clear site would serve stale data. This is mitigated by clearing in `DisposeSessionAsync`, which is already the single teardown path.

### Neutral

- **ADR-0022's pause gate model is unchanged.** The gate/barrier mechanism in `PipelineController` is preserved exactly as-is. Worker extraction does not change the coordination protocol — it only moves the domain logic out of the coordinator.
- **ADR-0023's command channel dispatch is unchanged.** All command serialization and state machine processing remain on the dispatch loop.
- **ADR-0026's WorkerBinding/BindWorker is unchanged.** The ticker binding is a direct application of the existing pattern. Pipeline workers continue to use the pause gate tier as specified in ADR-0026 §4.
- **ADR-0003's sync strategy is unchanged.** The `ISyncStrategy.GetVideoDelay` call moves from an inline block in `PipelineController` to the `VideoSinkWorker`, but the algorithm and inputs are identical.
- **ADR-0005's resource ownership rules are unchanged.** Frame disposal, sink ownership boundaries, and decoder lifecycle are preserved.

## Alternatives Considered

### Introduce IReadOnlyPlaybackClock to enforce clock ownership at the type level

A read-only interface exposing only `Position`, `IsRunning`, and `IsPaused` would let the controller and pipeline controller hold a read-only view while only the session holds the mutable `IPlaybackClock`. This provides compiler-enforced ownership.

Deferred. The current codebase has no violations — the controller does not mutate the clock today. Introducing the interface adds a type to the public surface (since `IPlaybackClock` is already public) for a problem that has not yet manifested. If a violation is introduced in the future, this interface can be added as a targeted fix.

### Make PlaybackSession own the clock entirely (no controller reference)

This would force `Position` to be read through the session, reintroducing the delegation chain. It also prevents the controller from accessing `_clock.Position` for its own diagnostics or for the position ticker worker (which needs the clock at construction time). Rejected in favor of shared-reference with convention-based mutation ownership.

### Use events instead of ISessionCallback

C# events (`event Action OnEndOfStream`) are structurally similar to the current mutable delegates but with different subscription semantics. They do not solve the temporal coupling problem (events must still be subscribed after construction) and add the complexity of multi-subscriber semantics that the session does not need. A single-consumer callback interface is simpler and more explicit.

### Use IObservable<T> for session-to-controller communication

The controller already uses `IObservable<T>` for consumer-facing events. Using the same pattern for internal session→controller communication would be consistent. However, `IObservable<T>` is designed for multi-subscriber broadcast with backpressure semantics. The session→controller channel is a single-consumer, fire-and-forget callback. The overhead of subject creation, subscription management, and disposal is not justified for four simple callbacks.

### Leave worker loop bodies inline in PipelineController

This is the status quo. It works for the current three worker variants, but the video sink worker alone is ~100 lines of dense domain logic. The sync delay calculation, frame pool management, and pixel copy logic have no relationship to gate/barrier coordination. As new workers are added (hardware video, subtitles), the class grows linearly. Extraction is justified by the current size and the anticipation of growth.

### Extract workers as standalone classes that manage their own gate interaction

Instead of having `PipelineController` run the outer loop and call `worker.RunAsync`, each worker would own its own gate interaction. This was the original design in ADR-0022's structural decomposition section. Rejected because it duplicates the gate/barrier/cycle boilerplate in every worker. The centralized outer loop in `PipelineController` is DRY — workers focus on domain logic, the orchestrator focuses on coordination.

## Migration Path

These decisions are independent and can be implemented in any order. The recommended sequence minimizes risk by addressing correctness issues first and structural improvements second.

### Phase 1: Correctness fixes (high priority, low risk)

**1a. Route loop restarts through seek FSM (Decision 2).**
Change the `RepeatMode.One` internal transition to use `StartSeekRunner` instead of direct `session.SeekAsync`. This is a localized change to the controller's state machine configuration.

Verify: loop restart emits `SeekStateChanged` events. Concurrent user seek during loop restart cancels the loop seek cleanly. `IsActivelyPresenting` returns `false` during loop restart seek.

**1b. Use BindWorker for the position ticker (Decision 7).**
Replace the manual `_tickerBinding.StartAsync()`/`StopAsync()` calls with a single `BindWorker` declaration on `PlaybackState.Playing`. Remove the `_tickerBinding` field and the manual stop in `DisposeAsync`.

Verify: ticker starts on Playing entry, stops on any exit (Paused, Stopped, Error, Ended). No position updates emitted after exiting Playing.

### Phase 2: Ownership clarification (medium priority, low risk)

**2a. Move first-play/resume into session (Decision 3).**
Add `PlayAsync` to `IPlaybackSession`. Internalize the `_renderersActivated` tracking. Remove `_sessionPlaybackActivated` from the controller. Simplify `EnterPlayingFromPlayAsync`.

Verify: first play after load calls `StartRenderersAsync`. Resume after pause calls `ResumeAsync`. Multiple load cycles reset the activation flag correctly.

**2b. Replace callback delegates with ISessionCallback (Decision 4).**
Define the interface. Implement the adapter in the controller. Update `IPlaybackSessionFactory.CreateSession` to accept the callback. Remove `DetachSessionCallbacks` from the controller. Remove delegate nulling from both session and controller disposal.

Verify: all four callbacks are delivered. Session disposal stops callback delivery. No null-reference exceptions during teardown races.

**2c. Shorten property delegation chain (Decision 6).**
Read `Position` from `_clock` directly. Cache `MediaInfo` and `Duration` in the controller after initialization. Clear the cache in `DisposeSessionAsync`.

Verify: `Position` returns clock time. `Duration` and `MediaInfo` are correct after load, null/zero after stop.

**2d. Document clock ownership convention (Decision 1).**
Add XML documentation to `IPlaybackClock` specifying that only `PlaybackSession` may call mutation methods. Add a comment block in `PlaybackController` noting that it holds a read-only view.

### Phase 3: Structural decomposition (medium priority, medium risk)

**3a. Define IPipelineWorker interface (Decision 5).**
Create the interface with `Name`, `IsEnumerationWorker`, and `RunAsync`. Define `WorkerIterationResult`.

**3b. Extract VideoSinkWorker.**
Move the body of `RunVideoSinkWorkerAsync` into `VideoSinkWorker.RunAsync`. The worker receives `IVideoDecoder`, `IVideoSink`, `ISyncStrategy`, `IPlaybackClock`, and `ILogger` at construction. `PipelineController` instantiates it in `StartAsync` and calls it from the outer loop.

**3c. Extract remaining workers.**
Extract `VideoDiscardWorker`, `AudioDecodeWriteWorker`, and `DemuxPumpWorker` following the same pattern.

**3d. Generalize PipelineController's outer loop.**
Replace the three named worker methods with a loop over `IReadOnlyList<IPipelineWorker>`. The gate/barrier/cycle logic operates uniformly over the worker collection.

Verify at each sub-step: all existing pipeline controller tests pass. Seek, pause, resume, loop, and EOF behavior are unchanged. No regression in A/V sync.

## Compliance Checklist

When implementing this ADR, verify:

- [ ] No production code outside `PlaybackSession` calls `IPlaybackClock.Start()`, `.Pause()`, `.Resume()`, `.Seek()`, or `.Stop()`
- [ ] Loop restart seeks emit `SeekStateChanged` transitions (SeekRequested → FlushStarted → ... → SeekCompleted)
- [ ] `IsActivelyPresenting` (ADR-0027) returns `false` during loop restart seeks
- [ ] A user-initiated seek during a loop restart cancels the loop seek via `_activeSeekCancellation`
- [ ] `_sessionPlaybackActivated` is removed from `PlaybackController`
- [ ] `IPlaybackSession.PlayAsync` handles first-play vs resume internally
- [ ] `ISessionCallback` is injected at session construction, not wired post-construction
- [ ] `DetachSessionCallbacks` is removed from the controller
- [ ] `PlaybackController.Position` reads from `_clock.Position` directly, not through the session
- [ ] `PlaybackController.MediaInfo` and `Duration` are cached at load time and cleared on dispose
- [ ] Position ticker uses `BindWorker` on `PlaybackState.Playing` — no manual start/stop calls
- [ ] `IPipelineWorker.RunAsync` contains only domain logic — no gate, barrier, or cycle code
- [ ] `PipelineController` outer loop handles gate wait, barrier signal, and cycle management uniformly for all workers
- [ ] All existing `PipelineController` tests pass after worker extraction
- [ ] All existing `PlaybackController` tests pass after callback and property changes
- [ ] All existing integration tests pass end-to-end
