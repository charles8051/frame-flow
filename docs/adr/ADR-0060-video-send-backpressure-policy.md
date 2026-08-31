# ADR-0060: Video packet-send backpressure policy (block vs drop-newest)

## Status

Accepted.

**Date:** 2026-06-06
**Related:**
- ADR-0009 (threading and concurrency model — one demux pump reads the single `AVFormatContext`)
- ADR-0059 (discard streams with no consumer — the change that exposed this)
- ADR-0003 (audio-master sync — a null audio sink falls back to the wallclock pacer)
- ADR-0057 (pull-based master clock)

## Context

A media source is read by a **single** demux pump
(`DecodingPipeline.RunDemuxPumpAsync`, ADR-0009). It calls `av_read_frame` in a
loop and routes each packet to the matching decoder's bounded packet queue. The
pump has no clock of its own — its read rate is whatever the decoder queues let
it be. So the queues' full-mode behaviour **is** the pump's backpressure:

- **Audio queue** (`AudioDecoder`) is `BoundedChannelFullMode.Wait`: a send to a
  full queue **blocks**. When audio is consumed, this paces the pump to the audio
  consumer's rate.
- **Video queue** (`VideoDecoder`) used drop-newest: `SendPacketAsync` did
  `TryWrite`, and on a full queue **freed the packet and returned** (never
  blocked), incrementing a dropped-for-backpressure counter. The stated rationale
  was "the demux pump must never block on video send" — because blocking it on a
  slow video chain would also stop it reading audio, starving audio and freezing
  the clock. Drop-newest preserves the queued GOP prefix, so the artifact is
  "video holds the last good frame for a beat" rather than garble.

That design has a hidden assumption: **something else paces the pump.** With an
audio stream consumed, the audio `Wait` queue does. Drop-newest then only sheds a
few *fresh* video packets during a transient (e.g. a seek), which is exactly what
it was built for.

### The bug

ADR-0059 made FrameFlow **discard** a stream that has no consumer (audio with a
null sink — the muted-signage / `audioSink: null` path, which falls back to the
wallclock pacer per ADR-0003). That correctly fixed the old "undrained audio
queue wedges the pump" deadlock. But it also removed the audio `Wait` queue —
which had been the pump's only backpressure. Now the pump is paced **only** by
the video queue, and the video queue's drop-newest *never blocks*.

So the pump reads the **entire file at IO speed**, floods the 512-slot video
queue, and the drop-newest fallback sheds the vast majority of the video — it
keeps roughly the first queue's worth and drops the rest. Playback shows the
first ~512 frames (~20 s at 25 fps) and then **starves**: no more frames, while
the wallclock keeps advancing (the seek thumb keeps moving on a frozen picture).

Reproduced end-to-end with a 213 s H.264/AAC clip played `audioSink: null`
+ GPU presenter: video froze ~23 s in. Reproduced deterministically at the
decoding layer — with the audio stream discarded and the video drained at
~25 fps, the demux pump read all **5327 packets in 250 ms** and reported EOF,
instead of pacing to the consumer.

This is *not* an FFmpeg problem: an FFmpeg-domain review confirmed `AVDISCARD_ALL`
is the correct, idiomatic discard and does **not** synthesize a premature EOF.
The "EOF at 126 ms" in the field log was the pump genuinely reading the whole
file because nothing throttled it. The root cause is the **asymmetric queue
policy**: audio blocks, video drops, and once audio is gone nothing blocks.

## Decision

**The video packet send blocks by default; it uses drop-newest only when an
audio stream shares the same demux pump.**

`VideoDecoder` gains a `DropNewestWhenQueueFull` flag (default `false`):

- `false` (default): a send to a full queue **blocks** (`WriteAsync`). This is
  the correct backpressure when video is the sole consumed stream — it paces the
  otherwise-unthrottled pump to the video consumer's rate, so the whole file is
  read at playback speed and plays to completion with a clean EOS.
- `true`: a send to a full queue **drops the packet** (the prior behaviour).
  Safe only when audio also consumes the pump, where the audio `Wait` queue
  provides the pacing and blocking on a slow video chain would wedge the pump and
  starve audio.

`SubstrateSession` sets the flag to mirror whether audio is consumed:

```csharp
videoDecoder.DropNewestWhenQueueFull = audioHasConsumer;
```

So:
- **A/V with audio consumed** → audio `Wait` paces the pump; video drops-newest
  (a slow video chain cannot starve audio). Unchanged from before.
- **Audio discarded / video-only** → video blocks; the video queue paces the
  pump. Fixed.

Drop-newest stops being the default precisely because defaulting to "never block"
is only valid when *some other* consumed stream blocks — an invariant ADR-0059
broke for the no-audio case.

## Consequences

### Positive

- **Video-only / `audioSink: null` playback paces correctly and plays to
  completion** with a clean end-of-stream, instead of freezing after ~one queue's
  worth of frames. Fixes the regression ADR-0059 introduced for that path.
- **No behaviour change for normal A/V playback.** With audio consumed, the video
  send still drops-newest, so a slow video chain still can't starve audio, and
  the dropped-for-backpressure counter still surfaces transient sheds.
- **Backpressure is symmetric and intentional.** The pump is always paced by at
  least one consumed stream's `Wait` queue.

### Negative / trade-offs

- **One more piece of cross-layer wiring.** `SubstrateSession` must set the flag
  from `audioHasConsumer`. It sits next to the existing `YieldHardwareFrames`
  assignment, and the default (`block`) is the safe one, so a caller that forgets
  to set it gets correct backpressure, not a silent starvation.
- **A genuinely slow video chain with no audio now blocks the pump** rather than
  shedding frames. That is the *correct* behaviour (read at the rate you can
  present; don't read 200 s ahead and throw it away), and with no audio there is
  nothing to starve. If the video consumer truly stalls, video freezes — but that
  is a real downstream stall, not this bug, and the wallclock-independent freeze
  is the honest signal.

### Neutral

- ADR-0059's discard is unchanged and correct; this ADR restores the backpressure
  that discard removed. ADR-0003 (wallclock fallback) and ADR-0057 (pull clock)
  are untouched.

## Alternatives considered

### A. Revert ADR-0059 (don't discard the unconsumed audio)

Rejected. The discard is correct and efficient (no wasted audio demux/decode),
and reverting reintroduces the original undrained-queue deadlock. The discard is
not the bug; the missing video backpressure is.

### B. Always block the video send (drop the drop-newest entirely)

Rejected. When audio shares the pump and the video chain is sustainedly slow,
blocking the video send wedges the pump and starves audio + freezes the clock —
the exact failure drop-newest was added to prevent. The policy must be
conditional on whether audio shares the pump.

### C. Pump-level read-ahead bound keyed to the master clock

Give the pump a clock and stop it reading more than N seconds ahead of the
playback position. More general, but a larger change: the pump (ADR-0009) has no
clock today, and the per-decoder queue is the existing, sufficient backpressure
mechanism — it just had the wrong default for video. Revisit only if a future
need (e.g. multi-pump or seek-heavy prefetch tuning) actually calls for it.

## Validation

- **Decoding layer (deterministic) —
  `NoConsumerStreamDiscardTests.RunDemuxPump_RealVideoDecoder_AudioDiscarded_LongClip_DoesNotPrematurelyEof`.**
  Plays a long clip with the audio stream discarded and the video drained at
  ~25 fps. Before: the pump read the whole file and hit EOF in ~250 ms
  (`EndOfStream=true`). After: the pump is gated at ~512 queued packets and reads
  at ~realtime (`EndOfStream=false`, `PacketsRead` climbs slowly). Fails before,
  passes after.

  **The fixture changed after this ADR was written, and the numbers above are
  from the original run.** It used the 213 s clip named in *Reproduction*, a file
  no corpus generates, so the test silently no-opped everywhere but one machine.
  It now uses `bench-1080p60-h264-aac.mp4` — 45 s at 60 fps, ~2700 video packets,
  written by `generate-test-corpus.cs --include-benchmarks`. Same mechanism, same
  assertion; on a default corpus the test reports **skipped** rather than passing,
  so the suite does not claim coverage it does not have. The shorter fixtures
  cannot substitute: the longest non-benchmark one reaches EOF at 601 packets
  inside the first poll.
- **End-to-end —** reproduced and confirmed fixed via the instrumented
  `FrameFlow.Examples.AvaloniaPlayer` (`--presenter gpu --no-audio`, the
  signage shape).
- **Full suite green**, including the existing `NoConsumerStreamDiscardTests`
  A/V-case coverage (drop-newest still active when audio is consumed).

## References

- `src/FrameFlow.Decoding/VideoDecoder.cs` — `DropNewestWhenQueueFull` +
  `SendPacketAsync` policy switch.
- `src/FrameFlow.Playback/SubstrateSession.cs` — sets the flag from
  `audioHasConsumer`.
- `src/FrameFlow.Decoding/DecodingPipeline.cs` — the single demux pump.
- ADR-0059 — the discard that removed the audio backpressure this restores.
