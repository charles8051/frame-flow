# ADR-0057: Pull-based master clock (read-on-demand, no publish ticker)

## Status

Accepted.

## Context

Video is paced against a master `IClockSource` (ADR-0003): `PaceUntil` awaits
`clockSource.WaitUntilAsync(frame.Pts)` before forwarding each frame, so video
tracks audio.

Both clock sources — `OpenAlAudioSink` (the audio-mastered clock) and
`WallClockSource` (the no-audio fallback) — already computed the *true* timeline
on demand:

- `OpenAlAudioSink.GetPlaybackTime()` = `_baseSourceTime + processedSamples / sampleRate`,
  straight off the OpenAL sample counter.
- `WallClockSource` = `_baseOffset + _stopwatch.Elapsed`, a monotonic stopwatch.

But neither *exposed* that as a pull. Instead each ran a **5 ms threadpool ticker**
(`PeriodicTimer` + `Task.Run`) that read the on-demand value and **pushed** it into a
`ClockSubject`. `WaitUntilAsync` then resolved only when that ticker `Publish`'d a value
past the target.

That coupling is fragile under CPU contention. When the threadpool can't run the 5 ms
ticker on time, the *published* clock goes stale while the *real* time source keeps
advancing. `PaceUntil` can't unblock (it's waiting on the stale published value); frames
pile up; the ticker eventually catches up and publishes a jump, releasing a **burst** of
now-late frames; the latest-wins presenter keeps only the newest and drops the rest →
**visible hitching**. The sinks even instrumented this exact failure
(`Clock TICKER STARVED … likely cause of PaceUntil freezes downstream`).

This was measured on a reference kiosk (Intel i5-7300U / HD 620). The zero-copy
display path (ADR-0016/0038) roughly halved CPU vs. the software-decode path, but opening
**Splashtop** (a heavy screen-encoder) saturated the CPU and re-exposed the hitch through
this mechanism: the GPU video engines stayed flat while the threadpool ticker starved.

The truth (sample counter / stopwatch) was never the problem — only the *publisher link*
was. Fusing the clock *value* with a *push timing mechanism* is exactly the anti-pattern
this repository's design rules warn against ("cadence is a pure state advanced by a
shell-owned clock… read on demand"; keep state, IO, and timing separate).

## Decision

Make `IClockSource` **pull-based**. The value is computed on demand from the underlying
time source; there is no publish ticker.

- `Latest` → compute on read (`GetPlaybackTime()` / `_baseOffset + _stopwatch.Elapsed`).
- `WaitUntilAsync(target)` → a **compute-delay-recheck loop**: each slice recomputes
  `remaining = target − now` from the live source and sleeps `min(remaining, cap)`, so a
  descheduled thread can never strand the caller on a stale clock. A synchronous fast path
  returns immediately when the target is already due, keeping the per-frame hot path
  allocation-free. An owned `CancellationTokenSource` releases in-flight waits on dispose.
- **Delete the 5 ms publish tickers** in `OpenAlAudioSink` and `WallClockSource` (and the
  `TICKER STARVED` instrumentation that existed to flag their starvation).

`PaceUntil` and all other `IClockSource` consumers are unchanged — they already call
`WaitUntilAsync`; it simply stops being starvable.

## Consequences

### Positive

- **Pacing is immune to publisher-thread scheduling.** The clock is always accurate
  because the time source (sample counter / stopwatch) is read when the pacer needs it.
  A late wake just reads the now-correct time and releases — no stale-clock burst.
- **Removes a per-sink 5 ms timer thread.** A CPU win that matters precisely on the
  constrained hardware that exposed the bug.
- **Matches the codebase's own principle** — the clock value is a pure function of the
  time source, read on demand; timing (when to wake) is separated from state (the value).

### Negative / trade-offs

- **Wake-up latency is now bounded by OS timer granularity** (~15 ms default on Windows
  for `Task.Delay`) instead of the 5 ms publish cadence. Mitigated two ways: (1) the
  latest-wins presenter already smooths sub-frame release jitter — it shows the freshest
  frame each render tick; (2) a high-resolution timer (`timeBeginPeriod(1)`) sharpens it
  to ~1 ms if needed. ~~The high-res timer is **deferred** as a separate, scoped change
  (its natural home is the playback session/host, not a sink), to be added only if kiosk
  validation shows residual jitter.~~

  **Resolved by [ADR-0067](ADR-0067-high-resolution-pacing-timers.md).** The residual
  jitter was not residual: the ~15 ms quantization is a hard ~34 fps ceiling on 60 fps
  content, which took #128, #145 and #152 to pin down. The deferral above is correct
  about `timeBeginPeriod`, which sets process-wide timer policy a library should not set
  for its host — but the mitigation FrameFlow actually took is
  `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, which is per-timer and carries none of that
  consequence. Both clocks now default to a provider built on it, and the host does
  nothing.
- The audio pull loop acquires `_stateLock` per pacing read (~30–50/sec). Negligible — the
  lock is held for microseconds; the "avoid the lock for high-rate callers" rationale that
  motivated the push design was over-cautious.

### Neutral

- `ClockSubject` stays in `FrameFlow.Graph` as a general utility; it's just no longer used
  by these two sources.
- A/V sync is unchanged: video still paces against the same audio clock, sampled the same
  way (frame PTS vs. audio time). Only the *delivery mechanism* of the clock changed.

## Stage 2 (built): presenter-side select-by-clock on the single-sink path

Stage 1 (above) made the clock un-starvable but left pacing *inside the graph*:
`PaceUntil` still awaited `WaitUntilAsync(frame.Pts)` **while holding the frame inside the
operator**. On the zero-copy single-sink path the graph edges are capacity-1, so that
pinned a D3D11VA decode-texture slice (`GpuVideoFrame` keeps the decode lease alive while
referenced) across the wait with no slack. A single multi-second wait drained the
FFmpeg-default hwframe pool and stalled the decoder — the confirmed
held-lease → hwframe-pool-exhaustion coupling (`NodePumps.PumpOperatorAsync` holding the
`VideoFrameRef` across the `PaceUntil` await; the lockstep camera-pool drops). This was
re-surveyed in a 2026-06-11 pacing-cadence survey (§A1) that is not published with
this repository — it is written against a downstream deployment. Stage 2 is now
built (perf item A1).

**Decision.** For the **single-sink** video path, move pacing out of the graph operator and
into the presenter:

- A small PTS-ordered frame ring (`ClockSelectBuffer`, the **pure core**: a total
  `Select(now, dropped)` value transform — no IO, clock, or locks) plus an imperative
  shell decorator (`ClockSelectVideoSink`, an `IVideoSink` wrapping the consumer's real
  sink) own the pacing. `SubstrateSession` builds one pacer per session in `InitializeAsync`
  (once the per-item master clock is final), and the graph's single video sink node targets
  the pacer instead of the raw sink.
- The graph now runs `source → gate → (configurator) → ClockSelectVideoSink`. The sink
  node's pump hands each frame to `PresentAsync`, which **enqueues and returns at once**
  (blocking async only when the ring is full, for backpressure), so the graph's
  `VideoFrameRef` is released immediately. **No decode lease is ever held inside the graph
  across a clock wait** — the held-lease coupling is gone. Frames arrive at decode rate, the
  decoder fills the pool at decode rate, and leases release promptly.
- The shell's own delivery loop does the clock wait (`WaitUntilAsync`, the same
  Stage-1 starvation-immune pull), then runs the pure `Select` at delivery time: it presents
  the freshest frame whose PTS ≤ now and **drops** any earlier still-buffered frames (their
  leases return at once) — never pinning one frame across the wait. A `maxWait` cap (mirrors
  the old `PaceUntil` cap, default 5 s) degrades a misaligned/stalled master clock to
  choppy-but-alive rather than a frozen pool.
- End-of-stream is real-time-gated: because frames arrive at decode rate, graph completion
  no longer means the clip finished *playing*. The session marks input complete
  (`SignalInputComplete`) and awaits `WaitForDrainAsync` before raising Ended, so a no-audio
  `RepeatMode.One` loop ticks once per clip-duration, not at decode speed. Seek flushes the
  ring (`Flush`) so pre-seek frames never present against the rebased clock.

`PaceUntil` is **removed from the single-sink video path** and **deleted from no other
path**.

### Decision detail: self-driven delivery loop, not a compositor-tick hook

The Stage-1 sketch (struck through below) imagined choosing the frame *on each compositor
tick*, driven by the keyed-mutex present-completion. Stage 2 instead drives selection from a
**self-owned delivery loop** inside `ClockSelectVideoSink` that waits on the master clock
directly. Rationale:

- **Sink-agnostic.** The decorator wraps *any* `IVideoSink` (Avalonia zero-copy,
  `WriteableBitmap`, SDL, headless/test), so every single-sink path is paced uniformly. A
  compositor-tick hook would only exist for the keyed-mutex Avalonia presenter and would
  re-fragment pacing across sink types — the exact footgun Stage 1's "pace everything
  upstream" note warned about.
- **It fixes the bug directly.** The confirmed defect is the held lease inside the graph, not
  the choice of *who* ticks. Releasing the lease at enqueue and selecting in a shell loop
  removes the coupling without taking a dependency on present-completion plumbing.
- **The downstream presenter keeps its own latest-wins render tick** — it now only ever
  receives clock-selected frames, so latest-wins there is harmless (and a future
  compositor-driven refinement remains possible without re-introducing the in-graph lease).

### Scope: configurator-terminated paths retain `PaceUntil`

When a consumer supplies a configurator but **no single sink** (fan-out / inference chains
that wire their own sinks — AvaloniaMulticast, LiveCaptioning), there is no single sink to
decorate, and the substrate's pull/forward node model cannot express a buffering clock-pump
operator without a much larger change. Those paths therefore **keep the in-graph `PaceUntil`**
(upstream of the pause gate, so a frame forwarded on the wait-cap is held by the closed gate,
not leaked). They were never the confirmed held-lease problem — that is specifically the
single-sink zero-copy presenter — so their lease characteristic is unchanged. Generalising
select-by-clock to fan-out is left as future work (it needs a graph-level multi-sink
clock-pump node).

### Superseded sketch (Stage 1's deferral note)

> ~~The residual jitter source after this change is the *presenter's own* render tick (a UI
> `DispatcherTimer`), which can also starve. The durable follow-up — only if extreme
> contention shows it's needed — is presenter-side **select-by-clock**: a small PTS-tagged
> frame ring, choose the frame matching the on-demand clock on each compositor tick (driven
> by the keyed-mutex present-completion we already track), and drop `PaceUntil` from the
> video path entirely. Not built speculatively.~~

## Alternatives considered

- **Keep the push model, harden the ticker** (dedicated high-priority thread, multimedia
  timer). A band-aid: raises the starvation threshold but doesn't remove the failure class,
  and keeps the extra timer thread. Rejected.
- **Hybrid push + on-demand fallback.** More moving parts than a pure pull for no benefit
  once the value is computed on demand anyway. Rejected.
