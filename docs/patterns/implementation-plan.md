# FrameFlow — Playback Orchestration Refactor Plan

> **⚠ Historical plan (completed, pre-Crossbar substrate).** The
> phases below describe the original playback-controller refactor
> that landed in 2025–early 2026. They reference types and APIs that
> were subsequently replaced by the Crossbar substrate adoption
> ([ADR-0030] unified frame contracts) and the ADR-0010 / ADR-0012
> substrate cleanups: `IVideoSink.SupportedMemoryDomains` was
> removed, `IFrameSink<T>` was deleted from Crossbar entirely, and
> `IVideoSink`/`IAudioSink` now expose a `Consumer` property instead
> of inheriting from a Crossbar base. The phase structure and
> sequencing remain a useful historical record; treat the type
> shapes in checklists as superseded.
>
> [ADR-0030]: ../adr/ADR-0030-unify-frame-contracts-with-crossbar.md

Phased implementation plan for the playback controller rewrite. Each phase
is independently testable and shippable.

**Governing ADRs:**
- [ADR-0023](../adr/ADR-0023-hierarchical-state-machine-with-channel-dispatch.md) — HSM + channel dispatch
- [ADR-0024](../adr/ADR-0024-playback-controller-as-public-api-surface.md) — controller as public API
- [ADR-0025](../adr/ADR-0025-video-sink-and-frame-pool-architecture.md) — video sink + frame pool
- [ADR-0022](../adr/ADR-0022-long-lived-workers-with-pause-gate.md) — long-lived workers + pause gate
- [ADR-0020](../adr/ADR-0020-lifecycle-decoupled-from-processing-logic.md) — lifecycle/processing separation

**Companion documents:**
- [playback-controller](playback-controller.md) — controller internals
- [playback-states](playback-states.md) — state & transition catalogue
- [playback-statechart](playback-statechart.md) — Mermaid diagrams
- [video-sink-and-frame-pool](video-sink-and-frame-pool.md) — sink & pool design

---

## Design Decisions Summary

### Why Hierarchical State Machine (Stateless library)

The current `PlaybackStateMachine` uses a flat `HashSet<(From, To)>` transition
table. This lacks guards, parameterized triggers, async entry/exit actions, and
hierarchy. The Stateless NuGet package provides all of these with zero
dependencies. See [playback-controller](playback-controller.md) section 2.6.

### Why Channel Dispatch

Stateless's `FireAsync` is not thread-safe. All public methods post commands to
a bounded `Channel<IPlayerCommand>`. A single dispatch loop reads and processes
them sequentially. This serializes state access without locks. See
[playback-controller](playback-controller.md) section 2.

### Why Audio Clock as Master

The sound card's DAC runs from its own crystal oscillator. Drift vs. wall clock
reaches 180 ms/hour at 50 ppm — detectable in 15 minutes. The existing
`PlaybackClock` + `AudioMasterSyncStrategy` implement this correctly. No change
needed. See ADR-0003.

### Why Inverted Frame Ownership

The decoder should rent frames from the sink's pool, not allocate its own. This
enables backpressure (decoder waits when all frames are in use) and positions the
architecture for GPU zero-copy in v2. See
[video-sink-and-frame-pool](video-sink-and-frame-pool.md).

### Why Result Pattern

Invalid state transitions, seek clamping, and source errors are expected
outcomes, not bugs. `Result` / `Result<T>` makes these explicit. Exceptions
are reserved for programmer errors (`ObjectDisposedException`,
`ArgumentNullException`). See ADR-0008.

---

## Phase 1 — State Machines & Controller Shell

**Goal:** Wire up all Stateless machines. No real decoder or rendering — just
the state logic and the command dispatch loop.

**Deliverables:**

- [ ] Add Stateless NuGet package to `FrameFlow.Playback`
- [ ] `PlaybackState` enum (11 values) — replaces current 9-value enum in
      `FrameFlow.Media`
- [ ] `PlaybackTrigger` enum (~13 triggers)
- [ ] `SeekState` enum and `SeekTrigger` enum
- [ ] `RepeatMode` enum (already exists via loop support) and `RepeatTrigger` enum
- [ ] `PlaybackController` class with three `StateMachine<,>` fields
- [ ] `ConfigurePlaybackMachine()` — full primary HSM wiring:
  - `SubstateOf` for Loading and Ready composites
  - `PermitIf` guards for repeat mode on `LastFrameRendered`
  - `InternalTransitionIf` for RepeatOne and RepeatAll loop boundaries
  - `OnTransitioned` callback wiring for event subjects
- [ ] `ConfigureSeekingMachine()`, `ConfigureRepeatMachine()`
- [ ] Cross-region guard: `Playing → Ended` reads `_repeat.State`
- [ ] `StateTransition<T>` record struct
- [ ] Event subjects: `PlaybackStateChanged`, `SeekStateChanged`,
      `RepeatModeChanged`, `LoopRestarted`, `ErrorOccurred`
- [ ] Unit tests: verify every transition in the table (24 transitions + 2
      repeat variants). Verify guards block invalid paths. Verify internal
      transitions don't fire exit/entry.

**What this replaces:**
- `PlaybackStateMachine` class (retire after migration)
- The `PlaybackState` enum in `FrameFlow.Media` (expand from 9 to 11 values)

**Dependencies:** None — this is the foundation.

---

## Phase 2 — Command Channel & Async Dispatch

**Goal:** Make the controller thread-safe and async-friendly via channel
serialization.

**Deliverables:**

- [ ] `IPlayerCommand` interface with `TaskCompletionSource<Result>` and
      `CancellationToken`
- [ ] Concrete command records: `FireTriggerCommand`, `SeekCommand`,
      `LoadCommand`, `SetRepeatCommand`
- [ ] `Channel<IPlayerCommand>` field — bounded(64), single-reader
- [ ] `PostAndWaitAsync()` — post command, register caller CT, await TCS
- [ ] `PostInternalAsync()` — for worker callbacks, uses `TryWrite` (non-blocking)
- [ ] `DispatchLoopAsync()` — reads channel, fires appropriate machine,
      checks `CanFire()` for `Result.Fail` on invalid transitions
- [ ] Public API: `PlayAsync()`, `PauseAsync()`, `SeekAsync()`, `LoadAsync()`,
      `SetRepeatModeAsync()`, `StopAsync()` — thin wrappers around `PostAndWaitAsync`
- [ ] `DisposeAsync()` — complete writer, await loop, dispose subjects
- [ ] Tests: concurrent calls from multiple threads don't corrupt state.
      Cancellation propagates. Backpressure works.

**Dependencies:** Phase 1

---

## Phase 3 — Result Pattern Integration

**Goal:** Replace bare `Task` returns with typed `Result` / `Result<T>`.

**Deliverables:**

- [ ] `Result` readonly record struct (unit success, or `PlaybackError`)
- [ ] `Result<T>` readonly record struct (value success, or `PlaybackError`)
- [ ] `ErrorCategory` enum: `InvalidOperation`, `Source`, `Network`, `Decode`,
      `Io`, `System`
- [ ] `PlaybackError` sealed record: `Category`, `Message`, `Inner`
- [ ] Update dispatch loop: `CanFire()` check → `Result.Fail(InvalidOperation)`
      instead of catching `InvalidOperationException`
- [ ] Update `LoadAsync` to `Task<Result>`
- [ ] Update `SeekAsync` to `Task<Result<TimeSpan>>` (clamped position)
- [ ] Tests: invalid transitions return `Fail`, not throw.

**What this replaces:** The current `ThrowIfNotIn()` pattern in `PlaybackSession`.

**Dependencies:** Phase 2

---

## Phase 4 — Frame Pool & Video Sink Contracts

**Goal:** Define the sink and pool interfaces and implement the CPU reference
implementations.

**Deliverables:**

- [ ] `IVideoFrame` interface in `FrameFlow.Media`:
  - Metadata: `PresentationTime`, `Duration`, `Width`, `Height`, `Format`,
    `MemoryDomain`
  - Ref counting: `AddRef()`, `Dispose()` returns to pool
  - CPU accessor: `AsCpu()` → `CpuFrameData?`
  - Fallback: `ToCpu()` → `CpuFrameData`
- [ ] `CpuFrameData` readonly record struct (plane data + strides)
- [ ] `FrameMemoryDomain` enum (just `Cpu` for v1)
- [ ] `IFramePool` interface: `MemoryDomain`, `RentAsync()`, `Return()`
- [ ] `IVideoSink` interface: `FramePool`, `PresentAsync()`,
      `OnFormatChangedAsync()`, `SupportedMemoryDomains`
- [ ] `IFrameConverter` interface: `ConvertAsync(source, targetPool, ct)`
- [ ] `VideoFormatInfo` record: `Width`, `Height`, `Format`, `FrameRate`
- [ ] `CpuFramePool` implementation:
  - Backed by `ArrayPool<byte>`
  - `SemaphoreSlim` for bounding concurrent rentals (3-4 frames)
  - `RentAsync` blocks when exhausted (backpressure)
- [ ] `NullVideoSink` — drops frames, dummy pool. For audio-only and tests.
- [ ] `PipelineConfig` record and `PipelineNegotiator` (CPU-only path in v1)
- [ ] Tests: rent/return cycle. Ref counting. Pool exhaustion blocks. Dispose
      returns to pool.

**What this replaces:**
- `IDecodedVideoFrame` interface
- `CpuVideoFrame` class
- `IVideoFramePresenter` interface

**Dependencies:** None — can be built in parallel with Phases 1-3.

---

## Phase 5 — PlaybackSession Refactor

**Goal:** Refactor `PlaybackSession` from a public API type to an internal
per-media-item lifecycle manager called by the controller.

**Deliverables:**

- [ ] `PlaybackSession` becomes `internal sealed class`
- [ ] Session exposes lifecycle methods only (no transport controls):
  - `InitializeAsync()` — open source, create decoders
  - `ParseMetadataAsync()` — parse container metadata
  - `BufferToThresholdAsync()` — start demux + decoders, fill buffer
  - `StartRenderers()` — start present workers
  - `Pause()` / `Resume()` — delegate to `PipelineController`
  - `SeekAsync()` / `SeekInternal()` — flush + reposition
  - `DisposeAsync()` — tear down everything
- [ ] Callback properties wired at creation:
  - `OnBufferThresholdMet`, `OnBufferUnderrun`, `OnLastFrameRendered`,
    `OnFatalError`, `OnSeekComplete`
- [ ] Session uses `IVideoSink` + `IFramePool` instead of `IVideoFramePresenter`
- [ ] Session uses `PipelineNegotiator` to select pool and optional converter
- [ ] Wire session into controller: `OnEntry(Initializing)` creates session,
      `OnEntry(Stopped)` disposes session
- [ ] Tests: mock decoder/sink. Pipeline starts and stops cleanly. Seek flushes
      and restarts. Pause gate blocks workers. EOS propagation.

**What this replaces:** The current public `PlaybackSession` and its
`IPlaybackSession` interface.

**Dependencies:** Phases 1-4 (all converge here)

---

## Phase 6 — Presenter Migration

**Goal:** Migrate existing presenters (SDL, Avalonia) from `IVideoFramePresenter`
to `IVideoSink`.

**Deliverables:**

- [ ] `SdlVideoSink : IVideoSink` — wraps SDL_Texture, owns a `CpuFramePool`
  - `PresentAsync`: `SDL_UpdateTexture` + `SDL_RenderCopy` from `CpuFrameData`
  - `SupportedMemoryDomains`: `[Cpu]`
- [ ] `AvaloniaVideoSink : IVideoSink` — wraps `WriteableBitmap`, owns a
      `CpuFramePool`
  - `PresentAsync`: copy into bitmap, marshal to UI thread
  - `SupportedMemoryDomains`: `[Cpu]`
- [ ] Update DI registration: `AddFrameFlowPlayback()` registers
      `IPlaybackControllerFactory` instead of `IPlaybackSessionFactory`
- [ ] Update example apps to use `IPlaybackController`
- [ ] Remove old `IVideoFramePresenter` and `IPlaybackSession` interfaces
- [ ] Remove `PlaybackStateMachine` class

**Dependencies:** Phase 5

---

## Phase 7 — Integration & Edge Cases

**Goal:** Handle remaining edge cases and hardening.

**Deliverables:**

- [ ] **Seek edge cases:**
  - Seek during seek (cancel first, start new)
  - Seek past duration (clamp → may trigger Ended)
  - Seek before 0 (clamp to 0)
- [ ] **Position ticker:**
  - `IObservable<TimeSpan> PositionTick` — periodic ~250ms emission
  - Reads `IPlaybackClock.Position`, pauses when not Playing
- [ ] **Resource cleanup verification:**
  - `DisposeAsync` disposes session, completes channel, disposes subjects
  - No leaked tasks, handles, or pool buffers
- [ ] **Diagnostic / observability:**
  - DOT graph generation via `UmlDotGraph.Format()` for each machine
  - Structured logging of state transitions (source-generated `[LoggerMessage]`)
  - `DroppedFrames`, `BufferHealth` stats
- [ ] Integration tests: full Load → Play → Seek → Pause → Stop → Dispose cycle
      with mock decoder and null sink

**Dependencies:** All prior phases

---

## Dependency Map

```
Phase 1 (State Machines)  ──► Phase 2 (Channel Dispatch)
                                    │
                                    ▼
                              Phase 3 (Result Pattern)
                                    │
                                    ▼
Phase 4 (Frame Pool + Sink) ──────► Phase 5 (Session Refactor)
                                    │
                                    ▼
                              Phase 6 (Presenter Migration)
                                    │
                                    ▼
                              Phase 7 (Integration)
```

**Parallelism:** Phases 1-3 (state + dispatch + results) and Phase 4
(frame pool + sink contracts) can proceed in parallel on separate tracks.

---

## Key Architectural Constraints

### Threading Model

```
┌───────────────────────────────────────────────┐
│  Dispatch loop task                            │
│  (reads command channel, fires state machines, │
│   calls session lifecycle methods)             │
│                                                │
│  ONLY code that touches StateMachine instances  │
└───────────────────────────────────────────────┘
         ^ PostAndWaitAsync        ^ PostInternalAsync
         | (from callers)          | (from workers)
┌────────+───┐             ┌──────+──────────────┐
│ Caller     │             │ Worker tasks         │
│ thread     │             │ (demux, decode,      │
│            │             │  present)             │
└────────────┘             └─────────────────────┘
```

### Cancellation Hierarchy

```
_controllerCts                        (controller lifetime)
   +-- _sessionCts                    (per-media-item, killed on Stop/Load-new)
        +-- shutdown CTS              (in PipelineController, killed only by Stop/Dispose)
             +-- demux pump token
             +-- video decode token
             +-- video present token
             +-- audio decode+write token
```

### Backpressure Chain

```
Sound card consumption rate
  -> Audio sink blocks on full device buffer
    -> AudioSamples channel blocks on full queue
      -> Audio decoder blocks on WriteAsync
        -> AudioPackets channel blocks on full queue
          -> Demuxer blocks on WriteAsync

FramePool backpressure
  -> Video presenter blocks waiting for sync time
    -> VideoFrames channel blocks on full queue
      -> Video decoder blocks on WriteAsync
        -> FramePool.RentAsync blocks (all surfaces in use)

Both chains converge at the demuxer.
```

### State Machine Interaction Rules

1. Each orthogonal region is a separate `StateMachine<TState, TTrigger>`.
2. All machines fire on the same dispatch loop thread — `.State` reads are safe.
3. Machines read each other via guards, never call `FireAsync` on each other.
4. Workers never touch any state machine — they post via `PostInternalAsync`.

---

## Migration Strategy

This refactor does not require a big-bang rewrite. The phases are ordered so that
each can be merged independently. Key principles:

1. **New types coexist with old types** during migration. `IPlaybackController`
   can be introduced while `IPlaybackSession` still exists.
2. **Feature parity before removal.** Old interfaces are removed only after the
   new path handles all current functionality (play, pause, seek, loop, stop).
3. **Example apps migrate last.** The SDL and Avalonia examples switch to the new
   API in Phase 6, validating the full stack.
4. **Tests at every phase.** Each phase adds tests for its deliverables. No phase
   ships without validation.

---

## What This Eliminates

| Current complexity | Eliminated by |
|--------------------|---------------|
| `PlaybackStateMachine` (flat `HashSet` transitions) | Stateless HSM with guards and hierarchy |
| Manual `ThrowIfNotIn()` scattered across methods | `CanFire()` + `Result.Fail` in dispatch loop |
| `Interlocked.CompareExchange` for state transitions | Single-threaded dispatch loop |
| `skipClockAndSinkInit` parameter | Workers don't restart on pause/resume (ADR-0022) |
| `ResetPacketQueue()` calls | Queues never complete except at shutdown |
| `isResume` branch detection | No distinction between first play and resume at worker level |
| Separate `RestartForLoopAsync` | Loop = seek to zero (same code path) |
| Unbounded frame accumulation in channel | Pool-based allocation with backpressure |
| `IVideoFramePresenter` (no pool, no backpressure) | `IVideoSink` with owned `IFramePool` |
