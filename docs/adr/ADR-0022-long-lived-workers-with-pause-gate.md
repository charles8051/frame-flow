# ADR-0022: Long-Lived Workers with Pause Gate

**Status:** Proposed. Partially superseded by ADR-0036 on the decode
side: the gate/barrier/cycle ceremony described here remains how
*playback* paces sink presentation, but the decode workers (demux pump,
video/audio decode loops) no longer need it — `IDecodedMediaStream`
expresses pause-as-not-pulling and seek-as-epoch-cancel using standard
channel backpressure and `SemaphoreSlim` serialization.
**Date:** 2026-04-05
**Supersedes:** None (refines ADR-0009, ADR-0020, ADR-0021)
**Related:** ADR-0003 (audio-master sync policy), ADR-0005 (native resource ownership), ADR-0009 (threading and concurrency model), ADR-0012 (memory management for decoded frames), ADR-0013 (cancellation token propagation), ADR-0020 (lifecycle decoupled from processing logic), ADR-0021 (looped playback strategy), ADR-0036 (decode/playback decoupling — lifts decode workers out of this ADR's scope)

## Context

The current `PlaybackSession` implements every control operation — pause, resume, seek, loop restart — through the same tear-down-and-rebuild cycle:

```
StopWorkersAsync()   → cancel CTS, await demux pump, await video/audio tasks, dispose CTS
StartWorkersAsync()  → new CTS, new channel, maybe activate sink, maybe start clock, spawn tasks
```

This approach has been the direct cause of every major playback bug encountered so far:

| Bug | How tear-down-and-rebuild caused it |
|-----|-------------------------------------|
| Decoder queues permanently closed after pause | `CompletePacketQueue` in the pump's finally block is terminal — queue cannot accept new packets on resume |
| Audio clock reset to zero on resume | `StartWorkersAsync` called `ActivateAsync` (resets hardware counters), needed `ResumeAsync` instead |
| A/V sync leap after audio pre-buffer | New workers start racing before clock and audio sink are synchronized |
| Loop restart sync drift | Needed deactivate-then-activate rather than just activate |

Each bug was fixed with a special-case branch: `skipClockAndSinkInit`, `isResume` detection via `_clock.IsPaused`, `ResetPacketQueue()` calls. These are symptom patches that grow the surface area for future bugs.

The `skipClockAndSinkInit` boolean parameter on `StartWorkersAsync` is the clearest signal: it means the method does too many things and its callers need intimate knowledge of which subset of initialization applies to their particular control operation.

### The problem compounds with seeking

Seeking requires the same stop-flush-reposition-restart cycle, but with additional edge cases:

- Seek while paused (don't restart workers afterward)
- Seek while playing (restart workers afterward)
- Seek during a seek (must serialize or reject)
- Seek near EOF with looping enabled (may trigger loop)
- Rapid sequential seeks (must debounce or serialize)

Each of these becomes another special-case branch in the tear-down-and-rebuild model. The combinatorial interaction between seek, pause, resume, and loop makes the current approach unsustainable.

### What the current architecture looks like

`PlaybackSession` is a ~1000-line class with 7+ interleaved responsibilities:

1. Public API and state machine
2. Worker lifecycle (create/destroy CTS, spawn/await tasks)
3. Channel management (create/flush video frame channels)
4. Clock management (start/stop/pause/resume/seek)
5. Audio sink lifecycle (activate/deactivate/pause/resume)
6. A/V synchronization (sync delay computation in present loop)
7. Loop orchestration (EOF detection, restart sequencing)

Every control operation must carefully thread through all seven concerns in exactly the right order with exactly the right subset of initialization. There is no structural enforcement — correctness depends on the developer remembering which steps apply to which operation.

## Decision

### Workers are long-lived and controlled through signals, not lifecycle

Worker tasks (demux pump, video decode, video present, audio decode/write) are created once when playback starts and destroyed only when the session stops or disposes. Control operations (pause, resume, seek, loop) communicate with workers through an **async pause gate** rather than cancelling and respawning them.

### The pause gate primitive

An `AsyncManualResetEvent` (or equivalent) serves as the central coordination primitive:

- **Set** (open): workers run normally
- **Reset** (closed): workers block at their next gate check

Each worker loop checks the gate once per iteration:

```csharp
while (!shutdownToken.IsCancellationRequested)
{
    await _pauseGate.WaitAsync(shutdownToken);

    // Normal work: read packet, decode frame, present, etc.
}
```

The `shutdownToken` is cancelled only by `StopAsync` and `DisposeAsync` — the only operations that actually destroy workers. Pause, resume, and seek never touch the CTS.

### Two kinds of cancellation

| Signal | Meaning | Used by |
|--------|---------|---------|
| Pause gate (closed) | Suspend processing, keep workers alive | Pause, Seek, Loop |
| Shutdown CTS (cancelled) | Terminate workers permanently | Stop, Dispose |

This replaces the current model where a single CTS serves both purposes, forcing workers to be destroyed and recreated for every control operation.

### How each operation changes

#### Pause

```
Current:
  1. Cancel CTS, await all workers     ← workers destroyed
  2. Pause clock
  3. Pause audio sink
  4. Flush video frame channel         ← dispose queued frames

Proposed:
  1. Close pause gate                  ← workers block at next iteration
  2. Await worker barrier              ← confirm all workers have paused
  3. Pause clock
  4. Pause audio sink
  // Frames stay in channel — no flush needed, workers are just paused
```

#### Resume

```
Current:
  1. ResetPacketQueue on decoders      ← because queues were completed
  2. Resume clock
  3. Resume audio sink
  4. Create new CTS, channel, tasks    ← workers rebuilt from scratch

Proposed:
  1. Resume clock
  2. Resume audio sink
  3. Open pause gate                   ← workers unblock and continue
  // No queue reset — queues were never completed
```

#### Seek

```
Current:
  1. Cancel CTS, await all workers     ← workers destroyed
  2. Flush video frame channel
  3. Seek demux
  4. Seek clock
  5. Deactivate audio sink
  6. Create new CTS, channel, tasks    ← workers rebuilt

Proposed:
  1. Close pause gate                  ← workers pause in place
  2. Await worker barrier              ← confirm all workers have paused
  3. Drain decoder input queues        ← discard pending packets
  4. Drain video frame channel         ← dispose pending frames (ADR-0012)
  5. Flush decoder codec buffers       ← discard stale internal frames
  6. Seek demux
  7. Seek clock
  8. Reset audio sink position
  9. Open pause gate                   ← workers resume from new position
  // Workers are still alive — no teardown or rebuild
```

#### Loop restart

Loop restart becomes identical to seek-to-zero. No separate `RestartForLoopAsync` method is needed.

#### Stop / Dispose

These are the only operations that destroy workers:

```
  1. Cancel shutdown CTS               ← workers exit their loops
  2. Await all worker tasks
  3. Flush channels
  4. Dispose resources (reverse creation order per ADR-0005)
```

### Flush coordination via worker barrier

Seeking and other operations that modify pipeline state require a **barrier**: all workers must be confirmed paused before flushing begins. Without this guarantee, a worker could be mid-iteration (holding a packet, writing to a channel) during the flush, causing data races or resource leaks.

The barrier is a lightweight synchronization point:

1. Close the pause gate
2. Each worker, upon reaching its gate check, signals the barrier
3. The controller awaits all worker signals
4. Now safe to flush — no worker is mid-iteration
5. After flush/seek, open the gate — workers resume

This can be implemented with a `CountdownEvent`, a set of `TaskCompletionSource` instances, or a simple counter with a `SemaphoreSlim`. The exact primitive is an implementation detail.

### Structural decomposition

`PlaybackSession` is decomposed into focused components:

```
PlaybackSession             (state machine + public API surface)
 |
 +-- PipelineController     (pause gate, flush coordination, worker barrier)
 |    +-- DemuxPumpWorker        (long-lived, gate-aware)
 |    +-- VideoDecodeWorker      (long-lived, gate-aware)
 |    +-- VideoPresentWorker     (long-lived, gate-aware)
 |    +-- AudioDecodeWriteWorker (long-lived, gate-aware)
 |
 +-- PlaybackClock          (unchanged)
 +-- ISyncStrategy          (unchanged)
```

- **PlaybackSession** owns the state machine and public API. It delegates all worker coordination to `PipelineController`. Target: ~200 lines.
- **PipelineController** owns the pause gate, worker barrier, flush sequences, and the shutdown CTS. It exposes `PauseAsync`, `ResumeAsync`, `FlushAndSeekAsync`, and `ShutdownAsync`.
- **Worker classes** are small, focused loops. Each has a `RunAsync(AsyncManualResetEvent gate, CancellationToken shutdownToken)` method.

### What this eliminates

| Current complexity | Eliminated by |
|--------------------|---------------|
| `skipClockAndSinkInit` parameter | Workers don't restart on pause/resume |
| `ResetPacketQueue()` on decoders | Queues never complete except at shutdown |
| `isResume` branch in `PlayAsync` | No distinction between first play and resume needed at worker level |
| Separate `RestartForLoopAsync` method | Loop = seek to zero (same code path as any seek) |
| Fire-and-forget `WatchForEndOfStreamAsync` | EOF signaled through the pipeline, handled by controller |
| `_demuxPumpTask`, `_videoTask`, `_audioTask` churn | Tasks created once, stored once |
| Careful ordering of CTS cancel vs. task await | Single shutdown CTS, cancelled only at end of session |

### What this enables

- **Robust seeking**: seek is a pipeline operation (gate-close, flush, reposition, gate-open), not a lifecycle operation (destroy, rebuild). New seek edge cases don't require new special-case branches.
- **Rapid seek / scrubbing**: sequential seeks naturally serialize through the controller. Workers pause once, multiple repositions happen, workers resume once.
- **Frame-accurate seeking**: the flush + barrier pattern guarantees no stale frames survive a seek.
- **Simpler testability**: workers can be tested with a fake gate and a fake shutdown token. No need to test complex startup/shutdown sequencing.

## Consequences

### Positive

- Control operations become combinations of the same two primitives (gate close/open) rather than unique teardown/rebuild sequences.
- Eliminates the class of bugs caused by asymmetric initialization — there is no initialization to get wrong on resume.
- Each worker is a small, testable unit with a single loop and a single exit condition.
- `PlaybackSession` shrinks from ~1000 lines to ~200, making the state machine auditable.
- Seeking, rapid scrubbing, and loop restart all share the same code path.

### Negative

- Workers must be written to be gate-aware — each loop body must include a gate check and properly handle the shutdown token alongside the gate.
- The barrier introduces a synchronization point that could deadlock if a worker fails to reach its gate check. Bounded timeouts on the barrier mitigate this (consistent with ADR-0013's bounded disposal timeout).
- The decomposition adds more types to `FrameFlow.Playback`. This is justified by the reduction in per-type complexity.

### Neutral

- ADR-0009's guidance on `Channel<T>` for backpressure is unchanged. Channels remain the queue primitive between workers.
- ADR-0012's frame ownership model is unchanged. Frames are still disposed exactly once. The flush path disposes frames during seek; the present path disposes frames during normal playback.
- ADR-0003's audio-master sync policy is unchanged. The sync strategy still computes per-frame delay in the present worker.
- ADR-0013's cancellation semantics are preserved. Consumer tokens on public methods mean "abort this operation." The shutdown CTS means "destroy the session."
- ADR-0021's looping semantics are preserved. Loop configuration, iteration counting, and `SetLoop` runtime control are unchanged. Only the restart mechanism changes (from dedicated method to seek-to-zero).

## Migration Path

This does not require a big-bang rewrite. A phased approach:

1. **Introduce `AsyncManualResetEvent`** and add gate checks to existing worker loops alongside the current CTS-based control. Both mechanisms coexist temporarily.
2. **Convert pause/resume** to use the gate. Remove `skipClockAndSinkInit`, `ResetPacketQueue` calls, and the `isResume` branch.
3. **Convert seek** to use gate + flush + barrier instead of stop/start workers.
4. **Convert loop** restart to call the same seek-to-zero path used by seek.
5. **Extract `PipelineController`** and worker classes from `PlaybackSession`.
6. **Remove old infrastructure**: `StopWorkersAsync`/`StartWorkersAsync` as general-purpose methods, per-operation CTS churn, fire-and-forget watcher task.

Each phase is independently testable and shippable. The system is never in a half-migrated state that breaks existing functionality.

## Adjacent Recommendations

These are not part of the core decision but are worth considering alongside this change:

### Formalized state machine

Replace `lock(_stateLock)` + manual `ThrowIfNotIn` with a small state machine type that encodes valid transitions:

```csharp
if (!_stateMachine.TryTransition(PlaybackState.Seeking))
    throw new InvalidOperationException(...);
```

This makes invalid transitions structurally impossible rather than relying on guard clauses scattered across every public method.

### Simplified audio sink contract

The current four-method contract (`ActivateAsync` / `DeactivateAsync` / `PauseAsync` / `ResumeAsync`) creates subtle bugs when the wrong method is called (Activate when Resume was intended). Consider collapsing to:

- `PrepareAsync(TimeSpan position)` — initialize hardware or reposition to a timestamp
- `SuspendAsync()` / `ResumeAsync()` — pause/resume without losing position

This removes the distinction that caused the audio clock reset bug.

### Observable state changes

Consumers currently poll `State`. An event or callback for state transitions would let UI layers react without polling — important for seek and loop scenarios where state changes rapidly.
