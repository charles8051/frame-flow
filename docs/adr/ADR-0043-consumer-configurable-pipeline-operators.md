# ADR-0043: Consumer-Configurable Pipeline Operators

## Status

**Accepted (experimental); type references partially superseded.**
The chosen shape is the first iteration
of a consumer-facing pipeline-interception API on the Layer-2 player
builder. The mechanism (a single configurator delegate per stream
that receives the playback pipeline and returns a transformed
pipeline) is intentionally narrow so the call site can evolve. The
API surface is not frozen — refinements to the configurator shape,
introduction of additional seams (e.g. pre-pacing access), or a
deeper fluent surface as proposed in
`docs/IDEAL_AVALONIA_PLAYER.md` may supersede or extend this ADR.

> **Update 2026-05-15 / 16 (Crossbar ADR-0010 / ADR-0012).** Code
> examples below reference `IFrameSink<IVideoFrame>` and
> `SupportedMemoryDomains` — both have since been removed from the
> Crossbar substrate. The configurator mechanism this ADR
> introduced is unchanged. Current type shapes:
> - "Bespoke `IFrameSink<IVideoFrame>` for YOLO inference" →
>   `BackgroundConsumer<IVideoFrame>` (formerly `BackgroundFrameSink`)
>   wired with `.ToSink(backgroundConsumer.Consumer)`.
> - `IVideoSink.SupportedMemoryDomains` → gone; conversions are
>   explicit `Transform` operators at the boundary.
> - Sinks pass their `Consumer` projection to terminators rather
>   than passing the sink itself.
>
> See Crossbar ADR-0010 for the consumer-function unification and
> Crossbar ADR-0012 for the explicit-conversion model.
>
**Date:** 2026-05-13
**Supersedes:** None.
**Related:**
- ADR-0024 (playback controller as public API surface)
- ADR-0030 (Crossbar frame substrate)
- ADR-0032 (pull-shape playback controller)
- ADR-0036 (decoded media stream decoupled from playback)
- ADR-0037 (pixel pipeline operators)
- ADR-0038 (memory-domain pipeline operators)
- ADR-0039 (Whisper transcription operator)
- ADR-0042 (pipeline error handling via fluent operators)
- `docs/IDEAL_AVALONIA_PLAYER.md` (target consumer surface)

## Context

After the Avalonia out-of-box refactor (`FrameFlowVideoView`
self-sufficient, `WithAvaloniaVideoView` / `WithOpenAlAudio`
extensions, `ObserveOnUiThread`), the common-case player call site
collapses to:

```csharp
_player = await FrameFlowPlayer.Open(path)
    .WithAvaloniaVideoView(VideoView)
    .WithOpenAlAudio(loggerFactory)
    .BuildAsync();
```

What this surface does **not** give consumers is any way to hook the
pipeline that flows from the decoder to the sink. Crossbar's
operator vocabulary (`Tee`, `Broadcast`, `Observe`, `Transform`,
`Enrich`, `Catch`, plus FrameFlow's `ConvertPixelFormat`, `Resize`,
`Resample`, `TranscribeWithWhisper`, `DetectWith`) is rich enough to
express most of the example scenarios — overlay captions, branch
inference, record while presenting, sample for VU meters — but those
operators are only reachable today by dropping out of the Layer-2
builder entirely and constructing the playback stack manually
(`IDecodedMediaStreamFactory` → `IPlaybackControllerFactory` →
manual sink wiring). That defeats the point of the builder.

The seam exists inside `PipelineController.RunVideoPumpAsync` and
`RunAudioPumpAsync`. Today they look like:

```csharp
await _stream.Video
    .PacedUntil(_clockSource)
    .Observe(_ => /* counter */ ValueTask.CompletedTask)
    .ToSinkAsync(_videoSink, SinkErrorPolicy.SkipFrame, …);
```

Everything between `_stream.Video` and `ToSinkAsync` is currently
hard-coded. We want consumers to inject operators at that seam
without having to know how `PipelineController` is composed.

## Decision

### Surface

Add two delegate types in `FrameFlow.Media` (the shared contracts
assembly) and two corresponding builder methods on `IPlayerBuilder`:

```csharp
namespace FrameFlow.Media;

public delegate FramePipeline<IVideoFrame> VideoPipelineConfigurator(
    FramePipeline<IVideoFrame> pipeline);

public delegate FramePipeline<PcmAudioBuffer> AudioPipelineConfigurator(
    FramePipeline<PcmAudioBuffer> pipeline);
```

```csharp
namespace FrameFlow.Player;

public interface IPlayerBuilder
{
    // existing surface …

    IPlayerBuilder ConfigureVideoPipeline(VideoPipelineConfigurator configure);
    IPlayerBuilder ConfigureAudioPipeline(AudioPipelineConfigurator configure);
}
```

A consumer who wants to overlay captions, fork off an inference
branch, resize before display, or simply observe every frame writes:

```csharp
_player = await FrameFlowPlayer.Open(path)
    .WithAvaloniaVideoView(VideoView)
    .ConfigureVideoPipeline(p => p
        .ConvertPixelFormat(PixelFormat.Bgra32)
        .OverlayCaptions(captionStream))
    .ConfigureAudioPipeline(p => p
        .Tee(recordingSink, buf => buf.AddRef()))
    .BuildAsync();
```

### Semantics

1. **The configurator runs after pacing.** It receives the same
   pipeline the sink pump would have terminated — paced against the
   playback clock and instrumented with the frames-presented
   counter. Operators added by the configurator therefore run at
   *display rate*, not decode rate. This is the right default for
   overlays, recording, and UI-facing tap branches.

2. **The configurator's returned pipeline is the terminal pipeline.**
   The player still calls `ToSinkAsync` on the result. Consumers
   should not call `ToSinkAsync` themselves inside the configurator
   — if they do, the sink registered via `WithVideoSink` /
   `WithAudioSink` will receive no frames, which is almost certainly
   a bug.

3. **Last call wins.** Calling `ConfigureVideoPipeline` twice
   replaces the previous configurator. Multi-step composition
   belongs inside the lambda, where Crossbar's fluent surface makes
   it natural. This keeps order-dependent semantics out of the
   builder.

4. **No configurator ≡ today's behavior.** When a consumer doesn't
   call `Configure*Pipeline`, the pump runs exactly as it does
   today. Backwards compatible.

5. **Frame ownership unchanged.** The configurator's pipeline obeys
   the same Crossbar contracts: operator-owns-upstream, `AddRef`
   for branching, `Dispose` on the way out. The player neither
   adds nor removes refs around the seam.

### Plumbing

The configurators are optional dependencies registered as DI
singletons by the `PlayerBuilder`:

```csharp
// PlayerBuilder.BuildAsync (sketch)
if (_videoConfigurator is not null)
    services.AddSingleton(_videoConfigurator);
if (_audioConfigurator is not null)
    services.AddSingleton(_audioConfigurator);
```

`ServiceProviderPlaybackSessionFactory.CreateSession` resolves them
(`GetService<VideoPipelineConfigurator>()`) and forwards them
through `PlaybackSession` into `PipelineController`. The pump
methods apply them:

```csharp
var pipeline = _stream.Video
    .PacedUntil(_clockSource)
    .Observe(_ => /* counter */ ValueTask.CompletedTask);

if (_videoConfigurator is not null)
    pipeline = _videoConfigurator(pipeline);

await pipeline.ToSinkAsync(_videoSink, SinkErrorPolicy.SkipFrame, …);
```

Both the DI-hosted factory (`ServiceProviderPlaybackSessionFactory`)
and the manual factory (`PlaybackSessionFactory`) accept the
configurators as optional constructor parameters defaulted to
`null`. Consumers using only the manual factory continue to work
without code changes.

## Alternatives considered

### A. Pre-pacing seam

Pass `_stream.Video` directly to the configurator and let it apply
pacing if it wants. Rejected for v1: most consumers want pacing and
adding `.PacedUntil(clock)` to every lambda is friction. Consumers
who need decode-rate access already have `IPlaybackController.VideoFrames`
as an escape hatch (per ADR-0032). If the experiment shows raw
access is regularly needed, add a sibling `ConfigureRawVideoPipeline`
seam later.

### B. Operator-list shape

`IPlayerBuilder.AddVideoOperator(IPipelineOperator<IVideoFrame>)`,
called multiple times to build a chain. Rejected: order-dependent
behavior is hard to read at the call site, and Crossbar's fluent
surface already composes operators inline. The lambda shape gives
the same expressiveness with fewer concepts.

### C. Full pipeline takeover

`IPlayerBuilder.WithVideoPipeline(Func<FramePipeline<IVideoFrame>, Task>)`
— the consumer's function consumes the pipeline however it wants,
the builder doesn't register a sink at all. The IDEAL doc sketches
this shape. Rejected for v1: too big a leap from today's
sink-centric surface, and the sink-registration model is the right
abstraction for the common case. The configurator shape is forward-
compatible — a future `WithVideoPipeline` can layer on top.

### D. Inheriting / overriding `PipelineController`

A composition-over-inheritance violation and tightly couples
consumers to internal types. Rejected on principle.

### E. Multiple configurators that compose

Calling `ConfigureVideoPipeline` twice composes (each operator
wraps the previous). Rejected: composition order becomes call
order, which is invisible at the use site, and consumers will
expect operators to read top-to-bottom. The replacing semantic is
clearer; in-lambda composition handles the multi-operator case.

## Consequences

### Positive

- The 80%-case advanced demos (caption overlay, inference branch,
  recording sink, VU meters) become one-line builder additions
  rather than full pipeline-stack reconstructions.
- The Layer-2 surface stays narrow: two methods, two delegate
  types, no new concepts.
- The configurator is purely additive — no existing call site
  needs to change.
- The seam location (inside `PipelineController`) is testable in
  isolation; doubles for `VideoPipelineConfigurator` can be
  supplied without touching the builder.

### Negative

- Adds two new types and two new builder methods to public API
  surface. The Layer-2 player surface grows; the rest of the
  codebase is unaffected.
- Two methods on a public interface (`IPlayerBuilder`) is a
  breaking change for any external implementer. We have none, but
  it's worth noting the API stability cost.
- The "configurator runs after pacing" rule needs to be discoverable.
  Captured in XML doc comments and surfaced in the example.

### Neutral

- The audio sink lifecycle gap (sinks registered via
  `Func<IAudioSink>` not disposed by the player) is unchanged.
  Out of scope for this ADR.
- `IPlaybackController` remains the power-user escape hatch
  (ADR-0024 / ADR-0032). The configurator augments but does not
  replace it.

## Future directions

- **Raw seam.** If consumers regularly want decode-rate access,
  add `ConfigureRawVideoPipeline` / `ConfigureRawAudioPipeline`
  alongside.
- **Per-sink fan-out.** A future `Broadcast`-shaped builder method
  could replace a single sink with several without forcing the
  consumer to write the `Tee` plumbing.
- **Pipeline-first builder.** Per the IDEAL doc, a future
  `WithVideo(video => video.…ToSink(view))` shape could replace
  the sink-then-configurator model. The configurator API
  established here is forward-compatible — it can become a thin
  shim or be replaced wholesale.

## Lessons from consumer refactors

Three example refactors landed against the new configurator surface
on 2026-05-13, each exercising a different shape:

- **`FrameFlow.Examples.LiveCaptioning`** — cross-stream join
  (PCM → Whisper → caption overlay on video) plus inline YOLOv8
  object detection. The first non-trivial consumer of the
  configurators.
- **`FrameFlow.Examples.OnnxInference`** — single-stream YOLOv8
  inference with skip-while-busy worker pattern, standard
  `AvaloniaVideoSink` for rendering, dedicated detection overlay
  for boxes. Validates the configurator API for the
  "presenter + side-task inference" shape.
- **`FrameFlow.Examples.AvaloniaMulticast`** — single decoded
  stream fanned out to three independent `IFrameSink<IVideoFrame>`
  panes via Crossbar's `Broadcast` inside the video configurator.
  First demo where the configurator's returned pipeline is
  terminal-shaped.

Together these surface four friction points worth tracking before
the next iteration of the API.

### Friction 1 — slow-or-async work needs side-task plumbing

The configurator pair treats audio and video as independent
streams that run synchronously through their respective pumps.
Anything that can't be done inline (cross-stream joins,
inference too slow for display rate, anything that needs to
preserve frame rate while doing slower work) needs a hand-rolled
side-task escape hatch. Two manifestations so far:

- **Cross-stream coordination (LiveCaptioning).** The audio →
  Whisper → caption-pipeline bridge needs:
  - A bounded `Channel<PcmAudioBuffer>` between the audio
    configurator and the caption pipeline.
  - A hand-rolled `ReadAllFrames(ChannelReader<T>, ct).AsPipeline()`
    adapter to lift the channel into a Crossbar pipeline.
  - An audio `Observe` callback that manually `AddRef`s each PCM
    buffer and drops the AddRef on `Channel.Writer.TryWrite`
    rejection.

- **Skip-while-busy inference (OnnxInference).** The video pump
  runs at display rate; YOLOv8 detection runs at inference rate
  (much slower on CPU). Inlining `DetectWith` would backpressure
  the pump and degrade playback. The demo uses an `Interlocked`
  flag inside `Observe` to gate a `Task.Run(...)` inference
  worker, drops new frames while the worker is busy, and posts
  results to the UI thread when ready.

Same underlying need — "tap the stream, do work elsewhere, route
results back" — different concrete pattern (channel vs flag).
The friction is that *both* patterns are hand-rolled by the
consumer. Crossbar's `Tee` operator helps when the side branch
terminates at an `IFrameSink<T>`, but there is no built-in
"pipeline-source" sink — i.e. a sink whose downstream face is a
`FramePipeline<T>` the consumer can compose against.

Three candidate follow-ups:

- **`PipelineBridge<T>`** — a small Crossbar primitive that
  exposes both an `IFrameSink<T>` *and* a `FramePipeline<T>`
  rooted on the same buffered channel. Captures the channel-shaped
  pattern as a single named concept. Would simplify
  LiveCaptioning's PCM bridge.
- **`OnEveryFrame(callback, busyPolicy)`** — a builder
  convenience that wraps the skip-while-busy + AddRef +
  side-task + dispose pattern. Would simplify OnnxInference's
  detection worker.
- **Joint configurator** — a higher-level builder method
  (`ConfigurePipelines(Func<AvPipelines, AvPipelines>)`) that
  receives both audio and video pipelines and returns both
  terminated pipelines. Consumers wire the join inside the
  lambda without crossing two configurator seams.

The cross-stream demos to watch as additional data points:
audio-reactive video effects, joint A/V inference, lip-sync
diagnostics. The slow-inference shape will recur with every
heavy operator (Whisper, Stable Diffusion, larger detectors).

### Friction 2 — implicit pixel-format coupling

Two demos (LiveCaptioning, OnnxInference) now need to insert
`.ConvertPixelFormat(PixelFormat.Bgra32)` before any pixel-domain
operator or sink that requires BGRA32 (YOLOv8 detector, Avalonia
sink). Today neither operators nor sinks advertise their required
format — consumers have to know each side's intake and bolt the
converter in by hand.

Two ways to address it later:

- **Sink-advertised intake format.** Have
  `IVideoSink.SupportedMemoryDomains` grow a sibling
  `SupportedPixelFormats` and let the player insert an implicit
  `ConvertPixelFormat` when the configurator's output type doesn't
  match. Cost: another field on the sink contract, and an
  ambiguity about where conversion happens.
- **Operator-advertised intake format.** Have operators like
  `DetectWith` accept any format and insert their own conversion
  internally. Cost: each operator has to know about the conversion
  surface, and conversion may happen multiple times in a chain.

Neither is urgent yet, but the friction recurs with every new
pixel-domain operator. The pattern is now stable enough across
demos to design against — when a third operator demo needs the
same bolt-on, the right form of the fix should be clearer.

### Friction 3 — metadata-to-UI plumbing is repetitive

Two advanced demos (LiveCaptioning, OnnxInference) end the
configurator chain with the same shape:

```csharp
.Observe((packet, _) =>
{
    var captions = packet.Metadata.TryGet<ActiveCaptions>(out var c) ? c : null;
    var detections = packet.Metadata.TryGet<DetectionResults>(out var d) ? d : null;
    /* stash state, post to Dispatcher.UIThread */
    return ValueTask.CompletedTask;
})
```

The pattern is generic: "extract per-frame metadata, marshal to
UI, debounce or coalesce." A future builder convenience —
something like `OnFrameMetadata<T>(Action<T> uiSideEffect)` or a
metadata-flavoured sink — could fold this away. AvaloniaMulticast
doesn't hit this pattern (no metadata extraction), so the
threshold of "third demo using it" hasn't quite been met. One
more before generalizing.

### Friction 4 — no first-class "no main sink" mode

AvaloniaMulticast hit a wall the first two demos didn't: when the
configurator's returned pipeline is **terminal-shaped** (e.g.
Crossbar's `Broadcast`, which yields nothing downstream after
fanning out to per-branch sinks), the player still wants to
terminate it at the registered `IVideoSink`. The demo's workaround
is registering `NullVideoSink` as a placeholder so the player's
`ToSinkAsync` call drains an empty pipeline.

This works, but it's a tell: the configurator API silently assumes
"one stream → one terminal sink," and multicast violates that.
The placeholder pattern is awkward enough at the call site to
deserve a real seam.

Candidate follow-ups:

- **`WithoutVideoSink()` builder method.** Explicit signal that
  the configurator owns its own terminal. The player skips the
  `ToSinkAsync` step in `RunVideoPumpAsync` (and the matching
  `WithoutAudioSink()` for audio).
- **Detect terminator at runtime.** The player could inspect the
  configurator's returned pipeline and skip `ToSinkAsync` when it
  detects a terminal shape. Probably too magical — explicit opt-in
  is clearer.
- **A `VideoBroadcastBuilder` shorthand.** Cover the most common
  case directly (`.WithVideoBroadcast(panes)`) and route everything
  else through `WithoutVideoSink()` + `ConfigureVideoPipeline`.

The `NullVideoSink` pattern is fine for one demo; if a second
demo hits the same wall, the explicit `WithoutVideoSink()` seam
is the right answer.

### What did *not* show friction

Worth recording the things that worked well, since the experiment
will keep going:

- **Lambda composition** of operators inside a single configurator
  (`video.ConvertPixelFormat(...).DetectWith(...).Observe(...)`)
  reads naturally across all three demos. Matches Crossbar's
  existing fluent surface.
- **Last-call-wins replacing** semantics never tripped any demo —
  no urge to call `ConfigureVideoPipeline` twice.
- **Post-pacing seam location** was right for every demo so far.
  All three want display-rate frames; none needed decode-rate
  access. Multicast specifically benefited — its pre-refactor
  shape consumed `controller.VideoFrames` at decode rate, the
  refactor paces correctly against the master clock.
- **Backwards compatibility** held across all refactors. The
  AvaloniaPlayer example (no configurators) was untouched and
  continued to work identically through every change.
- **The configurator surface scales down.** The AvaloniaPlayer
  example uses no configurator at all and gets the full ergonomic
  win of `WithAvaloniaVideoView` + `WithOpenAlAudio`. The
  configurator surface is purely additive for the advanced cases.
- **DI plumbing absorbed the complexity.** The mechanical
  registration of configurators as DI singletons in
  `PlayerBuilder.BuildAsync` and resolution in
  `ServiceProviderPlaybackSessionFactory` was invisible to all
  three consumer call sites.

### Decision (original)

Leave the configurator surface as-is for now. The friction points
above are real but each is one-or-two-instances of evidence; the
wrong abstraction baked in based on a single demo is worse than
the verbose call site it would replace. Revisit when:

- A third demo needs the side-task pattern → design
  `PipelineBridge<T>` and/or `OnEveryFrame` based on three
  concrete shapes.
- A third demo needs pixel-format insertion → settle the
  sink-vs-operator advertisement question with concrete evidence.
- A second demo hits the "no main sink" wall → add
  `WithoutVideoSink()` / `WithoutAudioSink()` as explicit opt-ins.
- A third demo publishes per-frame metadata to UI → design the
  `OnFrameMetadata<T>` shorthand.

Until then the configurator API stays narrow and the friction
stays in the example layer where it's discoverable rather than
hidden behind premature abstractions.

## Second wave: multicast inference in LiveCaptioning

The LiveCaptioning demo was extended to push YOLOv8 inference into
a multicast branch alongside the presenter, so heavy inference
runs at its own rate without backpressuring playback. The same
demo therefore exercises cross-stream coordination (audio →
captions), pixel-format conversion, multicast fan-out, and
skip-while-busy inference all at once. The exercise crossed
multiple thresholds named in the original decision above and
surfaced one new friction point worth recording before the next
iteration.

### Threshold crossed: Friction 1 — third instance of the side-task pattern

The LiveCaptioning multicast refactor introduces `YoloInferenceSink`,
a small `IFrameSink<IVideoFrame>` that runs YOLOv8 on a background
worker with `Interlocked`-flag-gated skip-while-busy semantics.
This is the **third** materialization of the "tap the stream, do
work elsewhere, route results back" pattern:

1. LiveCaptioning audio → Whisper (channel-based bridge).
2. OnnxInference video → YOLO (Interlocked flag + Task.Run inline
   in `Observe`).
3. LiveCaptioning video → YOLO (Interlocked flag inside a custom
   `IFrameSink` so it can terminate a `Broadcast` branch).

All three express the same dataflow but with different
mechanical shapes — channel-bridged pipeline source, in-line
Observe + Task.Run, custom IFrameSink wrapper. The threshold for
designing a unified abstraction is crossed. Concrete proposal:
add `IFrameSink<T>.OnEveryFrame(callback, busyPolicy)` plus
`PipelineBridge<T>` to Crossbar; both demos can adopt either or
both.

### Threshold crossed: Friction 4 — second instance of "no main sink"

LiveCaptioning is the **second** demo (after AvaloniaMulticast)
to hit the wall where the video configurator's returned pipeline
is terminal-shaped (`Broadcast`). The `NullVideoSink` workaround
is repeated; the awkwardness is identical. The threshold for
adding the explicit opt-in is crossed. Concrete proposal: add
`WithoutVideoSink()` / `WithoutAudioSink()` builder methods. The
player's pump checks for the opt-in flag and skips its
`ToSinkAsync` call entirely; the configurator owns its own
terminal.

### New friction 5 — the convenience extension is silently bypassed when multicast enters

`WithAvaloniaVideoView(view)` is the ergonomic adapter that
hides three coupled concerns: it materializes the view's owned
sink (via `EnsureSink`), registers it as the player's main video
sink, and tracks its lifecycle. Once a demo wants multicast,
**every one of those couplings has to be undone manually**:

```csharp
// Single-sink (current ergonomic path):
.WithAvaloniaVideoView(VideoView)

// Multicast (manual everything):
VideoView.LoggerFactory = _loggerFactory;     // was implicit
var viewSink = VideoView.EnsureSink();        // was implicit
// ...
.WithVideoSink(new NullVideoSink())           // friction 4 placeholder
// ...
branch => branch.Observe(...).ToSink(viewSink) // hand-route
```

The user goes from "one fluent call" to "four lines of imperative
setup" the moment multicast is needed. The ergonomic extension
doesn't compose with the configurator's terminal-shape escape
hatch.

Candidate follow-ups (entangled with Friction 4):

- **`WithAvaloniaVideoView(view, role: SinkRole)`** — let the
  extension take a hint about whether the view is the main sink
  or a branch terminator. When `role == Branch`, the extension
  applies `WithoutVideoSink()` internally and exposes the sink
  via the builder for use inside the configurator.
- **Stop tying the sink lifecycle to the `WithVideoSink` slot.**
  Treat sinks as nameable resources the configurator can
  reference, with the player tracking disposal independently of
  the "main sink" concept.

The current asymmetry surfaces precisely when a demo grows from
single-sink to multicast — i.e., right when the consumer is most
likely to be designing a real application, not a toy.

### Friction 2 (pixel-format coupling) — still two instances

Multicast LiveCaptioning still inserts the explicit
`.ConvertPixelFormat(PixelFormat.Bgra32)` step but doesn't add
a new instance — the same insertion handles both the presenter
and the inference branch. Threshold not crossed.

### Friction 3 (metadata-to-UI plumbing) — partially affected

Splitting caption publishing (presenter branch's `Observe`)
from detection publishing (inference sink's callback) actually
*cleaned up* the metadata-to-UI plumbing in this demo. The
previous unified `PublishOverlayState` callback had to discriminate
captions vs detections by metadata key; the new split has each
concern published from its natural producer. This is a small
positive observation: when consumers split processing by branch,
the metadata-to-UI pattern tends to fall out naturally. The
proposed `OnFrameMetadata<T>` shorthand may be more useful in
true single-stream consumers than in multicast ones.

### Revised decision

Two thresholds have been explicitly crossed. The next phase of
work for ADR-0043 is to design and implement:

1. ~~**`WithoutVideoSink()` / `WithoutAudioSink()`** builder
   methods on `IPlayerBuilder`. Player pumps respect the opt-in
   and skip terminal sink calls when set.~~ **Superseded by
   ADR-0045** — the "main sink" slot is removed entirely. Sinks
   become terminal operators (`ToSink`) the consumer composes
   into the configurator. No opt-out method needed because the
   structure being opted out of no longer exists.
2. ~~**`OnEveryFrame(callback, busyPolicy)`** convenience for the
   skip-while-busy worker pattern.~~ **Resolved by Crossbar
   ADR-0009** — shipped as `BackgroundConsumer<TFrame>` with
   `BusyPolicy.Drop` / `BusyPolicy.Block`. The bespoke
   `YoloInferenceSink` class in LiveCaptioning (100 lines) and
   the in-line skip-while-busy patterns in OnnxInference
   collapse to a single `BackgroundConsumer<IVideoFrame>(work,
   BusyPolicy.Drop)` construction.
3. ~~**`PipelineBridge<T>`** as a Crossbar primitive for the
   channel-to-pipeline pattern.~~ **Resolved by Crossbar
   ADR-0009** — shipped as `PipelineBridge<TFrame>(capacity,
   FrameOverflowPolicy)`. LiveCaptioning's hand-rolled
   `Channel.CreateBounded` + `ReadAllFrames` adapter + manual
   `AddRef`-and-`TryWrite` `Observe` collapse to a single
   bridge construction plus `audio.Tee(bridge.Sink, AddRef)`.

Friction 2 (pixel format) remains at two instances. Hold for
now.

~~Friction 3 (metadata-to-UI plumbing) remains at two instances
each.~~ **Resolved by Crossbar ADR-0009** (operator) +
`FrameFlow.Avalonia` extension (UI-thread marshalling). The
boilerplate `Observe + TryGet + null-check + Dispatcher.UIThread.Post`
chain collapses to `.OnMetadataOnUiThread<TFrame, TMeta>(uiAction)`,
one fluent call per metadata-to-UI binding.

~~Friction 5 (multicast bypass of convenience extensions) is new
and entangled with Friction 4's solution. Address them together.~~
**Partly resolved by ADR-0045.** Removing the main-sink slot
means `WithAvaloniaVideoView(view)` is just "compose
`.ToSink(view.Sink)` into the configurator" — a convenience
shortcut, not a separate slot. Multicast consumers who want the
view's sink as a Broadcast branch simply skip the shortcut and
wire it inside the configurator: friction reduced from "fighting
an imposed structure" to "I didn't use the shortcut for this
shape."

### Status after Crossbar ADR-0009

All four originally-tracked frictions are now either fully or
partly resolved. The remaining gap is Friction 2 (implicit
pixel-format coupling between operators and sinks), still at
two instances; revisit if a third pixel-domain operator surfaces
the bolt-on `.ConvertPixelFormat(...)` pattern.

The LiveCaptioning demo — which surfaced every friction in this
ADR — collapses substantially after these primitives land:

- ~10 lines of channel + `ReadAllFrames` adapter → 2 lines of
  `PipelineBridge<PcmAudioBuffer>(capacity, DropIncoming)` plus
  `pcmBridge.Pipeline.Resample(...).TranscribeWithWhisper(...)`.
- ~100 lines of `YoloInferenceSink` class → 10 lines of
  `BackgroundConsumer<IVideoFrame>(workCallback, BusyPolicy.Drop)`
  inline in the configurator setup.
- ~30 lines of caption-publish / detection-publish / dispatcher
  glue → one `.OnMetadataOnUiThread<IVideoFrame, ActiveCaptions>(updateUi)`
  call inside the presenter branch.

The remaining hand-rolled scaffolding around the fluent player
chain is mostly Avalonia-layer plumbing (window lifecycle,
status badge updates) rather than Crossbar / FrameFlow API
friction.

## Decision summary

The Layer-2 player gains two configurator methods that let
consumers inject Crossbar operators between the playback layer's
pacing/metrics and the registered sink. The chosen shape — a
single delegate per stream, lambda-composed, applied post-pacing
— is intentionally minimal so the rest of the consumer surface
remains stable while we learn from real demos.
