# ADR-0058: One shared OpenAL device and context per process

## Status

Accepted.

**Date:** 2026-06-05
**Related:**
- ADR-0003 (audio-master synchronization policy — the audio sink is the master clock)
- ADR-0057 (pull-based master clock — the clock is read on demand from the OpenAL sample counter)
- ADR-0044 (sink ownership and disposal — idempotent `DisposeAsync`, DI owns the sink)
- ADR-0035 (master-clock interface split)

## Context

`OpenAlAudioSink` is both the audio output and, per ADR-0003, the master clock
that paces video. Its clock read (`GetPlaybackTime`) and its buffer plumbing are
implemented with OpenAL calls against an OpenAL device and context that the sink
opened for itself in `ActivateAsync`:

```csharp
_device = _alc.OpenDevice(string.Empty);
_context = _alc.CreateContext(_device, null);
_alc.MakeContextCurrent(_context);     // <-- process-global state
_source = _al.GenSource();
```

Every `al*` call — `alGetSourcei(SampleOffset)` for the clock,
`alSourceQueueBuffers` / `alSourceUnqueueBuffers` / `alSourcePlay` for the buffer
pump — operates on whatever context is **currently made current**. And
`alcMakeContextCurrent` sets a *process-global* current context, not a per-thread
one. The OpenAL Soft `ALC_EXT_thread_local_context` spec is explicit about the
base behaviour: "`alcMakeContextCurrent` can be used to set a process-wide
context that can affect all threads of the OS process," and "there is only ever
one current context for any one process." The spec even warns: "depending on the
scheduler, the context (and thus the listener) may change while you're setting
the listener/source attributes ... you'll have to do your own mutex to make sure
the context stays as you expect."

### The bug

With **two** `OpenAlAudioSink` instances active in the same process, whichever
activated last calls `MakeContextCurrent` last and owns the global current
context. The other sink's `al*` calls then target the wrong context. Two failure
modes compound:

1. **Source-name collision.** Source names are numbered *per context*, starting
   at 1. Sink A's source is name `1` in context A; sink B's source is *also* name
   `1` in context B. When context B is current, sink A's
   `alGetSourcei(1, SampleOffset)` reads sink B's source, and sink A's
   `RecycleProcessedBuffers` calls `alSourceUnqueueBuffers(1, ...)` on sink B's
   source — stealing sink B's processed buffers.

2. **Master-clock corruption.** Because the sample-counter clock is the video
   pacer's master (ADR-0003, `SubstrateSession`: `if (audioSink is IClockSource)
   use it`), the cross-wired `SampleOffset` read makes the clock stall or jump.
   `PaceUntil` then holds video frames for whole seconds and burst-releases them.

This was observed in a downstream kiosk that ran two muted `OpenAlAudioSink`s at
once (each used only as a master clock). One `video-pace` operator logged two
independent PTS timelines interleaved ~50 ms apart (e.g. `pts=10.3s`, then
`pts=42.5s`, then back to `pts=10.7s`), with `PaceUntil` long-waits up to
~3.3 s, 151 underruns, and 1301 sink reactivations in one session. The failure
was identical on the CPU and GPU video presenters, which locates it upstream of
the presenter — in the clock — and rules out the zero-copy path and the
pull-based-clock change (ADR-0057).

The per-instance `_stateLock` does not help: it serialises each sink's own
operations, but two sinks have two different locks and there is nothing
serialising the *process-global current context* between them.

## Decision

Adopt the canonical OpenAL multi-source model: **one process-wide device, one
context, made current exactly once, with each sink owning its own source and
buffer pool inside that single context.** When the current context is never
changed after creation, there is nothing left to clobber, and every
`SampleOffset` read and buffer-queue op is valid regardless of which sink or
thread issues it. OpenAL "is already designed for one context being used on
multiple threads at once" (the same spec), so this is the intended shape, not a
workaround.

### `SharedOpenAlContext` (new, internal)

A reference-counted process singleton in `FrameFlow.Audio.OpenAL`:

- Lazily opens the device (`alcOpenDevice`), creates one context
  (`alcCreateContext`), and makes it current **once** on the first acquire.
- Exposes the shared `AL` API for sinks to drive their own sources/buffers.
- `Acquire()` returns a `SharedOpenAlContextLease` (or `null` when no device can
  be opened, so the caller stays inert — mirroring the prior open-failure path).
- The last lease disposed tears the context down: `MakeContextCurrent(null)`,
  `DestroyContext`, `CloseDevice`, dispose the API wrappers.
- A single process-global lock guards device/context lifecycle and the refcount.
  It is **not** taken on the audio/clock hot path. Lock ordering is per-sink lock
  → global gate (the gate is always the leaf), so no sink lock is ever acquired
  while the gate is held.

### `OpenAlAudioSink` changes

- `ActivateAsync` acquires the shared lease instead of opening its own
  device/context. It still `GenSource()`s and generates its own buffer pool
  inside the shared context. The per-sink `MakeContextCurrent` call is gone.
- `DisposeAsync` (idempotent per ADR-0044) deletes the sink's own source and
  buffers while the context is still current, then releases the lease. Only the
  last release tears down device/context.
- Everything per-source is unchanged: per-source gain (mute/volume), the
  loop-restart reactivation drain/rewind logic, the staging/coalescing buffer,
  `_processedSamplesPerChannel`, `_stateLock`, and the pull-based
  `WaitUntilAsync` (ADR-0057). Reactivation reuses the one lease — the lease is
  acquired once at first activation and released once at disposal, never per
  Activate/Deactivate cycle.

### Threading

OpenAL Soft is safe for concurrent operations on **distinct** sources within one
shared context — that is the documented design. Each sink already guards its own
source state with `_stateLock`, and distinct sinks touch distinct sources, so no
process-global lock is needed on the hot path. This matters: the master clock is
read ~30-60 times per second per player; putting a global lock or a
context-switch syscall there is exactly what we are avoiding. The shared gate is
touched only at activation and disposal.

## Consequences

### Positive

- **The clobber is impossible by construction.** The current context is set once
  and never changed, so no sink can retarget another sink's `al*` calls. Multiple
  concurrent real-audio players (or multiple muted clock-only sinks) now work.
- **Fewer native resources.** N sinks share one device and one context instead of
  opening N of each.
- **Cleaner failure handling.** When no device is available the sink leaves `_al`
  null and every existing `_al is null` guard makes it a clean no-op, removing a
  latent path where the old code left `_al` non-null after a failed `OpenDevice`.
- **Disposal stays idempotent (ADR-0044).** The lease releases exactly once
  (interlocked), and the last release tears down — no double-free, no leak.

### Negative / trade-offs

- **A new process-global singleton.** It is internal to `FrameFlow.Audio.OpenAL`
  and reference-counted with explicit teardown, but it is shared mutable
  process state. The lock discipline (gate is the leaf lock; never on the hot
  path) is the mitigation and is documented on the type.
- **All sinks share one output device.** Selecting different physical output
  devices per sink is not possible with a single shared device. No consumer needs
  this today; if it ever does, the model becomes one shared context *per device*,
  keyed by device name — a natural extension of the refcount keying, not a
  redesign.

### Neutral

- A/V sync policy (ADR-0003) and the pull-based clock (ADR-0057) are unchanged.
  Only *where the device/context lives* changed; the sample-counter read and the
  pacing loop are identical.

## Alternatives considered

### A. Shared device + context, many sources (accepted)

The canonical OpenAL model. Chosen because it removes the clobber by
construction and matches how OpenAL is designed to be used across threads.

### B. Thread-local context via `alcSetThreadContext` (`ALC_EXT_thread_local_context`)

Give each thread its own current context so the global one is never contended.
Rejected: fragile, because a single sink's operations span multiple threads — the
audio worker (`PresentAsync`), the video worker (the per-frame clock read), and
the controller lifecycle thread (Activate/Pause/Resume/Dispose). Every one of
those entry points would have to set the thread context before any `al*` call and
would have to know *which* sink's context to set. That is far more invasive and
error-prone than removing the per-sink context entirely. It also leaves N
devices/contexts open for no benefit.

### C. Keep per-sink contexts; add a global lock that re-asserts `MakeContextCurrent` per critical section

Wrap every `al*`-touching critical section in a process-global lock that calls
`MakeContextCurrent(thisSink._context)` on entry. Correct, but it serialises all
OpenAL work across every sink and puts a `MakeContextCurrent` (a context-switch)
on the master-clock read path that runs tens of times per second per player. The
spec's "do your own mutex" advice describes exactly this, and it is strictly
worse than having one context that never needs switching.

## Validation

- **Behavioural (real device, opt-in `RequiresAudioDeviceFact`).** Two new tests
  in `OpenAlAudioSinkMultiInstanceTests` drive two concurrent sinks and assert
  each master clock advances independently at ~real-time. Before the fix the
  victim sink's clock crawled (~0.17-0.47x real-time) or froze when a second sink
  activated; after the fix both sinks read identical clocks (~0.85-0.89x,
  in lockstep). Confirmed fail-before / pass-after on a machine with a real
  OpenAL output device.
- **Structural (deterministic given a device).** `SharedOpenAlContextTests`
  asserts the refcount accounting directly via internal counters: two sinks open
  exactly one device, the refcount tracks active sinks, disposing one keeps the
  context alive for the other, and the last disposal tears it down. Reactivation
  reuses the single lease. These are counting assertions with no playback timing.
- All device-activating test classes are serialised under one xUnit collection so
  the shared device and the process-global counters are not perturbed by parallel
  classes.

## References

- OpenAL Soft `ALC_EXT_thread_local_context` extension spec — process-wide vs
  thread-specific current context, and "one context being used on multiple
  threads at once."
- `src/FrameFlow.Audio.OpenAL/SharedOpenAlContext.cs` — the shared device/context.
- `src/FrameFlow.Audio.OpenAL/OpenAlAudioSink.cs` — acquires the lease; owns only
  its source and buffer pool.
- `src/FrameFlow.Playback/SubstrateSession.cs` — selects the audio sink as the
  master `IClockSource` when present.
