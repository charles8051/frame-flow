# ADR-0032: Pull-Shape Playback Controller — Pipeline Accessors on `IPlaybackController`

**Status:** Accepted (implementing now). Refined by ADR-0036, which
lifts the controller-owned channels and decode workers out into a
first-class `IDecodedMediaStream`. The pull accessors documented here
remain on `IPlaybackController`; their implementation moves under the
hood.
**Date:** 2026-05-11
**Supersedes:** None
**Related:** ADR-0022 (long-lived workers with pause gate), ADR-0023 (hierarchical state machine), ADR-0025 (video sink and frame pool architecture), ADR-0026 (state-bound worker lifecycle binding), ADR-0028 (internal layering and ownership cleanup), ADR-0029 (`ChannelVideoSink` push→pull bridge), ADR-0030 (frame-contract unification with Crossbar — explicitly deferred this ADR's work), ADR-0036 (decode/playback decoupling — refines this ADR by promoting the pull-shape channels to a standalone type)

## Context

ADR-0030 unified FrameFlow's video frame and memory-domain types
with Crossbar's substrate so the playback runtime and Crossbar's
pipeline operators speak one vocabulary. The original draft of
ADR-0030 also proposed adding a pull-shape pipeline accessor to
`IPlaybackController` so consumers could compose playback output
into Crossbar pipelines without wiring an `IVideoSink` and a
`ChannelVideoSink` bridge by hand. That accessor was explicitly
**deferred** because rearchitecting the playback worker loop
touched ADR-0022 (long-lived workers), ADR-0023 (state machine),
and ADR-0026 (lifecycle binding) — all stable.

The deferral was the right call at the time. Since then, two
forces have made the case for doing it now.

### Force 1 — the inference example exposed the topology

The YOLOv8 inference example
(`FrameFlow.Examples.OnnxInference`) is the first consumer that
actually wanted a pull-shape pipeline downstream of playback. Its
current wiring is:

1. Register a singleton `ChannelVideoSink` (the ADR-0029 bridge).
2. The playback worker pushes frames at the sink (`PresentAsync`
   → channel `TryWrite`).
3. The example runs a Crossbar pipeline that pulls from
   `sink.ReadAllAsync(ct)`.

That's two indirections to express one idea: "I want to consume
playback output as a pull stream." It also leaks the push side of
the topology into example code that has no reason to know about
it — `PlaybackHarness` wires the sink, the example calls
`AddFrameFlowChannelVideoSink`, the pipeline assembly happens in
two places.

The race condition that surfaced as audible audio looping
(`OpenAlAudioSink` state lock, fixed in commit `8b370ab`) was a
direct consequence of this topology: the inference example
caused the playback video worker to decode flat-out (because
`ChannelVideoSink` drops frames non-blockingly downstream), which
caused the video worker's per-frame `_audioSink.GetPlaybackTime()`
call to race the audio worker's `WriteAsync` at an extreme rate.
The lock fixed the symptom; the topology that caused the
contention rate is still present.

### Force 2 — the multi-modal annotation pattern needs audio pull

The "merging pipelines, plainly" essay (an unpublished working note,
2026-05-11) and the
inference-example design conversation surfaced that the next
demo — adding live captioning alongside YOLO detection — needs
audio as a pull stream so a Whisper transcription pipeline can
consume it in parallel with OpenAL playback. There's currently
no `ChannelAudioSink` and no controller-level audio pipeline
accessor; any consumer that wants audio for inference has to
either hand-roll a tee or decode the source twice. Both are bad.

Adding `ChannelAudioSink` as a one-off peer to `ChannelVideoSink`
would close the immediate gap but extend the dual-bridge pattern
to audio, doubling the surface area that this ADR is supposed to
consolidate.

## Decision

Land the deferred ADR-0030 follow-up: expose decoded streams as
pull-shape pipelines directly on `IPlaybackController`, replace
the worker-loop's sink-push call with a controller-owned channel
emit, and provide adapters so existing `IVideoSink`/`IAudioSink`
consumers keep working unmodified.

### 1. New accessors on `IPlaybackController`

```csharp
public interface IPlaybackController : IAsyncDisposable
{
    // ... existing surface (Load/Play/Pause/Seek/etc.) ...

    /// <summary>
    /// Decoded, AV-paced video frames as a pull-shape pipeline.
    /// Frames are emitted only while playback is in a Playing
    /// or Rebuffering state; pause/seek correctly halt emission.
    /// </summary>
    FramePipeline<IVideoFrame> VideoFrames { get; }

    /// <summary>
    /// Decoded audio blocks as a pull-shape pipeline. Same
    /// lifecycle semantics as <see cref="VideoFrames"/>.
    /// </summary>
    FramePipeline<PcmAudioBuffer> AudioBuffers { get; }
}
```

Both pipelines are **lazy**: the controller does not start
emitting until a consumer pulls. The controller does not
maintain backlog before consumption starts (a `Channel<T>` with
bounded capacity and a `DropOldest` policy guards against
unbounded growth if a consumer is slow to start).

### 2. Worker loop rearchitecture

`PipelineController.RunVideoSinkWorkerAsync` and
`RunAudioDecodeWriteWorkerAsync` currently do:

1. Pull a frame/block from the decoder.
2. Apply AV-sync delay (video only).
3. Rent a pool frame, copy data into it (video only).
4. Push to the registered `IVideoSink`/`IAudioSink`.
5. Loop.

The new loop:

1. Pull a frame/block from the decoder.
2. Apply AV-sync delay (video only — see §3).
3. Emit into the controller-owned `Channel<FramePacket<TFrame>>`.
4. Loop.

The sink-rent-and-push step (4 above, old) goes away from the
worker. Frames flow into a `Channel<FramePacket<IVideoFrame>>`
that `VideoFrames` wraps. Backpressure is the channel's bounded
capacity; if no consumer pulls, the channel fills and the
worker waits (or drops, depending on policy — see §5).

Audio worker is symmetric: pulls a `PcmAudioBuffer`, emits to
the audio channel. No sink push.

### 3. AV-sync placement stays in the controller

AV sync — the video worker calling
`_audioSink.GetPlaybackTime()` and waiting until the frame's PTS
catches up — **stays inside the controller** before the channel
emit. Reasons:

- **Default user expectation.** `controller.VideoFrames` should
  emit "watchable at real-time speed" by default. A consumer
  that wants raw decode-rate frames is rare (inference) but
  doesn't suffer from the paced default — they can iterate as
  fast as the pacing emits.
- **Single source of truth for the clock.** Moving AV-sync into
  the consumer side would require every consumer to know about
  the audio clock, defeating the point of the abstraction.
- **The race that surfaced from the inference example is fixed
  at the right layer.** The OpenAL lock guards the
  `GetPlaybackTime` read against concurrent `WriteAsync`. With
  the pull-shape design, that read still happens — once per
  decoded frame — and the lock still applies.

**Deferred to a future ADR:** a "raw/unpaced" accessor variant
(`controller.RawVideoFrames`) for consumers that want maximum
decode throughput without AV-sync delay. Not in this ADR; YAGNI
until the inference example actually needs it (its current
behavior — pace + drop frames the inference path can't keep up
with — is fine).

### 4. Sink continuity via adapters

`IVideoSink` and `IAudioSink` remain in the public API surface.
Existing consumers (Avalonia, SDL, OpenAL) continue to work
unmodified. The adapter that bridges the pull pipeline to a
push sink is a one-line extension:

```csharp
public static Task ToSinkAsync(
    this FramePipeline<IVideoFrame> source,
    IVideoSink sink,
    CancellationToken ct)
{
    // pulls from `source`, presents to `sink`, disposes frames
    // sink owns. The sink's frame pool is honoured: rent before
    // copy, present after.
}
```

The sink wiring inside `PlaybackSession`/`PlaybackController`
that currently calls `services.AddSingleton<IVideoSink>(...)`
continues to work, but instead of the worker pushing directly
to the sink, the controller's pull pipeline is pumped to the
sink by an internal adapter task spawned at `PlayAsync`.

Net effect: existing consumers see no behavior change. New
consumers can subscribe to `controller.VideoFrames` directly.

### 5. Channel sizing and backpressure

The internal `Channel<FramePacket<TFrame>>` uses
`Crossbar.FrameChannelOptions` for capacity and overflow
policy. Default: capacity 2, `BlockProducer` overflow (the
worker waits for a consumer to read). This preserves the
current behavior where slow consumers pace the decode loop
naturally.

Consumers that need different semantics (inference example
dropping frames non-blockingly) get them by composing
`controller.VideoFrames.WithOverflowPolicy(DropIncoming)` or
by setting the policy at controller construction.

### 6. `ChannelVideoSink` deprecation, not deletion

`ChannelVideoSink` (ADR-0029) becomes redundant — its job is
the controller's job now — but stays in `FrameFlow.Playback`
marked `[Obsolete]` for one release. Migration is mechanical:
delete the `AddFrameFlowChannelVideoSink` call, swap
`bridge.ReadAllAsync(ct)` for `controller.VideoFrames`, delete
the example's bridge field.

No `ChannelAudioSink` ships. The audio peer would have been
the next thing to write; this ADR makes it unnecessary.

### 7. No new accessors on `IPlaybackSession`

`IPlaybackSession` is the controller-internal lifecycle
holder. The pipeline accessors are public-API surface on
`IPlaybackController`. The session-side plumbing (channel
construction, lifecycle) is internal.

## Consequences

### Positive

- **The dual-bridge surface collapses.** One pull accessor
  per stream, on the controller, replaces the per-example
  `ChannelVideoSink`-plus-DI-wiring dance.
- **Adding new pull consumers is a one-line consumer-side
  change.** `await foreach (var frame in controller.VideoFrames)`
  is the whole call site.
- **The multi-modal annotation pattern slots in cleanly.**
  Whisper transcription consumes `controller.AudioBuffers`;
  YOLO consumes `controller.VideoFrames`; OpenAL playback
  pulls from `controller.AudioBuffers` via a sink adapter.
  No `TeeAudioSink`, no parallel-decode workaround.
- **Inference example simplifies.** Delete the
  `ChannelVideoSink` registration; subscribe to
  `controller.VideoFrames` directly; the rest of the
  YOLOv8 detection path is unchanged.
- **Topology is consistent across video and audio.** Today
  video has a pull bridge (`ChannelVideoSink`) and audio
  doesn't; after this ADR both streams have first-class
  pull accessors. Symmetry simplifies reasoning, docs, and
  testing.

### Negative

- **Three stable ADRs touched.** ADR-0022's worker loop
  shape, ADR-0023's state machine, and ADR-0026's lifecycle
  binding all interact with the worker rearchitecture. Each
  is touched in a targeted way (worker emits to channel
  instead of sink; gate semantics unchanged; cycle bookkeeping
  unchanged), but they're each load-bearing. Regression
  surface is non-trivial.
- **Channel capacity tuning is now a published concern.**
  The current sink architecture lets the consumer (Avalonia,
  SDL) choose its own buffering implicit in its render-loop
  cadence. The pull pipeline makes capacity an explicit knob.
  Default of 2 should be fine; consumers can override.
- **One round of breaking-ish behavior change for direct
  `IVideoSink` consumers.** The sink's `PresentAsync` is now
  called by an internal adapter task pumping the pipeline,
  not by the worker directly. Subtle timing differences
  possible. Mitigated by the content-capture harness from
  ADR-0031, which catches PCM- and pixel-level regressions
  the previous test layers would have missed.

### Neutral

- **The `OpenAlAudioSink` lock stays load-bearing.** AV-sync
  still calls `GetPlaybackTime`; the cross-thread read
  pattern is the same; the lock is still required.
- **`AddFrameFlowAvaloniaVideoSink` /
  `AddFrameFlowSdlVideoSink` / `AddFrameFlowOpenAlAudio` DI
  helpers unchanged.** They still register sinks; the
  controller pumps the pull pipeline into them.

## Alternatives considered

### A. Controller-internal broadcast (both push and pull)

The controller could maintain an internal broadcast: each
decoded frame fans out to all registered sinks AND all pipeline
subscribers. Push consumers see no change; pull consumers
subscribe via the accessor.

Rejected because:
- More complex than the chosen design. A broadcast plus per-
  subscriber backpressure means the slowest consumer paces
  the rest, or different overflow policies per subscriber
  produce different drop behaviors per subscriber.
- The chosen design (pull pipeline at the worker boundary +
  adapter to sinks) achieves the same end with one shape:
  pull. Sinks are just adapter subscribers.

### B. Replace sinks entirely with pipelines

Remove `IVideoSink`/`IAudioSink` from the public API and force
all consumers to subscribe to the pull pipeline.

Rejected because:
- Breaks every existing consumer (Avalonia, SDL, OpenAL,
  example apps in three repos).
- The sink contract is genuinely the cleanest expression for
  "I'm a render target — give me a frame and I'll display it."
  Pipelines are the cleanest expression for "I'm a transform."
  Both shapes deserve to exist.

### C. AV sync as a consumer-side pipeline operator

Move AV sync out of the controller into a pipeline operator
that consumers compose explicitly:
`controller.RawVideoFrames.PacedAgainst(controller.AudioClock)`.

Rejected because:
- The default user expectation that
  `controller.VideoFrames` plays at real-time speed is too
  important to violate. Making AV sync opt-in means every
  consumer has to remember to apply it.
- The current AV-sync placement (in the worker) is the right
  layer for the work. Moving it changes the contract for
  unclear benefit.

A future ADR can revisit this if a real consumer surfaces
that wants a different pacing model.

### D. Keep `ChannelVideoSink`, add a controller-level pull
accessor on top of it

Lighter-touch: leave the worker loop alone, expose
`controller.VideoFrames` as a thin wrapper around a controller-
owned `ChannelVideoSink`. Migration is purely additive.

Rejected because:
- It doesn't solve the duplication that motivated this ADR.
  We'd have two ways to express the same idea (the sink-then-
  bridge dance and the new accessor), both supported.
- The race-amplification topology that made the audio loop
  bug audible is still present.

## Implementation plan

1. **`Channel<FramePacket<TFrame>>` lifecycle.** Add a
   controller-owned channel pair to `PlaybackController` (or
   `PipelineController`); construct on `LoadAsync`; complete
   on terminal teardown.

2. **Worker loop change.** `RunVideoSinkWorkerAsync`'s
   sink-rent-copy-push tail (lines ~430-460) becomes an emit
   to the video channel. Audio worker symmetrically.

3. **`IPlaybackController.VideoFrames` /
   `AudioBuffers` accessors.** Wrap the channels in
   `FramePipeline<TFrame>` via Crossbar's `AsPipeline()` over
   the channel reader.

4. **Sink adapter.** Add an internal
   `ChannelPipeAdapter.PumpToSinkAsync` (or similar) that
   `PlaybackController.PlayAsync` spawns when an
   `IVideoSink` / `IAudioSink` is registered. Adapter pulls
   from the pipeline, manages the sink's frame pool, calls
   `PresentAsync` / `WriteAsync`.

5. **`ChannelVideoSink` deprecation.** Mark `[Obsolete]` with
   a redirect comment to `controller.VideoFrames`. Don't
   delete; one-release migration window.

6. **Inference-example migration.** Switch
   `FrameFlow.Examples.OnnxInference` from `ChannelVideoSink`
   to `controller.VideoFrames`. Verify against the
   content-capture harness from ADR-0031.

7. **`AvaloniaMulticast` example migration.** Same pattern.
   Verify the multicast still works.

8. **Test verification.** Re-run the content-capture harness
   from ADR-0031 against `test-av-h264-aac.mp4`. All four
   invariants must continue to pass. If any regresses, the
   surgery introduced a regression the lifecycle tests
   couldn't have caught — exactly what ADR-0031 exists for.

9. **Documentation.** Update ADR-0029 status to "Superseded
   by ADR-0032." Update the reading-folder essay on pipeline
   merging with the simpler `controller.AudioBuffers` shape.

Steps 1–4 are the load-bearing surgery (one PR). Steps 5–6
are the migration (one PR per example, or batched). Step 8
is the safety net — runs continuously across all PRs.
