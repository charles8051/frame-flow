# ADR-XXXX: A video-only pipeline that falls behind skips decode work, rather than losing time

## Status

Proposed (2026-09-08). Draft pending number assignment.

Narrows the video-only fallback [ADR-0003](ADR-0003-audio-master-sync-policy.md)
left open, and implements the drop responsibility ADR-0003 already assigns to the
playback layer. Leaves [ADR-0060](ADR-0060-video-send-backpressure-policy.md)'s
backpressure policy in place.

Answers [frame-flow#82](https://github.com/charles8051/frame-flow/issues/82).

## Context

### The policy question

When a video-only pipeline cannot keep up there are two defensible answers:

- **Stay realtime, drop frames.** What a player should do.
- **Play everything, run slow.** What an offline analysis pass wants.

The pump runs the second. The clock and the reported position run the first, and
nothing reconciles them. That contradiction is #82: on a 45 s file the position
reads `00:30.011` while the picture is at 16.95 s, and on a 30 s file the same run
passes its own duration while still `Playing`.

**The decision is realtime.** The rest of this ADR is about the only mechanism
that delivers it.

### What the failure actually is

`bench-2160p60-h264-aac.mp4` through the headless sink, which runs with
`yieldHardwareFrames` false and so copies every hardware frame GPU to CPU. 30 s
window, RTX 3080 Ti:

```
  state     Playing 00:30.011/00:45.000  gen=1
  video     decoded=1017 errors=0 shed=0 backend=D3D11Va
  sink      presented=1016 dropped=0 sync-dropped=1
  lag       00:13.077  — position is ahead of the picture
```

The consumer is below realtime, so the pipeline is below realtime. Nothing is
lost — letting the run continue reaches EOF with 2609 of 2700 decoded — but for
thirteen seconds the player reports a position no viewer is looking at.

### It is not about resolution, and not about readback

`--present-cost` makes the consumer slow at a chosen point in the chain:

| bottleneck | decoded / 30 s | sync-dropped | lag |
| --- | --- | --- | --- |
| 1080p60, none | 1809 | 0 | `00:00.012` |
| 1080p60, slow **presenter** (`--present-cost 30ms`) | 1808 | **811** | `00:00.020` |
| 2160p60, slow **decoder** (readback) | 1017 | 1 | **`00:13.077`** |

A slow presenter backs frames up into the clock-select ring, so `Select` has
something older to discard and the clock holds. A slow decoder means the ring
never holds more than one frame — nothing droppable, every frame presented late,
clock walks away.

Any per-frame consumer below realtime reaches the same place: inference on every
frame, software decode on a weak box, a slow presenter. The readback is what
triggered it here, not what causes it.

### Dropping packets at the queue cannot fix this

This is the part that decides the mechanism, and it is measured rather than
argued. **The audio path already runs packet-level dropping** —
`DropNewestWhenQueueFull` is true whenever audio shares the pump — so it shows
what that policy achieves on this workload:

```
  video     decoded=1008 errors=0 shed=704 backend=D3D11Va
  lag       00:09.219  — position is ahead of the picture
```

704 packets shed, and still **9.2 seconds behind**. Packet dropping is running at
full tilt and the position is nowhere near the picture.

Two structural reasons, both already documented in this repository:

**The decoder trails the pump by a queue's worth.**
[`ReadAheadCapacity`](../../src/FrameFlow.Decoding/ReadAheadCapacity.cs) says so
directly — "the decoder trails the pump by a queue's worth of packets" — and sizes
the video queue to `DefaultVideoReadAhead`, **12 seconds**. That number is not
free to lower: the queue must hold more *time* than the audio queue so audio is
the stream that fills first and paces the pump. Lower it and ADR-0060's throttle
inverts. So a 9.2 s lag is not a malfunction of packet dropping; it is the read-
ahead the design requires, observed from the other end.

**Drop-newest discards at the wrong end.** A full queue sheds the packet demux
just offered, while the decoder reads from the head. The queue front stays where
the decoder is, and nothing moves the decoder toward the clock.

Neither is fixed by shedding harder, and neither is fixed by pacing the reader —
a reader gate bounds how far *demux* runs ahead, not how far *decode* falls
behind. See *Alternatives, F*, which is the version of this ADR that got that
wrong.

### Where lateness is already handled correctly

The 1080p slow-presenter row is the one with near-zero lag, and the mechanism
behind it is the model for this decision. `ClockSelectBuffer.Select` compares
frame PTS against the master clock and discards what is already past. It is a
**lateness test**, not a fullness test, and it is the only thing in the pipeline
that holds position and picture together.

It cannot solve the decode-bound case on its own — with one frame in a ring of
three there is nothing to choose between, and by then the decode cost is already
paid. The lesson is the comparison it makes, not the place it makes it.

## Decision

**A video-only pipeline that falls behind stays realtime by skipping decode
work.** It does not play slowly, and it does not report a position the picture
has not reached.

### 1. The playback layer drives a decode discard level from measured lateness

[ADR-0003](ADR-0003-audio-master-sync-policy.md) already assigns "when to drop or
skip a late frame" to the playback orchestration layer. This implements that
rather than adding to it.

Playback observes lateness — it already computes
`PipelineDiagnosticsSnapshot.VideoPresentationLag` — and sets a discard level on
the decoder. The decoder obeys and holds no timing policy of its own.

This is deliberately the direction that keeps the clock out of the decoding
layer. ADR-0009 put timing outside it, and a decoder that read the master clock
to make its own skip decisions would widen that boundary for no gain: the layer
that already owns the policy also already has the number.

### 2. The decoder's discard level escalates and de-escalates

FFmpeg's own lever, `AVCodecContext.skip_frame`, in the order that costs least
first:

| level | discards | cost |
| --- | --- | --- |
| `AVDISCARD_NONE` | nothing | the normal state |
| `AVDISCARD_NONREF` | non-reference frames | nothing references them; no GOP damage |
| `AVDISCARD_BIDIR` | B-frames | more saved, more motion lost |
| `AVDISCARD_NONKEY` | everything but keyframes | recovers fastest, visibly stutters |

Escalate while lateness exceeds the threshold, de-escalate as it recovers, with
hysteresis wide enough that a pipeline sitting near the boundary does not
oscillate between levels.

`AVDISCARD_NONREF` first is what makes this safe by default. Non-reference frames
are exactly the ones nothing else decodes from, so the cheapest step damages
nothing. The destructive levels are reached only when the cheap one has not
recovered the deficit.

### 3. `skip_frame` gets bound

The `AVDiscard` enum is already bound and used —
`DemuxSession` sets `stream->discard = AVDISCARD_ALL` for streams with no consumer
(ADR-0059). `AVCodecContext.skip_frame` is not bound yet. That is new work, and it
is small.

### 4. Nothing changes in the pump or the queue

No read-ahead gate, no change to `DropNewestWhenQueueFull`, no change to
`ReadAheadCapacity`. A decoder that skips forward drains its queue, which lets
ADR-0060's existing backpressure pace the pump at realtime again on the video-only
path, and reduces shedding on the audio path.

The whole change is a discard level, a policy that sets it, and the binding it
needs.

## Consequences

### Positive

- Attacks the cost that is actually over budget. The decoder is what cannot
  sustain the rate, so decode work is what has to shrink; every mechanism that
  moves packets around leaves that untouched.
- The reported position describes content the pipeline has reached, on every
  configuration. `Position` and `Ended` become usable by a caller driving a
  playlist without a watchdog around them.
- Helps the audio path too, which today sheds 704 packets and still runs 9.2 s
  behind. The same escalation lets the decoder close that gap.
- Graduated rather than binary. A pipeline 200 ms behind loses B-frames; only one
  that stays badly behind reaches keyframes-only.
- No new coupling. Playback already owns the policy and already has the number;
  the decoder gains a setter.

### Negative

- **Video-only playback on a machine that cannot keep up now loses frames where
  it used to lose time.** The intended trade, and a behaviour change for anything
  relying on complete-but-slow.
- Analysis and transcode passes want every frame. This ADR gives them no way to
  ask for it. An explicit non-realtime mode is the obvious follow-on and is
  deliberately not decided here — a mode is easier to add once one coherent policy
  exists than while two incoherent halves do.
- Discard levels are visible as motion artefacts before they are visible as
  stutter. `AVDISCARD_BIDIR` on high-motion content looks worse than its frame
  count suggests.
- Escalation is a feedback loop against a measurement that already lags reality.
  The hysteresis is what keeps it stable, and hysteresis chosen badly is its own
  failure mode.

### Neutral

- A pipeline that keeps up never leaves `AVDISCARD_NONE`, so a healthy run is
  bit-identical to today on every configuration.

## Alternatives considered

### A. The clock follows the video

Derive position from the last presented PTS, or reseat both clocks onto it
continuously — generalising what `RepositionAsync` already does at a seek.

Rejected. It makes the halves agree by adopting the *other* policy: video-only
playback becomes permanently slow, and a live or long-running source drifts
without bound. Wrong for the single-window signage shape ADR-0060 validated
against, where a dropped frame is cheaper than a growing offset.

### B. Clamp the reported position to the duration

Rejected: it replaces an obviously wrong number with a plausible one.
`00:32.007/00:30.000` announces that something is broken.
`00:30.000/00:30.000` over a picture at 17 s does not.

### C. Report the lag and change nothing else

The step before, landed in [#84](https://github.com/charles8051/frame-flow/pull/84).
It is the instrument this decision is measured with and fixes nothing alone.

### D. Stop the headless sink forcing a GPU-to-CPU copy

Rejected as a fix for this, though it may be worth doing anyway. `--present-cost`
reproduces a slower-than-realtime consumer at 1080p with no readback involved, so
the class is independent of the mechanism that triggered it here.

### E. Bound the decoder queue in time instead of in slots

Already done, and it does not address this.
[`ReadAheadCapacity`](../../src/FrameFlow.Decoding/ReadAheadCapacity.cs) sizes the
video queue from the stream's frame rate to a 12-second target, and records why a
per-packet time bound was rejected in favour of a capacity derived once. Shrinking
that target is not available: the video queue must hold more time than the audio
queue or ADR-0060's throttle inverts.

### F. Pace demux reads against the master clock, and drop-newest unconditionally

**This was the first draft of this ADR, and it was wrong.** Recorded in full
because the reasoning is easy to arrive at again.

The proposal was ADR-0060's own deferred alternative C: bound how far the pump
reads ahead of the master clock, then restore drop-newest for video-only since
blocking would no longer be the pacing mechanism.

It does not fix the failure. A read-ahead bound constrains how far *demux* runs
ahead, not how far *decode* falls behind, and the queue's depth is a design
requirement rather than an accident. The measurement that settles it is the audio
path, which already runs the proposed configuration — realtime-paced demux plus
unconditional drop-newest — and sits **9.2 seconds behind while shedding 704
packets**.

It would also have added clock plumbing to the decoding layer and a new pacing
policy to the pump, to deliver an outcome that configuration already demonstrably
does not deliver.

## Validation

Not yet performed — this ADR proposes the change, it does not record it. What
would have to hold:

- **2160p60 fixture, video-only, 30 s window:** lag falls from 13.1 s to inside
  the escalation threshold; presented frame count falls; position tracks content;
  the run reaches `Ended` at the file's duration.
- **2160p60 fixture with audio:** lag falls from 9.2 s. This case is the reason to
  believe the mechanism generalises, because it is already dropping packets and
  still behind.
- **1080p60 fixture, both paths:** unchanged, and the discard level never leaves
  `AVDISCARD_NONE`. A pipeline that keeps up must not pay anything for this.
- **A long-GOP source at `AVDISCARD_NONKEY`:** output stutters to the keyframe
  interval and does not corrupt. This is the case where the escalation is most
  destructive and it needs its own fixture; the corpus has none today.
- **Escalation as a pure function** of lateness, current level and hysteresis —
  testable without FFmpeg, a decoder, or a clock.
- **`RunDemuxPump_RealVideoDecoder_AudioDiscarded_LongClip_DoesNotPrematurelyEof`
  keeps passing**, unchanged. This decision does not touch the pump, so ADR-0060's
  regression test should be untouched by it; if it moves, something is wrong.

## Open questions

- **The threshold and the hysteresis.** Both are empirical and both go in this ADR
  when measured. `VideoPresentationLag` is how they get chosen.
- **Whether hardware decode honours `skip_frame` usefully.** With D3D11VA the GPU
  does the decoding, so the saving may come mostly from the frames not produced
  rather than the work not done. On the readback-bound case that is still the
  saving that matters, but it should be measured rather than assumed.
- **Whether an explicit non-realtime mode follows** for analysis consumers.
  Deliberately out of scope; see Consequences.
- **Whether `AVDISCARD_BIDIR` earns its place** between NONREF and NONKEY, or
  whether two levels are enough. Three is proposed on the grounds that the jump
  from NONREF to keyframes-only is very large.
- **What happens across a seek** while escalated. The level should reset with the
  run, on the same reasoning that `PresentationLag` reports null on a fresh run.

## References

- [ADR-0003](ADR-0003-audio-master-sync-policy.md) — audio-master policy; the
  video-only fallback this narrows, and the drop responsibility it implements.
- [ADR-0009](ADR-0009-threading-and-concurrency-model.md) — timing outside the
  decoding layer, which the direction of control here preserves.
- [ADR-0059](ADR-0059-discard-streams-with-no-consumer.md) — the existing
  `AVDiscard` use at the demuxer.
- [ADR-0060](ADR-0060-video-send-backpressure-policy.md) — backpressure policy,
  left in place.
- [frame-flow#82](https://github.com/charles8051/frame-flow/issues/82) — the
  defect.
- [frame-flow#84](https://github.com/charles8051/frame-flow/pull/84) — the lag
  metric this is measured with.
- `src/FrameFlow.Decoding/ReadAheadCapacity.cs` — the 12-second read-ahead and why
  it cannot shrink.
- `src/FrameFlow.Playback/ClockSelectVideoSink.cs` — `Select`, the lateness test
  this generalises.
