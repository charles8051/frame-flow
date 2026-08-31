# ADR-0021: Looped Playback Strategy

**Status:** Proposed
**Date:** 2026-04-04
**Supersedes:** None
**Related:** ADR-0003 (audio-master sync policy), ADR-0009 (threading and concurrency model), ADR-0012 (memory management for decoded frames), ADR-0020 (lifecycle decoupled from processing logic)

## Context

FrameFlow needs a strategy for looped (repeating) playback. Common use cases include background video walls, music players with repeat, and demo loops.

The question is where looping should be orchestrated. Three candidate layers exist:

1. **Presentation layer** (Avalonia, SDL) — each presenter detects end-of-stream and restarts playback.
2. **Worker loops** (decode/demux pumps) — loops internally seek back to the beginning when they exhaust the stream.
3. **PlaybackSession** — the headless orchestrator detects end-of-stream and coordinates a restart.

### Why not the presentation layer

Placing loop logic in presenters forces every adapter to reimplement the same restart sequence. Headless consumers (frame dumpers, transcoding pipelines, automated tests) would not benefit at all. This violates the project's core separation between playback orchestration and output adapters.

### Why not the worker loops

Worker loops are processing logic. Injecting restart decisions into decode or demux pumps couples lifecycle control (when to restart) with data processing (how to decode). ADR-0020 explicitly forbids this: lifecycle decisions must be separated from processing logic. Workers should remain stateless consumers of a packet/frame stream that run until cancelled or exhausted.

### Why the PlaybackSession

`PlaybackSession` already owns the end-of-stream detection path (`WatchForEndOfStreamAsync`) and the restart machinery used by seeking (`SeekAsync` flushes queues, resets the clock, and calls `StartWorkersAsync`). Looping is architecturally identical to an automatic seek-to-zero triggered when the stream ends naturally. The session is the only component with visibility into both the lifecycle state machine and the worker task graph.

## Decision

Looped playback will be orchestrated by `PlaybackSession` at the lifecycle level, reusing the existing seek-and-restart pattern.

### 1. Configuration via options

`FrameFlowPlaybackOptions` gains two properties:

```csharp
public sealed class FrameFlowPlaybackOptions
{
    public bool AutoPlay { get; set; }
    public bool UseAudioAsMasterClock { get; set; } = true;

    /// <summary>
    /// When true, the session automatically restarts from the beginning
    /// when end-of-stream is reached instead of transitioning to Ended.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    /// Maximum number of loop iterations. A value of zero or negative
    /// means unlimited. Ignored when <see cref="Loop"/> is false.
    /// </summary>
    public int MaxLoopCount { get; set; }
}
```

This follows the established `IOptions<T>` pattern and is configurable through DI, `appsettings.json`, or the fluent builder.

### 2. Orchestration in WatchForEndOfStreamAsync

When all active worker tasks complete normally and `Loop` is enabled:

1. Verify the loop count has not been exceeded.
2. Flush all pending frames from video and audio channels (required by ADR-0012 to prevent stale frame leaks).
3. Seek the demux session to `TimeSpan.Zero`.
4. Reset the playback clock via `_clock.Seek(TimeSpan.Zero)`.
5. Deactivate and reactivate the audio sink to reset device buffers.
6. Call `StartWorkersAsync` to launch fresh worker tasks.
7. Increment the loop iteration counter.
8. The session remains in `Playing` state throughout — it never transitions to `Ended`.

When `Loop` is false, or the maximum loop count is reached, the session transitions to `Ended` as it does today.

### 3. Observable loop state

`PlaybackSession` exposes:

```csharp
/// <summary>
/// The current loop iteration (zero-based). Resets when the session is stopped or disposed.
/// </summary>
public int CurrentLoopIteration { get; }
```

Presentation layers can read this to update UI (e.g., "Loop 3 of 5") or emit analytics. The `StateChanged` event (or equivalent notification mechanism) fires when a new iteration begins, so consumers do not need to poll.

### 4. Runtime loop control via SetLoop

`IPlaybackSession` exposes a synchronous method for toggling loop behavior at runtime:

```csharp
public interface IPlaybackSession : IAsyncDisposable
{
    // ... existing members ...

    bool IsLooping { get; }
    int CurrentLoopIteration { get; }
    void SetLoop(bool enabled, int maxCount = 0);
}
```

**Why synchronous, not async.** `SetLoop` mutates two fields under the existing `_stateLock`. There is no I/O, no device call, no state machine transition. An async signature would mislead consumers into thinking the method awaits completion of the current loop iteration or performs meaningful work. It is a configuration mutation, not an operation.

**Thread safety.** The only consumer of the loop fields is `WatchForEndOfStreamAsync`, which reads them after all worker tasks have completed. There is no race with active processing — by the time the watcher checks `IsLooping`, the decode and present loops have already exited. The lock provides cross-thread visibility, not coordination.

**Edge case: toggling mid-restart.** If `SetLoop(false)` is called while `WatchForEndOfStreamAsync` is executing its restart sequence (flush → seek → restart), the decision to loop was already made. The current iteration completes, and the next end-of-stream check sees `IsLooping == false` and transitions to `Ended`. This is correct behavior — the change takes effect at the next decision point, not retroactively.

**Edge case: SetLoop after Ended.** Setting `IsLooping = true` after the session has already reached `Ended` has no retroactive effect. The consumer must seek and play again. `SetLoop` controls future end-of-stream behavior, not session resurrection. This boundary is documented on the method.

**Initialization.** The `_loop` and `_maxLoopCount` fields are initialized from `FrameFlowPlaybackOptions` in the constructor, so DI/options configuration still works as the default. `SetLoop` overrides the configured values at runtime.

### 5. State machine implications

No new `PlaybackState` values are needed. During a loop restart the session remains `Playing`. The restart sequence is fast (flush + seek + restart workers) and does not warrant a transient state because no external coordination depends on observing the restart in progress. If future requirements demand it, a transient `Restarting` state can be introduced without breaking the existing state machine.

## Consequences

### Positive

- Looping works identically for all consumers: Avalonia, SDL, headless, and tests.
- Reuses proven seek-and-restart machinery — minimal new code.
- Follows ADR-0020: lifecycle orchestration stays in the session, workers remain stateless.
- Configuration-driven: works with DI and host patterns out of the box.
- Testable with fake clocks and presenters per ADR-0007.

### Negative

- A small gap between loop iterations is unavoidable: the flush/seek/restart cycle takes nonzero time. For gapless audio looping (e.g., seamless music loops), a more sophisticated pre-buffering strategy would be needed in a future phase.
- Adding properties to `FrameFlowPlaybackOptions` and `SetLoop` to `IPlaybackSession` slightly increases the configuration and API surface.

### Neutral

- Presentation layers remain unchanged — they continue consuming frames without awareness of looping.
- The `Ended` state retains its current meaning (stream truly finished, no more playback).

## Alternatives Considered

### Orchestrate looping in each presenter

Rejected. Duplicates restart logic across every adapter, breaks headless playback, and violates the core/adapter separation.

### Embed loop logic in the demux pump

Rejected. Couples lifecycle decisions with processing logic, violating ADR-0020. The demux session should not decide whether to restart — it should be told to seek.

### Add a PlaybackState.Looping value

Rejected for now. The restart is fast and no consumer needs to observe the transient mid-restart state. A new state value would add complexity to every state-machine consumer. Can be revisited if the restart gap becomes observable and consumers need to react.

### Pre-buffer the next iteration for gapless playback

Deferred. Gapless looping requires reading ahead into the next iteration while the current one is still playing, which significantly complicates the buffering model. Not justified for v1 where a small inter-loop gap is acceptable.

## Compliance Checklist

When implementing looped playback, verify:

- [ ] Loop restart logic lives in `PlaybackSession`, not in workers, decoders, or presenters
- [ ] All pending frames are flushed and disposed before restarting (ADR-0012)
- [ ] The audio sink is deactivated and reactivated across the restart boundary
- [ ] The playback clock is reset to zero before workers restart
- [ ] `CurrentLoopIteration` is accurate and resets on stop/dispose
- [ ] Looping is disabled by default (no behavioral change for existing consumers)
- [ ] The session transitions to `Ended` when max loop count is reached
- [ ] Workers are not aware of looping — they run a single linear pass per iteration
- [ ] `SetLoop` is synchronous — no async, no I/O, lock-guarded field mutation only
- [ ] `SetLoop` after `Ended` has no retroactive effect (documented on the method)
- [ ] `SetLoop(false)` mid-restart takes effect at the next iteration boundary, not immediately
