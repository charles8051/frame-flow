# ADR-0036: Decouple the Decoded Media Stream from the Playback Controller

**Status:** Accepted. **Phase 1 + Phase 2 landed.**

> **Note 2026-08-26.** Every `IMasterClock` below is stale. The type shipped
> as `FrameFlow.Graph.IClockSource`, and its shape changed from a synchronous
> `GetPlaybackTime()` pull to `Latest` plus `WaitUntilAsync(target, ct)`. The
> AV-sync operator described here as `PacedAgainst(IMasterClock, ISyncStrategy)`
> is gone outright — both that operator and `ISyncStrategy` were removed, so
> neither name should be carried into new code. Pacing now lives in
> `FrameFlow.Playback.PaceUntil` and, for the video path the session actually
> wires up, `ClockSelectVideoSink`; both await `IClockSource.WaitUntilAsync`
> rather than polling a clock. See ADR-0035's 2026-08-26 note for the full
> before/after.

**Phase 1** (`IDecodedMediaStream` interface, factory, standalone
captioning demo) shipped in commits starting 2026-05-11. Phase 1
proved the decode layer stands alone.

**Phase 2** (the full dissolution) is now also landed. Highlights:

- `PipelineController` shrunk from ~1500 lines to ~450. The three
  worker variants per stream (sink-push / channel-pull / discard)
  collapsed to one sink-pump variant. The `PlaybackCycle` /
  barrier / supersession bookkeeping is deleted.
- AV-sync moved into `PacedAgainst(IMasterClock, ISyncStrategy)`,
  a Crossbar pipeline operator in `FrameFlow.Playback`. Video
  pump is now `stream.Video.PacedAgainst(clock, strategy).Observe(rent
  → copy → present).RunAsync(ct)`.
- Pause/resume reduces to "cancel sink pump CTS / re-spawn pump
  tasks." No gate primitive — gate-then-pull semantics in a
  pipeline operator can leak frames across seek; pump-cancel
  doesn't.
- EOF coordination: stream raises `EndOfStreamReached` after
  draining its decode workers; controller waits for sink pumps to
  drain before firing its own EOF event so state machine
  transitions (Playing → Ended) happen after all frames have been
  presented and counted.
- `IDecodedMediaStream` gained an `EndOfStreamReached` event so
  the controller can observe EOF in pull-mode (no pump) too.
- Loop-restart works: `SeekAsync` recreates the bounded channels
  when an earlier natural EOF completed them, so the new decode
  epoch can write again.
- Master clock selection: when audio is registered but the source
  has no audio stream, fall back to wallclock — the sink's
  sample-counter clock would stay at zero and stall video pacing
  forever otherwise.

**Test verification:** Decoding 119, Playback 362, Audio 43, Media
89, Integration 54 — **667 tests pass**. The ADR-0031 content-capture
harness (AV-sync within tolerance, PCM matches reference, pixels
match reference, EOF ordering, position monotonicity) all green.

**Phase 3 (polishing) also landed:**

- `PipelineDiagnosticsSnapshot` split along the seam: the decode half
  folds under a `Stream: DecodedMediaStreamDiagnosticsSnapshot` field;
  the playback half retains `VideoSink`, `AudioSink`, and
  `VideoFramesDroppedForSync`. Composes naturally with
  `IDecodedMediaStream.GetDiagnostics()` for consumers using the
  stream directly. Consumers updated:
  `DiagnosticsSurfaceIntegrationTests`, `AvaloniaPlayer`'s
  `DiagnosticsReport` + `LiveCounterSampler` + `MainWindow`.
- `ChannelVideoSink` and `AddFrameFlowChannelVideoSink` deleted. The
  inference example was already on `IPlaybackController.VideoFrames`
  (ADR-0032 pull surface); the multicast example same. No in-tree
  consumer remains. ADR-0029 status updated to Superseded.

**Date:** 2026-05-11
**Supersedes:** None directly. Refines and partially supersedes the worker-loop
structure described in ADR-0022, ADR-0026, ADR-0028, ADR-0032.
**Related:** ADR-0020 (lifecycle decoupled from processing logic),
ADR-0022 (long-lived workers with pause gate),
ADR-0023 (hierarchical state machine with channel dispatch),
ADR-0024 (playback controller as public API surface),
ADR-0026 (state-bound worker lifecycle binding),
ADR-0028 (internal layering and ownership cleanup),
ADR-0030 (frame-contract unification with Crossbar),
ADR-0032 (pull-shape playback controller),
ADR-0035 (master clock interface split).

## Context

ADR-0032 landed pull-shape pipeline accessors on `IPlaybackController` —
`controller.VideoFrames` and `controller.AudioBuffers` as
`FramePipeline<T>` — and that unblocked the captioning demo, the
inference demo, and any future consumer that wants decoded frames as a
Crossbar-shaped pull stream rather than a registered push sink. It also
exposed something we'd been carrying for a while: **the playback
controller is doing two unrelated jobs welded together.**

Today, `PipelineController` (the internal heart of `PlaybackController`)
owns all of:

1. **Demux pump** — pulls packets from the format context, routes them
   to the right decoder by stream index. Bypasses
   `IDemuxSession.ReadPacketAsync` for performance.
2. **Video decode worker** — drains the video decoder's enumeration,
   applies AV-sync delay, and either rents-and-pushes to an
   `IVideoSink` or emits to the controller-owned
   `Channel<IVideoFrame>` for pull consumers.
3. **Audio decode worker** — symmetric: drains the audio decoder's
   enumeration and either writes to an `IAudioSink` or emits to the
   controller-owned `Channel<PcmAudioBuffer>`.
4. **Seek atomicity** — `FlushAndRepositionAsync` /
   `FlushWhilePausedAsync`: stops the workers at a barrier, resets
   decoder packet queues, calls `demuxSession.SeekAsync`, resumes.
5. **Pause/resume gate** — `AsyncManualResetEvent` plus barrier counts,
   so that pause is observable as "all workers reached the gate."
6. **Playback cycle bookkeeping** — `PlaybackCycle`, natural-completion
   signaling, supersession, EOF dispatch.
7. **AV sync** — the video worker reads `_audioSink.GetPlaybackTime()`
   (or wallclock when audio-less) and asks `_syncStrategy` how long
   to wait before the next emission.
8. **Sink presentation** — when sinks are registered, the workers do
   rent-and-copy into the sink-owned pool and call `PresentAsync` /
   `WriteAsync`.

The first four are **decoding-pipeline** concerns: read packets, hand
them to decoders, drain decoded output, support seek. They have nothing
to do with playback — they would be exactly the same in a transcoder, a
batch ML pipeline, or a non-realtime media tool.

The last four are **playback** concerns: pace frames against a master
clock, dispatch to render targets, run a state machine that says "we're
playing" or "we're paused" or "we've reached EOF."

Mashing both responsibilities together is what made the captioning demo
awkward: to consume `PcmAudioBuffer` packets for Whisper, you have to
spin up an `IPlaybackController`, sequence through `LoadAsync` →
`PlayAsync`, and live with the AV-sync delay applied to your audio.
There is no "I just want decoded frames, pull-shaped, please" path. The
controller is the only door.

It also explains the residual friction in `PipelineController.cs`: 1,500
lines, three worker variants (sink-push / channel-pull / discard) per
stream type, a cycle/barrier system that exists almost entirely to
support the pause-as-quiescence semantics, and the AV-sync inline in the
worker loop rather than expressed as a pipeline operator.

The pull-shape refactor (ADR-0032) got us halfway. This ADR finishes
the journey: it carves out the decoding pipeline as a Crossbar-shaped
first-class type, and reshapes `PlaybackController` into a thin
composition layer on top.

### What "Crossbar-shaped" means here

A Crossbar-shaped subsystem exposes its output as `FramePipeline<T>`,
not as a registered callback. Consumers compose with `Transform`,
`Observe`, `Broadcast`, `ToSink`, etc. State is local to each stage,
not shared across a controller. The decode side has no business knowing
about playback state, sinks, or master clocks; the playback side has no
business reaching into demux internals.

Today's `PipelineController` has both sides reaching across the seam.
The video worker calls `_audioSink.GetPlaybackTime()` per frame. The
seek path reaches into `_videoDecoder.Flush()` and `_demuxSession`
through the pipeline. The barrier/gate exists to coordinate decode-side
workers with playback-side pause commands.

We want, instead:

- A `DecodedMediaStream` that owns the decode workers and exposes
  `Video: FramePipeline<IVideoFrame>` and
  `Audio: FramePipeline<PcmAudioBuffer>`, with `SeekAsync` as its only
  control method.
- A `PlaybackController` that *consumes* those pipelines, applies AV
  sync as a pipeline operator before sinks, runs the state machine,
  and forwards seek to the stream.

## Decision

Introduce `IDecodedMediaStream` as a first-class type in
`FrameFlow.Decoding`. Move all demux+decode+channel plumbing from
`PipelineController` into a concrete `DecodedMediaStream`. Slim
`PipelineController` (and through it `PlaybackController`) to be a
state-machine + AV-sync + sink-pump layer that composes a stream.

### 1. New public type — `IDecodedMediaStream`

Lives in `FrameFlow.Decoding`. The contract is small, deliberately so;
this is the API a Crossbar-style consumer wants.

```csharp
namespace FrameFlow.Decoding;

/// <summary>
/// A decoded media stream: demux + decode behind a single
/// pull-shape interface. Exposes video and audio as
/// <see cref="FramePipeline{TFrame}"/> outputs. Has no notion of
/// play/pause — consumers pace decode by pulling at their own rate;
/// bounded channels backpressure into the demux pump. Has one
/// control method, <see cref="SeekAsync"/>, which atomically
/// flushes decoders and repositions the demuxer.
/// </summary>
public interface IDecodedMediaStream : IAsyncDisposable
{
    /// <summary>Metadata for the loaded media.</summary>
    MediaInfo Info { get; }

    /// <summary>
    /// Decoded video frames as a pull-shape pipeline. Empty if the
    /// source has no video stream.
    /// </summary>
    FramePipeline<IVideoFrame> Video { get; }

    /// <summary>
    /// Decoded audio buffers as a pull-shape pipeline. Empty if the
    /// source has no audio stream.
    /// </summary>
    FramePipeline<PcmAudioBuffer> Audio { get; }

    /// <summary>
    /// Atomically flushes decoder buffers and repositions the
    /// demuxer to <paramref name="position"/>. After the returned
    /// task completes, the next packet pulled from <see cref="Video"/>
    /// or <see cref="Audio"/> has PTS at or after the seeked position
    /// (modulo keyframe alignment).
    /// </summary>
    /// <remarks>
    /// Seek is serialized — a second call queues behind the first.
    /// Pull consumers may observe a brief gap as the stream
    /// repositions; the gap is bounded by decoder flush + demux
    /// seek + first keyframe decode.
    /// </remarks>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Coherent diagnostics rollup for the decode subsystems
    /// (demux, decoders, channel depths). Cheap; safe to call from
    /// a UI timer.
    /// </summary>
    DecodedMediaStreamDiagnosticsSnapshot GetDiagnostics();
}
```

### 2. Concrete `DecodedMediaStream`

Internal class in `FrameFlow.Decoding`. Constructed by an
`IDecodedMediaStreamFactory` (public surface), which is the equivalent
of today's `IPlaybackSessionFactory` for the decode side.

Owns:

- `DemuxSession` (the lifecycle owner)
- Optional `VideoDecoder` / `AudioDecoder` (lifecycle owner)
- `DecodingPipeline` (already exists; runs the demux pump task)
- A bounded-1 `Channel<IVideoFrame>` and a bounded-1
  `Channel<PcmAudioBuffer>` exposed as pipelines via `AsPipeline()`
- Two long-lived decode worker tasks, started in the constructor
  (or by an explicit `StartAsync`, but constructor is fine for
  symmetry with how Crossbar pipelines work — they're "live" as soon
  as the object exists)
- A `_seekLock` (`SemaphoreSlim(1, 1)`) serializing `SeekAsync` calls
- A `_decodeCts` that scopes the current "decode epoch"; cancel +
  restart on seek

Workers are simple — much simpler than today's three-variant worker
zoo because there's no sink branch and no AV-sync:

```csharp
private async Task RunVideoDecodeAsync(CancellationToken epoch)
{
    await foreach (var frame in _videoDecoder!.DecodeAsync(epoch))
    {
        // No AV-sync here. No sink rent/copy. Just emit.
        // Bounded(1) Wait channel paces decode at consumer rate.
        bool transferred = false;
        try
        {
            await _videoChannel.Writer.WriteAsync(frame, epoch);
            transferred = true;
        }
        finally
        {
            if (!transferred) frame.Dispose();
        }
    }
}
```

That's the entire video worker for the new world. Audio is symmetric.

### 3. Seek is the only control method

`SeekAsync` is the only verb. There is no `Play`, `Pause`, `Stop`, or
`Resume` on `IDecodedMediaStream`. Reasons:

- **Pause is just "don't pull."** A consumer that wants to pause stops
  iterating its `await foreach` over the pipeline. The bounded(1)
  channel fills, the decode worker blocks on its next `WriteAsync`,
  the demux pump's packet queue fills, the pump blocks. The whole
  pipeline is naturally paused. No barrier, no gate, no cycle count.

- **Resume is just "pull again."** Resuming iteration drains the
  channel; the worker proceeds; everything unblocks.

- **Stop is dispose.** End-of-stream is signaled by the pipeline
  iteration ending (writer completed). Disposal cancels in-flight
  iterations.

This kills ~600 lines of `PipelineController.cs`. The barrier, the
gate, the `PlaybackCycle` class, the cycle/supersession bookkeeping —
all gone, on the decode side. The state they encoded ("are we paused?
is this an EOF or a pause-flush?") is now answered by trivial channel
semantics.

### 4. Seek atomicity without the barrier

The current seek path is:

1. `PauseWorkersAsync` — cancel cycle, wait barrier.
2. Reset decoder packet queues.
3. Flush decoders.
4. Call demuxer's `SeekAsync` (the reposition action).
5. `ResumeWorkers`.

Under the new shape:

1. `await _seekLock.WaitAsync(ct)` — serialize seek vs. seek.
2. `_decodeCts.Cancel()` — kill the current decode epoch. The
   demux pump and decode workers observe cancellation and exit
   their `await foreach` cleanly.
3. `await Task.WhenAll(...)` on the worker tasks. (They tear down
   in bounded time because the cancellation token is cancelled.)
4. Drain any frames sitting in `_videoChannel` / `_audioChannel`
   (dispose them — they're pre-seek).
5. `_videoDecoder.ResetPacketQueue()` / `Flush()` (symmetric for
   audio).
6. `await _demuxSession.SeekAsync(position, ct)`.
7. New `_decodeCts`, new worker tasks, new pump task, fresh
   channels (the existing channels stay; new epoch).
8. Release `_seekLock`.

This is more *operations* than the barrier version, but each operation
is local, has clear semantics, and uses only standard async primitives.
No custom barrier, no `PlaybackCycle`, no supersession state.

The pull consumer sees one continuous iteration through `Video` /
`Audio`. The seek looks like a brief stall (frames stop arriving for
~20-50ms), then frames at the new position start flowing.

### 5. `PlaybackController` becomes a thin composition

`PipelineController` (and the public `PlaybackController` it lives
inside) reshape as:

```csharp
internal sealed class PipelineController : IAsyncDisposable
{
    private readonly IDecodedMediaStream _stream;
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly IMasterClock _clock;          // ADR-0035
    private readonly ISyncStrategy _syncStrategy;  // ADR-0003

    // The composed video pipeline: stream.Video, with AV-sync applied.
    private readonly FramePipeline<IVideoFrame> _pacedVideo;

    // Sink pump tasks (one per registered sink). Each task does
    // `await foreach` over the composed pipeline and presents/writes
    // to its sink. No copying or pooling inside this class — sink
    // adapters handle their own pool rent.
    private Task? _videoPumpTask;
    private Task? _audioPumpTask;

    private CancellationTokenSource? _runCts;
    private readonly AsyncManualResetEvent _gate = new(initiallySet: false);
}
```

`PlayAsync` starts (or unpauses) the sink pumps. `PauseAsync` closes
the gate, which gates the pump tasks at a `_gate.WaitAsync` between
each frame's pull and presentation. `SeekAsync` forwards to
`_stream.SeekAsync(position)` after pausing.

The state machine (8 primary states, 3 seeking states, 2 repeat modes)
stays exactly as ADR-0023 / ADR-0024 describe — that's not the
problem. The problem was that the *implementation* of pause/resume/
seek/EOF was tangled into the decode plumbing. With decode lifted out,
the state machine retains its expressive surface and the implementation
behind each transition becomes a one-liner.

### 6. AV-sync as a pipeline operator

The AV-sync logic that lived inline in `RunVideoSinkWorkerAsync` and
`RunVideoChannelWorkerAsync` moves into a Crossbar operator:

```csharp
public static FramePipeline<IVideoFrame> PacedAgainst(
    this FramePipeline<IVideoFrame> source,
    IMasterClock clock,
    ISyncStrategy syncStrategy)
{
    return source.Transform(async (packet, ct) =>
    {
        var delay = syncStrategy.GetVideoDelay(packet.Frame.Pts, clock.GetPlaybackTime());
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
        return packet;
    });
}
```

That's the whole operator. The video sink pump is then:

```csharp
_pacedVideo = _stream.Video.PacedAgainst(_clock, _syncStrategy);
// ...
await foreach (var packet in _pacedVideo.WithCancellation(ct))
{
    var poolFrame = await _videoSink.FramePool.RentAsync(...);
    // copy, present, dispose
}
```

This is what "Crossbar-shaped" means concretely: AV sync is a
*transform* on the frame stream, expressible as one operator, applied
by composition. Not a per-worker procedural detail.

### 7. Pull consumers bypass the playback layer entirely

The big win for users. The captioning demo today:

```csharp
var controller = await builder.BuildPlaybackControllerAsync(...);
await controller.LoadAsync(source);
await controller.PlayAsync();
await foreach (var audio in controller.AudioBuffers...)
```

becomes:

```csharp
await using var stream = await factory.CreateAsync(source);
await foreach (var audio in stream.Audio...)
```

No state machine to drive. No play/pause to remember. No "are sinks
registered" branching. Just decoded audio as a pipeline.

Equivalently, an inference pipeline that wants decoded video without
pacing or sinks:

```csharp
await using var stream = await factory.CreateAsync(source);
await foreach (var frame in stream.Video.Transform(YoloDetect))
```

The `PlaybackController` exists *only* when a consumer wants its
extra services: state machine, AV-sync pacing, repeat/loop, sink
delivery, position tracking. Most demos in this repo want all of
that; a growing minority don't.

### 8. Diagnostics surface stays coherent

ADR-0034 introduced per-subsystem diagnostics snapshots composed into a
pipeline-level rollup. Under the new shape:

- `IDecodedMediaStream.GetDiagnostics()` returns
  `DecodedMediaStreamDiagnosticsSnapshot` (demux + video decoder +
  audio decoder + channel depths). This composes the per-subsystem
  snapshots that already exist in `FrameFlow.Decoding.Diagnostics`.
- `IPlaybackController.GetDiagnostics()` returns
  `PlaybackDiagnosticsSnapshot` (state + position + the decoded-stream
  snapshot + sink snapshots + A/V drift). It composes the decoded
  stream's snapshot and adds playback-layer concerns.

`PipelineDiagnosticsSnapshot` (ADR-0034) gets split along the same
seam: the decode half moves to `DecodedMediaStreamDiagnosticsSnapshot`,
the playback half stays on `PlaybackDiagnosticsSnapshot`. Consumers
that want the controller-level rollup keep using
`controller.GetDiagnostics()`; consumers using the stream directly
get a smaller, focused snapshot.

## Consequences

### Positive

- **Decode pipeline becomes a first-class, composable type.**
  `IDecodedMediaStream` is the type a transcoder, an inference
  pipeline, a captioning agent, or a recording app wants. None of
  them need a playback controller; today all of them have to bring
  one along.

- **`PipelineController` shrinks dramatically.** The 1,500-line file
  becomes ~400 lines: state machine glue, sink pumps, AV-sync
  composition, seek-and-pause forwarding. Three worker variants per
  stream type collapse to one sink-pump variant. No cycle/barrier
  ceremony.

- **AV-sync becomes a pipeline operator.** `.PacedAgainst(clock,
  strategy)` is the whole thing. Testable in isolation. Reusable by
  any consumer that wants the same pacing semantics without the rest
  of the controller.

- **Seek atomicity uses standard primitives.** `SemaphoreSlim` for
  serialization, `CancellationTokenSource` for epoch boundary,
  `Channel<T>` for backpressure. No custom `PlaybackCycle` /
  supersession state.

- **Pause becomes free.** A pull consumer pauses by not pulling.
  The bounded channel backpressures naturally. The state-machine
  layer adds an explicit gate when needed (sink pumps need to know
  "stop presenting"), but the decode layer is unaware.

- **The shape extends cleanly to live sources.** A live capture
  source (camera + microphone, RTSP, screen capture) implements
  `IDecodedMediaStream` directly without any playback-state
  apparatus. Today this would require a fake `IPlaybackController`
  or a parallel API.

- **Test layering improves.** Decode-only tests use
  `IDecodedMediaStream` and assert on frame counts, PTS sequences,
  seek correctness — without involving the playback state machine.
  Playback-only tests can use a fake `IDecodedMediaStream` that
  emits scripted frames and assert on state transitions, AV-sync
  delays, sink calls — without involving FFmpeg.

### Negative

- **Three stable ADRs are partially superseded.** ADR-0022 (long-lived
  workers with pause gate) — the gate semantics shrink to the
  playback layer; the decode-side gate is gone. ADR-0026 (state-bound
  worker lifecycle binding) — workers are no longer bound to playback
  states; they're bound to the stream's lifetime. ADR-0028 (internal
  layering) — the seam moves. Each of these ADRs gets a status note
  pointing here.

- **One round of breaking changes for direct users of
  `PipelineController` internals.** This is internal so the blast
  radius is bounded, but the worker tasks, cycle bookkeeping, and
  barrier methods all change shape. Tests that introspected those
  internals must update.

- **`PlaybackCycle` and its supersession protocol go away.** That
  protocol was load-bearing for one specific case: "natural EOF
  from a worker that completed a stream that was about to be
  superseded by a seek." Under the new shape, that race is
  expressed by `SeekAsync` taking `_seekLock` and cancelling the
  decode epoch — a frame emitted just before the cancellation is
  drained by the seek logic before the new epoch starts. The race
  has the same resolution; the code expressing it is gone.

- **Migration effort.** The current `PipelineController` is the
  trunk of the playback subsystem. The refactor must preserve every
  observable behavior: state transitions fire in the right order,
  position ticks at the right cadence, EOF dispatches at the right
  moment, sinks receive frames identical to before. The integration
  tests from ADR-0031 (content-comparing capture sinks) are the
  primary safety net.

### Neutral

- **`ChannelVideoSink` / push-shape sinks unchanged.** Existing
  consumers of `IVideoSink` and `IAudioSink` keep working — the
  sink pumps inside `PlaybackController` present to them just as
  the workers did before. The seam moves; the surface doesn't.

- **`IMasterClock` from ADR-0035 fits naturally.** The clock is
  consumed by the AV-sync pipeline operator, not by the decode
  layer. Decode is clock-agnostic. The clock split that ADR-0035
  did at the audio-sink boundary is now also reflected at the
  decode-vs-playback boundary.

- **Existing examples that use `controller.VideoFrames` keep
  working.** The pull accessors stay on `IPlaybackController` (they
  delegate to the underlying stream). Examples can migrate to
  `IDecodedMediaStream` directly when they don't need the
  controller, but they're not forced to.

## Alternatives considered

### A. Leave it alone — the architecture works

The current architecture works in the sense that tests pass and demos
run. But every advanced demo paid the same tax: bring a
`PlaybackController` along even when you don't need playback. The
captioning demo had to fake-out the AV-sync delay (Whisper doesn't
care). The inference demo wires a `ChannelVideoSink` and works around
the controller's coupling. Future demos (live sources, transcoding,
DAW-style audio routing) would all do the same.

Rejected because the cost of *not* refactoring grows with every new
demo, while the cost of refactoring is bounded — and is right-sized
for the moment we're in (foundations stabilizing, advanced demos
queued).

### B. Add an `IDecodedMediaStream` accessor without removing it from `PlaybackController`

Lighter touch: keep `PipelineController` as-is; expose an
`IDecodedMediaStream` view that internally delegates back to the
controller's channels. Migration is purely additive.

Rejected because the duplication this ADR is supposed to *remove*
(decode plumbing welded to playback) would still be there. The
controller would still own the demux pump, the workers, the barrier,
the cycle bookkeeping. We'd have two ways to express the same idea —
the controller's pull accessors and the new stream type — both
backed by the same code, neither one the simplification.

### C. Make `PlaybackController` itself implement `IDecodedMediaStream`

Inverted layering: `PlaybackController : IDecodedMediaStream`, so
consumers that want the decoded stream just use the controller as
that interface.

Rejected because it doesn't simplify implementation — the controller
still owns everything — and it lies about the controller's role to
callers who only want decode. The whole point is for "I want decoded
frames" consumers to *not* drag a state machine in.

### D. Move decode to a separate process

The truly orthogonal version: decode runs in a worker process,
playback runs in the host, they communicate over IPC.

Rejected as far overshooting the current need. The ADR-0036 shape
*permits* that future (the IPC boundary is a natural fit for
`IDecodedMediaStream`'s pull surface) without requiring it.

## Implementation plan

Sequenced for incremental verification. Each step lands as a separate
commit; the integration test suite passes after each.

1. **Define `IDecodedMediaStream` and snapshot types.** Public surface
   in `FrameFlow.Decoding`. No behavior change; this step compiles
   but isn't wired.

2. **Build `DecodedMediaStream` concrete.** Internal class in
   `FrameFlow.Decoding`. Lift the demux-pump task, video decode
   worker, audio decode worker, channel construction, and seek
   serialization out of `PipelineController`. Add an
   `IDecodedMediaStreamFactory` that builds it from an `IMediaSource`
   (mirrors today's `IPlaybackSessionFactory`).

3. **Add the AV-sync pipeline operator.** `PacedAgainst(IMasterClock,
   ISyncStrategy)` extension method on `FramePipeline<IVideoFrame>`.
   Lives in `FrameFlow.Playback`. Unit-testable in isolation.

4. **Refactor `PipelineController` to wrap `IDecodedMediaStream`.**
   Sink pumps replace the three worker variants. The state machine
   stays. The gate semantics tighten to "pause sink presentation,"
   not "pause everything including decode." Pause now means "don't
   pull more frames from the stream"; the stream backpressures the
   pump naturally.

5. **Forward `controller.VideoFrames` / `AudioBuffers` to the
   stream.** The pull accessors stay on `IPlaybackController` (ADR-
   0032 contract preserved); they now delegate to
   `_stream.Video` / `_stream.Audio`.

6. **Diagnostics surface split.**
   `DecodedMediaStreamDiagnosticsSnapshot` composes the demux/decoder
   snapshots. `PlaybackDiagnosticsSnapshot` composes the stream
   snapshot plus the playback-layer snapshots (state, sinks, drift).

7. **Tests: full suite + ADR-0031 capture harness.** Every existing
   integration test passes. The capture harness's four invariants
   (PCM correctness, pixel correctness, EOF ordering, position
   monotonicity) all hold.

8. **One new test: `IDecodedMediaStream` used standalone.** Open a
   test file, iterate `stream.Video` and `stream.Audio` in parallel,
   assert frame counts and EOF. No `PlaybackController` involved.
   This is the proof that the new layer stands alone.

9. **Migrate the captioning example.** Drop the `PlaybackController`,
   use the stream directly. Verify Whisper output matches.

10. **ADR status updates.** ADR-0022, ADR-0026, ADR-0028, ADR-0032
    get status notes pointing here. ADR-0032 is mostly *refined* (the
    pull accessors stay; the implementation moves); the others are
    *partially superseded* in the way described above.

Step 1-3 are independent and can land together. Step 4 is the load-
bearing surgery. Steps 5-7 are mechanical. Steps 8-10 are the
verification, the migration, and the bookkeeping.

## References

- ADR-0020: Lifecycle decoupled from processing logic (the precedent
  for this kind of separation, applied here to the decode/playback
  split rather than the lifecycle/processing split)
- ADR-0028: Internal layering and ownership cleanup (the seam this
  ADR moves)
- ADR-0030: Frame-contract unification with Crossbar (the substrate
  that makes pipeline-as-surface viable)
- ADR-0032: Pull-shape playback controller (the half-step that
  exposed the entanglement)
- ADR-0035: Master clock interface split (the matching split on the
  audio side — `IMasterClock` extracted from `IAudioSink` so the AV-
  sync operator can take the narrow interface)
- `src/FrameFlow.Playback/PipelineController.cs` — the trunk that
  shrinks
- `src/FrameFlow.Decoding/DecodingPipeline.cs` — the partial concrete
  that grows into `DecodedMediaStream`
