# ADR-XXXX: The demux pump reads against the master clock, so video-only playback drops instead of falling behind

## Status

Proposed (2026-09-08). Draft pending number assignment.

Takes up the alternative [ADR-0060](ADR-0060-video-send-backpressure-policy.md)
deferred as *C. Pump-level read-ahead bound keyed to the master clock*, on the
condition it set for revisiting: "only if a future need actually calls for it."
[frame-flow#82](https://github.com/charles8051/frame-flow/issues/82) is that need.

Narrows the video-only fallback [ADR-0003](ADR-0003-audio-master-sync-policy.md)
left open, and reverses ADR-0060's `DropNewestWhenQueueFull` default once the
pacing it provided comes from somewhere else.

## Context

### What ADR-0060 decided, and the assumption under it

ADR-0060 gave the video packet send two behaviours on a full queue, chosen by
whether audio shares the demux pump:

```csharp
videoDecoder.DropNewestWhenQueueFull = audioHasConsumer;
```

With audio, a full queue frees the packet and counts it as shed — blocking there
would wedge the pump and starve audio. Without audio, the send **blocks**, which
paces the otherwise-unthrottled pump to the video consumer. ADR-0060 states the
goal plainly: the file is then "read at playback speed and plays to completion
with a clean EOS."

That holds on one assumption, which the ADR does not state: **that the video
consumer runs at playback rate.** Pacing the pump to the consumer is pacing it to
realtime only while the consumer *is* realtime. Below that rate the pump is paced
below realtime too — and the master clock is not. The two diverge, and nothing in
the system reconciles them.

### What it looks like when the assumption fails

`bench-2160p60-h264-aac.mp4` through the headless sink, which runs with
`yieldHardwareFrames` false and so copies every hardware frame GPU to CPU. 30 s
window, RTX 3080 Ti, Threadripper PRO 5945WX:

```
  state     Playing 00:30.011/00:45.000  gen=1
  video     decoded=1017 errors=0 shed=0 backend=D3D11Va
  sink      presented=1016 dropped=0 sync-dropped=1
  lag       00:13.077  — position is ahead of the picture
```

1017 frames is 16.95 s of content. The position says 30.011. Nothing is lost —
letting the run continue reaches EOF with 2609 of 2700 decoded and `shed=0` — but
for thirteen seconds the player reports a position no viewer is looking at, and on
a 30 s file the same run passes its own duration while still `Playing`.

### It is not about resolution, and not about readback

`--present-cost` makes the consumer slow at a chosen point in the chain. Applied
to the 1080p fixture it reproduces "consumer slower than realtime" and does
**not** reproduce the divergence:

| bottleneck | decoded / 30 s | sync-dropped | lag |
| --- | --- | --- | --- |
| 1080p60, none | 1809 | 0 | `00:00.012` |
| 1080p60, slow **presenter** (`--present-cost 30ms`) | 1808 | **811** | `00:00.020` |
| 2160p60, slow **decoder** (readback) | 1017 | 1 | **`00:13.077`** |

The middle row loses 811 frames and its clock stays honest. Dropping is *how* it
stays honest.

The difference is where the bottleneck sits relative to the clock-select ring.
`ClockSelectBuffer` drops late frames in `Select`, but the ring is capacity 3. A
slow presenter backs frames up into it, so there is something older to discard. A
slow decoder means the ring never holds more than one frame — nothing is
droppable, every frame is presented late, and the clock walks away.

### The structural gap

Drop mechanisms in the pipeline today:

| configuration | upstream of the ring | downstream (in the ring) |
| --- | --- | --- |
| audio present | drop-newest sheds at the decoder | `Select` drops late |
| video-only | **none — the send blocks** | `Select` drops late |

Video-only is the only configuration in the system with no upstream drop
mechanism at all. ADR-0009 anticipated the mechanism in the abstract — "bounded
channels with Wait mode mean the demux loop can stall when downstream consumers
are slow" — and treated it as a tuning problem. It is a policy problem.

ADR-0003 assigns timing policy to the playback orchestration layer and names
"when to drop or skip a late frame" as part of it. ADR-0060 removed that
capability from the video-only path. That was a reasonable local fix for the bug
it was solving and it was not recorded as a policy change, so the policy now
differs by configuration without anywhere saying so.

### Two coherent policies, and the system runs half of each

When a video-only pipeline cannot keep up there are two defensible answers:

- **Stay realtime, drop frames.** What a player should do, and what the audio
  path already does.
- **Play everything, run slow.** What an offline analysis pass wants.

The pump runs the second. The clock and the reported position run the first.
Neither is wrong on its own. The contradiction between them is
[frame-flow#82](https://github.com/charles8051/frame-flow/issues/82).

## Decision

**Video-only playback stays realtime.** A pipeline that cannot sustain the rate
drops frames. It does not play slowly, and it does not report a position the
picture has not reached.

Three parts, in dependency order.

### 1. The demux pump reads against the master clock

The pump gains a read-ahead bound: it will not read further than `N` of stream
time ahead of the master clock's position. When it is that far ahead it waits on
the clock rather than on a queue write.

This is the pacing ADR-0060 obtained from the blocking send, sourced from the
thing that actually defines playback speed instead of from a downstream
consumer's throughput. A consumer slower than realtime no longer drags the read
rate down with it.

The gate belongs in `RunDemuxPumpAsync`'s imperative shell, before the `ReadNext`
step. `DemuxPump.Step` stays pure and clock-free: the shell already owns await,
cancellation and the native pointer, and the wait is one more effect it performs
on the core's behalf.

### 2. `DropNewestWhenQueueFull` becomes unconditionally true

With the pump clock-paced, blocking is no longer the mechanism that keeps the
pump honest, so the video send no longer needs it. A full queue frees the packet
and counts it as shed, on every configuration.

Unconditional drop-newest is what ADR-0060 moved *away* from, and its reason is
the one that has to be answered rather than sidestepped: "defaulting to *never
block* is only valid when some other consumed stream blocks." That was true, and
it is why the change is in this order — part 1 supplies the other throttle, so
part 2 is no longer the removal of the last one.

(Not to be confused with ADR-0060's alternative B, which is the reverse: always
block, on every configuration. That stays rejected for its own reason — blocking
with audio present wedges the pump and starves the clock.)

### 3. The bound `N` is chosen against the lag metric, not guessed

`PipelineDiagnosticsSnapshot.VideoPresentationLag` landed in
[#84](https://github.com/charles8051/frame-flow/pull/84) for this purpose. `N` has
a floor and a ceiling, and the useful range between them is an empirical question:

- **Floor:** larger than one GOP plus whatever a seek prefetches, or the pump
  starves the decoder at every keyframe boundary.
- **Ceiling:** small enough that the queue cannot absorb the drift the bound
  exists to prevent. A bound of thirty seconds reproduces today's behaviour.

Start from the queue's current effective depth expressed in seconds and tune with
the lag metric on the 2160p60 fixture. The number goes in this ADR when it is
measured, not before.

## Consequences

### Positive

- The reported position describes content the pipeline has reached, on every
  configuration. `Position` and `Ended` become usable by a caller driving a
  playlist without a watchdog around them.
- Shed accounting works on both paths. Today a video-only pipeline over budget
  reports `shed=0`, which is true and useless; after this it reports what it
  discarded, as the audio path already does.
- One policy across configurations. "Falls behind" stops meaning two different
  things depending on whether the file has an audio track.
- ADR-0003's stated ownership of drop decisions is restored to the video-only
  path.
- The pump's read rate stops being a function of downstream throughput, which
  decouples it from every future consumer's per-frame cost.

### Negative

- **Video-only playback on a machine that cannot keep up now loses frames where
  it used to lose time.** That is the intended trade and it is a behaviour change.
  Any consumer relying on the current complete-but-slow behaviour is relying on
  something this reverses.
- Analysis and transcode passes genuinely want every frame. This ADR does not give
  them a way to ask for it; it makes realtime the only policy. An explicit
  non-realtime mode is the obvious follow-on and is deliberately not decided here
  — adding a mode is easier once there is one coherent policy than while there are
  two incoherent halves.
- The pump gains a clock. ADR-0009 kept timing out of the decoding layer, and this
  is a real widening of that boundary, not an implementation detail.
- Read-ahead becomes bounded in time rather than in packets, so a high-bitrate
  source buffers more bytes for the same `N`. Memory-bounded backpressure
  ([#7](https://github.com/charles8051/frame-flow/issues/7)) is where that gets
  settled, not here.

### Neutral

- With audio present, nothing changes in behaviour. The audio `Wait` queue already
  paced the pump and drop-newest was already on; the clock bound is a second,
  looser throttle that a healthy A/V pipeline never reaches.

## Alternatives considered

### A. The clock follows the video

Derive position from the last presented PTS, or reseat both clocks onto it
continuously — generalising what `RepositionAsync` already does at a seek.

Rejected. It makes the two halves agree by adopting the *other* policy: video-only
playback becomes permanently slow rather than dropping, and a live or long-running
source drifts without bound. It is also the wrong default for the single-window
signage shape ADR-0060 validated against, where realtime is the requirement and a
dropped frame is cheaper than a growing offset.

Worth recording that this was the first proposal, and that ADR-0060's own
alternatives list is what argued it down.

### B. Clamp the reported position to the duration

Rejected, and it is worth being explicit about why: it replaces an obviously wrong
number with a plausible one. `00:32.007/00:30.000` at least announces that
something is broken. `00:30.000/00:30.000` over a picture at 17 s does not.

### C. Report the lag and change nothing else

Not an alternative so much as the step before. Landed in #84. It is the instrument
this decision is measured with, and it fixes nothing on its own.

### D. Stop the headless sink forcing a GPU-to-CPU copy

Rejected as a fix for this, though it may be worth doing for its own sake.
`--present-cost` reproduces a slower-than-realtime consumer at 1080p with no
readback involved, so the class of failure is independent of the mechanism that
happened to trigger it here. Any per-frame consumer below realtime — inference on
every frame, a slow presenter, software decode on a weak box — reaches the same
place.

### E. Bound the decoder queue in time instead of in slots

Closely related, and the same idea one layer too low.
[#31](https://github.com/charles8051/frame-flow/issues/31) already notes the queue
is sized from `avg_frame_rate` rather than a measured rate. But the queue cannot
see the master clock either, so a time-bounded queue would still be measuring
against the wrong reference. Fixing the sizing is worth doing; it does not
substitute for pacing the reader.

## Validation

Not yet performed — this ADR proposes the change, it does not record it. What
would have to hold:

- **On the 2160p60 fixture, video-only, 30 s window:** lag stays inside the chosen
  bound instead of reaching 13 s; `shed` climbs from 0 to roughly the frames the
  pipeline could not take; position tracks content; the run reaches `Ended` at the
  file's duration.
- **On the 1080p60 fixture, video-only:** unchanged. It keeps up today, so the
  clock bound must never be the binding constraint there, and `shed` must stay 0.
- **With audio, both fixtures:** unchanged, including the existing A/V-case
  coverage in `NoConsumerStreamDiscardTests`.
- **`RunDemuxPump_RealVideoDecoder_AudioDiscarded_LongClip_DoesNotPrematurelyEof`
  keeps passing.** It is ADR-0060's own regression test — the pump must still not
  read the file at IO speed. Its mechanism changes from queue backpressure to the
  clock bound, so the assertion needs re-reading against the new reason even though
  the observable is the same. This test skips on a default corpus; it needs
  `--include-benchmarks`.
- **A test at the seam**, not only end to end: the read-ahead decision should be a
  pure function of clock position, last-read PTS and the bound, testable without
  FFmpeg.

## Open questions

- **The value of `N`.** See Decision section 3. Nothing else here depends on which
  number it is, only on there being one.
- **Whether an explicit non-realtime mode follows.** Analysis consumers want every
  frame. Deliberately out of scope; see Consequences.
- **What the pump reads the clock through.** `IClockSource` is the master-clock
  interface, but handing the decoding layer the playback layer's clock is the
  ADR-0009 widening named above. A narrower read-only seam may be worth defining
  rather than passing the clock itself.
- **Whether `sync-dropped` and `shed` should be reconciled** in the diagnostics
  once both paths can shed. They count different discards for different reasons,
  and a reader currently has to know which is which.

## References

- [ADR-0003](ADR-0003-audio-master-sync-policy.md) — audio-master policy; the
  video-only fallback this narrows, and the ownership of drop decisions.
- [ADR-0009](ADR-0009-threading-and-concurrency-model.md) — bounded channels, and
  the stall this turns from a tuning note into a policy.
- [ADR-0057](ADR-0057-pull-based-master-clock.md) — the pull-model clock the bound
  reads.
- [ADR-0060](ADR-0060-video-send-backpressure-policy.md) — the decision amended,
  and the alternative C taken up.
- [frame-flow#82](https://github.com/charles8051/frame-flow/issues/82) — the
  defect.
- [frame-flow#84](https://github.com/charles8051/frame-flow/pull/84) — the lag
  metric.
- `src/FrameFlow.Decoding/DecodingPipeline.cs` — `RunDemuxPumpAsync`, where the
  gate goes.
- `src/FrameFlow.Decoding/VideoDecoder.cs` — `SendPacketAsync` and
  `DropNewestWhenQueueFull`.
