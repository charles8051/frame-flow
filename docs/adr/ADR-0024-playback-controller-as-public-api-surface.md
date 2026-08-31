# ADR-0024: PlaybackController as Public API Surface

**Status:** Proposed
**Date:** 2026-04-07
**Supersedes:** None (refines current PlaybackSession public surface)
**Related:** ADR-0008 (result types), ADR-0020 (lifecycle decoupled from processing), ADR-0022 (long-lived workers with pause gate), ADR-0023 (hierarchical state machine with channel dispatch)

## Context

### Current architecture: two layers

FrameFlow currently exposes `IPlaybackSession` as the public API. Internally, `PlaybackSession` owns both state management and pipeline orchestration. ADR-0022 introduced `PipelineController` as an internal coordinator for worker lifecycle, giving a two-layer model:

```
PlaybackSession        (public API + state machine + session lifecycle)
 └── PipelineController (pause gate, worker barrier, flush coordination)
      ├── DemuxPumpWorker
      ├── VideoDecodeWorker
      ├── VideoPresentWorker
      └── AudioDecodeWriteWorker
```

This works but puts too many responsibilities in `PlaybackSession`: it manages state transitions, creates/disposes per-media-item resources, coordinates seeking, handles repeat logic, and exposes the public API. As ADR-0023 adds a hierarchical state machine and command channel, `PlaybackSession` would grow further.

### The three-layer model from production players

Production players (ExoPlayer, AVPlayer, VLC) consistently separate three concerns:

| Layer | Responsibility | Lifetime |
|-------|---------------|----------|
| **Controller** | State machines, command dispatch, public API, event distribution | Application lifetime |
| **Session** | Per-media-item resources: demuxer, decoders, clock, queues | Media item lifetime |
| **Workers** | Data processing loops: demux, decode, render | Controlled by session |

The controller decides **what** state we're in. The session decides **how** to run the pipeline. Workers **do** the byte-level processing.

### Why this matters for FrameFlow

With ADR-0023's state machine and command dispatch, the controller layer becomes the natural owner of:

- The `StateMachine<PlaybackState, PlaybackTrigger>` and all orthogonal region machines
- The `Channel<IPlayerCommand>` command channel and dispatch loop
- The public API methods (thin wrappers around `PostAndWaitAsync`)
- Observable state properties and event subjects
- The `playWhenReady` flag and composite `IsPlaying` computation

The session layer becomes the natural owner of:

- Per-media-item resources (demuxer, decoders, clock, channels)
- `PipelineController` and worker task lifecycle
- Seek orchestration (flush, reposition, resume)
- Buffer threshold monitoring

These concerns have **different lifetimes**: the controller persists across media items, the session is created and destroyed per item. The current `PlaybackSession` conflates both lifetimes.

## Decision

### 1. Introduce PlaybackController as the top-level public API

A new `PlaybackController` class will be the consumer-facing entry point. It owns the state machines, command channel, dispatch loop, and event subjects described in ADR-0023.

```
PlaybackController          (public API, state machines, command dispatch)
 │                           Lifetime: application / DI scope
 │
 └── PlaybackSession         (per-media-item resources, pipeline coordination)
      │                       Lifetime: created on Load, disposed on Stop/Load-new
      │
      └── PipelineController  (pause gate, worker barrier, flush coordination)
           ├── Workers...      Lifetime: first Play → Stop/Dispose
```

### 2. PlaybackSession becomes internal

`PlaybackSession` is demoted from public API to an internal implementation detail of `PlaybackController`. It no longer exposes transport controls directly. Instead, the controller's state machine entry/exit actions call session lifecycle methods:

- `OnEntry(Initializing)` → create session, call `session.InitializeAsync()`
- `OnEntry(InitialBuffering)` → call `session.BufferToThresholdAsync()`
- `OnEntry(Playing)` → call `session.StartRenderers()` or `session.Resume()`
- `OnEntry(Paused)` → call `session.Pause()`
- `OnEntry(Stopped)` → dispose session, set to null
- Seek trigger → call `session.SeekAsync(position)`

Session lifecycle methods fire callbacks (`OnBufferThresholdMet`, `OnLastFrameRendered`, `OnFatalError`) that the controller routes through its command channel via `PostInternalAsync`.

### 3. IPlaybackController replaces IPlaybackSession as the public interface

```csharp
public interface IPlaybackController : IAsyncDisposable
{
    // ── Transport controls ─────────────────────────────
    Task<Result> LoadAsync(IMediaSource source, CancellationToken ct = default);
    Task<Result> PlayAsync(CancellationToken ct = default);
    Task<Result> PauseAsync(CancellationToken ct = default);
    Task<Result> StopAsync(CancellationToken ct = default);
    Task<Result<TimeSpan>> SeekAsync(TimeSpan position, CancellationToken ct = default);

    // ── Repeat ─────────────────────────────────────────
    Task<Result> SetRepeatModeAsync(RepeatMode mode);

    // ── Observable state ───────────────────────────────
    PlaybackState State { get; }
    SeekState SeekingState { get; }
    RepeatMode RepeatMode { get; }
    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    MediaInfo? MediaInfo { get; }

    // ── Events ─────────────────────────────────────────
    IObservable<StateTransition<PlaybackState>> PlaybackStateChanged { get; }
    IObservable<LoopRestarted> LoopRestarted { get; }
    IObservable<PlaybackError> ErrorOccurred { get; }
}
```

All methods return `Task<Result>` or `Task<Result<T>>` per ADR-0008. Invalid transitions return `Result.Fail(InvalidOperation)` rather than throwing.

### 4. Factory updates

`IPlaybackControllerFactory` replaces `IPlaybackSessionFactory`:

```csharp
public interface IPlaybackControllerFactory
{
    IPlaybackController CreateController();
}
```

The DI extension method becomes `AddFrameFlowPlayback()` registering the factory and its dependencies.

### 5. Session never overlaps

Loading new content disposes the old session before creating a new one. The controller enforces this: the `OnEntry(Initializing)` action disposes any existing session first. Sessions are never concurrent within a single controller.

## Pushback: Is the controller layer premature?

**Argument against:** FrameFlow has a single public API consumer today. The current `PlaybackSession` with `PipelineController` internal is only two layers. Adding a third layer increases type count and indirection for a library that can barely play a file.

**Counter-argument:** The state machine and command dispatch from ADR-0023 need to live somewhere. If they live in `PlaybackSession`, that class owns both application-lifetime concerns (state machines, event subjects, the dispatch loop) and per-item-lifetime concerns (demuxer, decoders, clock). These lifetimes are fundamentally different — the state machine survives across Load calls, the demuxer does not. Forcing them into one class means either:

- The state machine is recreated per Load (wasteful, loses subscriber connections)
- Per-item resources must be nullable and carefully managed within a long-lived object (the current approach, which is the source of most bugs)

The controller/session split is not adding complexity — it is removing the complexity of managing two lifetimes in one class.

**Resolution:** Proceed with the split. The controller is small (~200 lines of state machine configuration + dispatch loop). The session shrinks to resource lifecycle. The total line count is comparable to today's single class, but the responsibilities are cleanly separated.

## Consequences

### Positive

- Application-lifetime concerns (state machines, event subjects, dispatch loop) and per-item concerns (decoders, clock, queues) have explicit, separate owners.
- Loading new content is structurally safe: dispose old session, create new session. No nullable resource juggling.
- The public API surface gets `Result`-typed returns and observable events, improving consumer ergonomics.
- State machine configuration is co-located in the controller, not scattered across session methods.

### Negative

- One more type in the public API (`IPlaybackController` instead of `IPlaybackSession`).
- Consumers using the current `IPlaybackSession` will need to migrate. Since FrameFlow is pre-release, this is acceptable.
- The controller↔session boundary adds a callback-based communication path that must be correctly wired.

### Neutral

- ADR-0022's `PipelineController` is unchanged — it remains an internal detail of `PlaybackSession`.
- ADR-0003's sync policy is unchanged — the clock still lives in the session.
- Workers are unchanged — they know nothing about the controller.
- DI registration changes shape but not substance.

## Alternatives Considered

### Keep PlaybackSession as the public API and embed the state machine

Rejected. This is the current model and the source of the lifecycle confusion described in ADR-0022's context section. Adding a state machine and command dispatch to `PlaybackSession` would make it larger and more complex, not less.

### Make PlaybackController a thin facade that delegates everything

Considered. A controller that is purely a thread-safe wrapper around session methods would be simpler but would not solve the lifetime mismatch. The state machines and event subjects must outlive individual sessions.

### Use a mediator/message bus instead of direct callbacks

Rejected as over-engineering. The controller creates the session and wires callbacks at creation time. A mediator adds indirection without solving any problem that direct delegates do not already solve at this scale.
