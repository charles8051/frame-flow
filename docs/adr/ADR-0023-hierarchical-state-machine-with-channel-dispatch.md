# ADR-0023: Hierarchical State Machine with Channel-Serialized Dispatch

**Status:** Proposed
**Date:** 2026-04-07
**Supersedes:** None (refines ADR-0009, ADR-0022)
**Related:** ADR-0003 (audio-master sync), ADR-0008 (result types), ADR-0009 (threading and concurrency), ADR-0020 (lifecycle decoupled from processing), ADR-0021 (looped playback), ADR-0022 (long-lived workers with pause gate)

## Context

### The current state machine is too flat

FrameFlow's `PlaybackStateMachine` uses a `HashSet<(PlaybackState, PlaybackState)>` transition table with 27 manually enumerated valid transitions. This works, but has structural problems:

1. **No hierarchy.** Loading substates (Opening → Paused) and Ready substates (Paused ↔ Playing) are flattened into a single enum. Guards like "stop from any active state" must be expressed as individual transitions from every state, not as a single exit from a composite.

2. **No parameterized triggers.** Seek carries a `TimeSpan`, Load carries an `IMediaSource`, FatalError carries an exception. The current model passes these out-of-band through method parameters, separate from the transition itself.

3. **No guard expressions.** The repeat-mode guard on `Playing → Ended` (only reachable when `RepeatMode == Off`) is implemented as procedural logic in `PlaybackSession`, not as a declarative guard on the transition. This scatters state logic across multiple classes.

4. **No async entry/exit actions.** State entry side effects (initializing decoders, buffering to threshold) are called imperatively by session methods rather than being attached to state entry. This makes the state machine a passive validator rather than an active orchestrator.

5. **Thread safety is manual.** `PlaybackStateMachine` uses `Interlocked.CompareExchange` for atomic transitions, but compound operations (check state + perform action + transition) are not atomic. Callers must acquire their own locks or accept TOCTOU races.

### What a production player needs

Study of HTML5 MediaElement, ExoPlayer (Android Media3), AVPlayer (iOS), and VLC reveals that production players model playback as a hierarchical state machine with orthogonal concurrent regions:

- A **primary playback HSM** with composite states (Loading ⊃ {Initializing, Preparing, InitialBuffering}, Ready ⊃ {Paused, Playing, Rebuffering}) and a terminal Destroyed state.
- **Orthogonal regions** for seeking, repeat mode, volume, playback rate, and other concerns that evolve independently of the primary state.
- **Cross-region guards** where one region's state gates transitions in another (e.g., `Playing → Ended` is guarded by `RepeatMode == Off`).

### Why channel-serialized dispatch

Whichever state machine library is used, concurrent trigger firings corrupt internal state. The standard solution is to serialize all state mutations through a single dispatch loop reading from a bounded `Channel<IPlayerCommand>`. This eliminates:

- Lock contention on the state machine
- TOCTOU races between guard evaluation and transition
- The need for callers to understand synchronization

Public API methods become thin wrappers that post a command and await its completion. Worker tasks post internal triggers (BufferReady, LastFrameRendered, FatalError) through the same channel via a non-blocking `TryWrite` path.

## Decision

### 1. Adopt Stateless for the primary playback state machine

The primary playback state machine will be implemented using the [Stateless](https://github.com/dotnet-state-machine/stateless) library (`dotnet-state-machine/stateless`). This is a mature, zero-dependency NuGet package with 50M+ downloads that provides:

| Requirement | Stateless feature |
|-------------|-------------------|
| Hierarchical states | `SubstateOf()` |
| Guards | `PermitIf()` with lambdas |
| Internal transitions | `InternalTransitionIf()` — no exit/entry fires |
| Async entry/exit | `OnEntryAsync()`, `FireAsync()` |
| Parameterized triggers | `SetTriggerParameters<T>()` |
| Pre-fire validation | `CanFire()` — used for Result pattern |
| Diagram generation | `UmlDotGraph.Format()` for diagnostics |

The existing `PlaybackStateMachine` class will be retired in favor of `StateMachine<PlaybackState, PlaybackTrigger>`.

### 2. Primary state hierarchy

```
Idle
Loading (composite)
  ├─ Initializing        (resolving source, opening container)
  ├─ Preparing           (parsing metadata, negotiating codecs)
  └─ InitialBuffering    (filling buffer to playable threshold)
Ready (composite)
  ├─ Paused
  ├─ Playing
  └─ Rebuffering         (buffer underrun, auto-resume when refilled)
Ended
Stopped
Error
Destroyed (terminal)
```

Key transitions:

- `Idle → Initializing` on `Load(source)` (parameterized trigger)
- `InitialBuffering → Playing` or `→ Paused` based on `playWhenReady` flag
- `Playing → Ended` only when `RepeatMode == Off` (cross-region guard)
- `Playing → Playing` (internal transition) for RepeatOne loop restart
- `Ready.* → Stopped`, `Loading.* → Stopped` via composite exit
- `* → Error` from any non-terminal state (parameterized with error details)
- `* → Destroyed` from any state via `Release()` (terminal, no transitions out)

### 3. Orthogonal regions via composition

Orthogonal concerns are modeled as separate `StateMachine<TState, TTrigger>` instances. All machines fire on the same dispatch loop thread, so cross-region `.State` reads are safe without locks.

**V1 scope (implement now):**

| Region | States | Rationale |
|--------|--------|-----------|
| Primary playback | 11 states | Core lifecycle — required |
| Seeking | NotSeeking, SeekPending, SeekInProgress | Seek state is observable and gates `IsPlaying` |
| Repeat/Loop | RepeatOff, RepeatOne, RepeatAll | Guards on `Playing → Ended`, loop events |

**V2 scope (design seam only, no implementation):**

| Region | States | Rationale |
|--------|--------|-----------|
| Playback rate | Normal, SlowMotion, FastForward, TrickPlay, Reverse | Requires clock rate integration |
| Volume/Audio | Unmuted, Muted, AudioDucked | Straightforward but not blocking v1 |

**Deferred (no seam needed yet):**

DRM, casting, ad insertion, live stream, audio focus, audio route, presentation mode, app lifecycle, playback suppression, subtitles, track selection, network/buffer level. These are production-player concerns that are far beyond FrameFlow's v1 scope. Designing ADR-level abstractions for them now would be speculative — the state enums and transition tables can be added later without breaking the primary machine or the dispatch loop.

### 4. Channel-serialized command dispatch

All public API methods post command objects to a bounded `Channel<IPlayerCommand>`. A single background `Task` reads commands and calls `FireAsync` on the appropriate state machine.

```
┌────────────┐     ┌───────────────────┐     ┌──────────────────┐
│ Public API │────►│ Channel<Command>  │────►│ Dispatch Loop    │
│ (callers)  │     │ (bounded, 64)     │     │ (single reader)  │
└────────────┘     └───────────────────┘     │ calls FireAsync  │
                                              └──────────────────┘
       ▲                                              │
       │ PostInternalAsync (TryWrite)                  │
┌──────┴─────┐                                        │
│ Workers    │◄───────────────────────────────────────┘
│ (callbacks)│     OnEntry/OnExit actions
└────────────┘
```

Command types:

- `FireTriggerCommand` — wraps a `PlaybackTrigger` enum value
- `SeekCommand` — carries `TimeSpan position`
- `LoadCommand` — carries `IMediaSource`
- `SetRepeatCommand` — carries `RepeatMode`

Each command carries a `TaskCompletionSource<Result>` that the dispatch loop completes after processing, allowing callers to `await` the result.

Worker tasks never call `FireAsync` directly. They invoke callback delegates that post triggers through the command channel via `TryWrite` (non-blocking).

### 5. Result integration at the dispatch boundary

Before calling `FireAsync`, the dispatch loop calls `CanFire()` to check whether the transition is valid. If not, it completes the command's TCS with `Result.Fail(ErrorCategory.InvalidOperation)` instead of catching `InvalidOperationException`. This aligns with ADR-0008's guidance: invalid state transitions are expected outcomes, not exceptional failures.

### 6. Composite IsPlaying

```csharp
public bool IsPlaying =>
    _playback.State == PlaybackState.Playing
    && _playWhenReady
    && _seeking.State == SeekState.NotSeeking;
```

Additional conditions (DRM, suppression, audio focus) will be added as their orthogonal regions are implemented in v2+.

### 7. Shutdown

Disposing the controller completes the channel writer and awaits the dispatch loop. Once the loop exits, all event subjects are disposed and the current session (if any) is torn down.

## Consequences

### Positive

- State transitions become declarative: guards, entry/exit actions, and hierarchy are co-located in configuration methods rather than scattered across session logic.
- Thread safety is structural: the dispatch loop is the only code that calls `FireAsync`. No locks needed on any state machine.
- Cross-region guards are safe: all machines fire on the same thread, so `.State` reads are race-free.
- Diagnostics improve: Stateless can generate DOT/UML graphs of the configured machine for documentation.
- The command channel provides natural backpressure and clean shutdown.

### Negative

- Adds a NuGet dependency (Stateless). This is a well-established, zero-dependency package, so the risk is low.
- Stateless does not natively support orthogonal regions. Composition of multiple machines is straightforward but requires discipline to keep cross-region interaction through guards only, never direct `FireAsync` calls across machines.
- The dispatch loop adds one `Task` per controller instance. This is negligible for a media player.
- Migration from the current `PlaybackStateMachine` is non-trivial but can be done incrementally.

### Neutral

- ADR-0009's channel-based inter-worker communication is unchanged. The command channel is a separate concern from the pipeline data channels.
- ADR-0022's pause gate model is unchanged. The pause gate controls worker execution; the command channel controls state transitions. They are orthogonal.
- ADR-0013's cancellation semantics are preserved. `CancellationToken` on public methods cancels the pending command, not the session.

## Alternatives Considered

### Keep the hand-rolled state machine and add locks

Rejected. Adding `SemaphoreSlim` around every compound operation (check + act + transition) would work but scatters synchronization concerns across every caller. The channel dispatch pattern centralizes serialization and makes it impossible to forget.

### Use a different state machine library

Considered Appccelerate.StateMachine and custom DU-based dispatch. Appccelerate is more powerful but heavier. DU dispatch (discriminated union pattern) works well for sequential flows but fights against concurrent external steering — a video player must accept play/pause/seek from the UI at any time while workers fire internal triggers. Stateless occupies the right point in the trade-off space.

### Model all orthogonal regions in v1

Rejected. The Obsidian design documents enumerate 16 orthogonal regions (DRM, casting, ads, live stream, audio focus, audio route, presentation mode, app lifecycle, playback suppression, subtitles, track selection, network/buffer level, volume, rate, seeking, repeat). Implementing all of these now would be speculative design for a library that cannot yet play a video file end-to-end. V1 needs three regions (primary, seeking, repeat). The architecture supports adding regions incrementally — each is a new `StateMachine<TState, TTrigger>` field, a configuration method, and dispatch cases.

### Make the dispatch loop synchronous

Rejected. State entry actions (initializing decoders, buffering) are inherently async. `FireAsync` with `OnEntryAsync` keeps the dispatch loop async-native without blocking thread pool threads.
