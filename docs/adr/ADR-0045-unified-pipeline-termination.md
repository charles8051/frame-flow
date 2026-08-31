# ADR-0045: Unified Pipeline Termination via RunAsync

## Status

**Accepted.** Drops the player's hardcoded "main sink" termination
path in favor of universal `pipeline.RunAsync(ct)` driving. Sinks
become terminal operators (`ToSink`) the consumer composes into
the configurator like any other operator. `WithVideoSink` and
`WithAudioSink` are retained as convenience methods that internally
compose `.ToSink(sink)` into the configurator's output.

Supersedes friction-point fixes proposed in ADR-0043's "Second
wave" section that introduced `WithoutVideoSink()` / `WithoutAudioSink()`
to opt out of the main-sink slot. Those methods are no longer
needed — the slot itself is removed.

**Date:** 2026-05-13
**Supersedes:** ADR-0043 "Second wave" friction-4 / friction-5
proposals (`WithoutVideoSink()`, `WithoutAudioSink()`).
**Related:**
- ADR-0024 (playback controller as public API surface)
- ADR-0030 (Crossbar frame substrate)
- ADR-0036 (decoded media stream decoupled from playback)
- ADR-0043 (consumer-configurable pipeline operators)
- ADR-0044 (sink ownership and disposal)

## Context

Crossbar's pipeline architecture has exactly one fundamental
driver: `pipeline.RunAsync(ct)` enumerates the underlying
`IAsyncEnumerable<FramePacket<T>>`. Operators compose along the
way; terminal operators (`ToSink`, `Broadcast`, `Tee` chains)
consume frames at the end. `ToSinkAsync(sink, ct)` is implemented
as two lines:

```csharp
// As of Crossbar ADR-0010 (2026-05-15) the parameter is
// FrameConsumer<TFrame>; IFrameSink<T> was deleted. Resource-owning
// sinks pass their .Consumer projection.
public static Task ToSinkAsync<TFrame>(
    this FramePipeline<TFrame> pipeline,
    FrameConsumer<TFrame> consumer,
    CancellationToken ct = default)
{
    return pipeline.ToSink(consumer).RunAsync(ct);
}
```

That is — `ToSink(sink)` is just an operator like `Tee`, `Observe`,
or `Broadcast`. It happens to be terminal-shaped (yields no
packets downstream), but it is not a special primitive at the
Crossbar layer. `RunAsync` is the universal terminus.

FrameFlow's Layer-2 player invented a separate concept: a "main
sink" slot, registered via `WithVideoSink(IVideoSink)` /
`WithAudioSink(IAudioSink)`. The player's pump uses that
registered sink as the terminator, hardcoding the call:

```csharp
// PipelineController.RunVideoPumpAsync (pre-ADR-0045)
if (_stream is null || _videoSink is null) return;
var pipeline = _stream.Video.PacedUntil(_clockSource).Observe(metrics);
if (_videoConfigurator is not null) pipeline = _videoConfigurator(pipeline);
await pipeline.ToSinkAsync(_videoSink, SinkErrorPolicy.SkipFrame, …);
```

The pump requires `_videoSink` to be non-null even when the
configurator's returned pipeline already terminates internally
(e.g. `Broadcast`, a chain of `Tee` calls, or any consumer
construction that doesn't fit a "one sink at the end" model).
This is the source of the friction documented across two demos:

- **AvaloniaMulticast** registers `NullVideoSink` as a placeholder
  because `Broadcast` is terminal-shaped.
- **LiveCaptioning** (multicast variant) repeats the same
  placeholder to fan video out to both a presenter branch and a
  YOLO inference branch.

ADR-0043's "Second wave" proposed adding `WithoutVideoSink()` /
`WithoutAudioSink()` opt-in methods to skip the player's appended
`ToSinkAsync` call. That preserves the "main sink" abstraction
while letting consumers opt out — but the abstraction itself is
the problem. The player imposes a structure (`single terminal
sink`) that doesn't exist at the Crossbar level and forces every
consumer with a non-trivial pipeline shape to work around it.

The architectural answer is to **remove the imposed structure
entirely**. Sinks are operators. `RunAsync` is the terminus.
FrameFlow's player should drive whatever pipeline the configurator
returns, without imposing a particular shape.

## Decision

### Termination

The player's pump calls `pipeline.RunAsync(ct)` on the
configurator's returned pipeline. The pump no longer composes
its own terminal `ToSink`. Whatever shape the configurator
returns — `ToSink`, `Broadcast`, multi-`Tee`, or a future
operator we haven't invented — is what runs.

```csharp
// PipelineController.RunVideoPumpAsync (post-ADR-0045)
if (_stream is null || _videoConfigurator is null) return;
var pipeline = _stream.Video.PacedUntil(_clockSource).Observe(metrics);
pipeline = _videoConfigurator(pipeline);
await pipeline.RunAsync(ct);
```

Same for audio. The pump runs when a configurator is present;
absent a configurator, the pump skips (decoded frames still
flow through `IPlaybackController.VideoFrames` for pull-mode
consumers per ADR-0032).

### Builder methods retained as convenience

`WithVideoSink(IVideoSink)` and `WithAudioSink(IAudioSink)` are
kept on `IPlayerBuilder` but reimplemented as **convenience
composition into the configurator**:

```csharp
public IPlayerBuilder WithVideoSink(IVideoSink sink) {
    var existing = _videoConfigurator;
    _videoConfigurator = pipeline =>
        (existing?.Invoke(pipeline) ?? pipeline).ToSink(sink);
    _videoSink = sink;  // remembered for clock-source selection + diagnostics
    return this;
}
```

The convenience is: a consumer who just wants "send all video
to this sink" writes one line:

```csharp
.WithVideoSink(myView.Sink)
```

A consumer who wants pre-sink transformation:

```csharp
.ConfigureVideoPipeline(p => p.ConvertPixelFormat(Bgra32))
.WithVideoSink(myView.Sink)  // appended after the configurator
```

A consumer with a non-trivial shape skips `WithVideoSink`
entirely and puts the sink wherever it belongs:

```csharp
.ConfigureVideoPipeline(p => p
    .ConvertPixelFormat(Bgra32)
    .Broadcast(...,
        branch => branch.ToSink(view.Sink),
        branch => branch.ToSink(inferenceSink)))
```

All three are first-class. The single-sink case stays one line;
the multicast case is no longer a hack.

### Convenience extensions unchanged at the call site

`WithAvaloniaVideoView(view)` and `WithOpenAlAudio(loggerFactory)`
continue to work — they call `WithVideoSink` / `WithAudioSink`
internally. Under the new model that means they compose
`.ToSink(view.Sink)` and `.ToSink(openAlSink)` into the
configurator. Simple consumers see no API change.

Multicast consumers who want the view's sink as a branch (not as
a main terminator) bypass the extension and wire it manually
inside the configurator — same friction as before, but
fundamentally different in nature: there is no longer a "main
sink" abstraction the consumer is fighting against. They simply
write the pipeline they want.

### Clock-source selection unchanged

When `WithAudioSink(sink)` is called, the sink is recorded for
two purposes: composition into the audio configurator (the new
behavior), and registration as `IAudioSink` in DI (the existing
ADR-0044 behavior). The DI registration is what the
`PlaybackSession` resolves to pick a master clock when the sink
implements `IClockSource`. The clock-source pathway is
independent of the pump's termination strategy.

For consumers who register an audio sink directly inside a
configurator (via `.ToSink(audioSink)` only), the player has no
visibility into the sink and falls back to wall-clock pacing.
That is the explicit contract: `WithAudioSink` opts into both
"play audio" and "use this as master clock"; a configurator-only
audio sink is treated as a side effect that the player doesn't
coordinate against.

### Per-sink diagnostics

`PipelineDiagnosticsSnapshot.VideoSink` and `.AudioSink` are
sourced from the sinks recorded via `WithVideoSink` /
`WithAudioSink`. Consumers using configurator-only sinks (no
convenience method) get `Empty` snapshots for those fields and
are responsible for calling `GetDiagnostics()` on their own sink
references. Diagnostics are not lost — they are just located at
the right ownership boundary.

## Alternatives considered

### A. `WithoutVideoSink()` / `WithoutAudioSink()` opt-ins (the ADR-0043 Second-wave proposal)

Keep the main-sink slot; add explicit methods that tell the
player to skip its appended `ToSinkAsync`. Rejected: preserves
the wrong abstraction. The "main sink" concept is what creates
the friction in the first place. Adding an opt-out to a
problematic abstraction is worse than removing the abstraction.

### B. Detect terminal-shaped pipelines at runtime

The player inspects the configurator's returned pipeline and
calls `RunAsync` when it detects a terminal operator, otherwise
`ToSinkAsync`. Rejected: requires runtime introspection that
Crossbar doesn't expose, and the heuristic is brittle (a
configurator returning a chain that happens to terminate via
`Tee` followed by `Observe` is hard to classify). Explicit beats
implicit; "the configurator always says how to terminate" is the
explicit version.

### C. Drop `WithVideoSink` / `WithAudioSink` entirely

Force consumers to always write `.ConfigureVideoPipeline(p => p.ToSink(sink))`.
Rejected: makes the simple case more verbose for no architectural
benefit. The convenience methods are real ergonomics for the
80%-of-consumers single-sink path.

### D. Make sinks observable as operators on their own (`SinkOperator(sink)`)

Replace `ToSink(sink)` with a more general operator wrapper.
Rejected: `ToSink` already does this. Crossbar's existing
operator vocabulary is sufficient; no new wrapping is needed.

## Consequences

### Positive

- **Friction 4 (no main sink mode) eliminated.** Not by adding
  opt-out methods, but by removing the imposed structure that
  created the need.
- **Friction 5 (multicast bypasses convenience extension)
  partially resolved.** The new model treats `WithAvaloniaVideoView`
  as "append a sink at the end" — which is exactly what the
  consumer wants in single-sink scenarios. Multicast consumers
  still skip the convenience, but the friction is now "I didn't
  use the shortcut" rather than "I had to fight the framework's
  main-sink slot."
- **`NullVideoSink` placeholder pattern disappears.** Both
  AvaloniaMulticast and LiveCaptioning multicast can drop the
  workaround. The DI registration of a sink the consumer never
  intends to use goes away.
- **Player pump is smaller.** The bifurcation between
  "configurator + ToSinkAsync" and "RunAsync drain" collapses
  into one path.
- **Generalizes to future shapes.** Any new Crossbar operator
  with terminal-shape semantics works in the configurator
  without player changes.
- **Cleaner mental model.** "The configurator produces the
  pipeline; the player drives it." No second mechanism.

### Negative

- **Breaking change to the player's pump contract.** Consumers
  who relied on "I register a video sink and frames automatically
  go to it" continue to work via the retained convenience
  method, but anyone who was poking at `PipelineController`
  internals sees a different shape.
- **`PipelineDiagnosticsSnapshot.VideoSink` semantics shift
  slightly.** For configurator-only consumers, that field is
  `Empty`. Acceptable — the consumer owns their sink
  references and can query diagnostics directly.
- **`WithVideoSink` composition has ordering implications.** If
  called after `ConfigureVideoPipeline`, it appends. If called
  before, the configurator may overwrite or wrap it. We define
  the contract as "compose in call order; last-call-wins for
  `ConfigureVideoPipeline` replacing the entire configurator
  except for previously-composed `WithVideoSink` appends." The
  docs need to be clear; in practice consumers call them in
  one consistent order.

### Neutral

- **Sink ownership (ADR-0044) is unchanged.** DI containers
  still own sink lifetime. The player still doesn't dispose
  sinks. Idempotent dispose contract still holds.
- **Clock-source selection (ADR-0035) is unchanged.** Audio
  sinks that implement `IClockSource` still serve as master
  clock when registered via `WithAudioSink`.
- **Pull-mode consumers (ADR-0032) are unaffected.** No
  configurator means no pump runs; consumers read frames via
  `IPlaybackController.VideoFrames` / `AudioBuffers` as before.

## Implementation

The change is small. Single commit. Per the pre-1.0 stance - no external
consumers, breaking changes land inline: no
backwards-compatibility shims, no `[Obsolete]` markers.

1. **`PipelineController.RunVideoPumpAsync` / `RunAudioPumpAsync`**:
   - Pump runs when `_videoConfigurator is not null` (no more sink
     null-check).
   - Builds pre-configurator pipeline (`PacedUntil` + `Observe`
     metrics).
   - Applies configurator.
   - Calls `RunAsync(ct)` instead of `ToSinkAsync(sink, ...)`.
   - Removes `_videoSink` / `_audioSink` field usage from the pump
     body (still held for diagnostics rollup).

2. **`PlayerBuilder.WithVideoSink` / `WithAudioSink`**:
   - Compose `.ToSink(sink)` into the corresponding configurator
     instead of registering a "main sink" slot.
   - Continue to track the sink reference for clock-source
     selection (audio) and diagnostics (both).

3. **Examples cleanup**:
   - `FrameFlow.Examples.AvaloniaMulticast` drops
     `.WithVideoSink(new NullVideoSink())` — multicast configurator
     handles its own termination.
   - `FrameFlow.Examples.LiveCaptioning` drops the same placeholder
     from its multicast variant.

4. **ADR-0043 update**:
   - Mark "Second wave" friction-4 / friction-5 fixes as
     superseded by this ADR. Decision pointer updated.

5. **Tests**:
   - Audit `PlaybackSessionTeardownTests` and any pump-behavior
     tests for assertions about `ToSinkAsync` invocation. Update
     to assert `RunAsync`-shaped behavior.

## Decision summary

Pipelines are driven by enumeration. Crossbar's `RunAsync` is
the universal enumerator. Sinks are terminal operators (`ToSink`)
the consumer composes into the configurator, not a separate slot
imposed by the player. The player's pump always calls `RunAsync`
on the configurator's returned pipeline. Convenience methods
(`WithVideoSink`, `WithAudioSink`, `WithAvaloniaVideoView`,
`WithOpenAlAudio`) survive as shortcuts that compose `.ToSink(sink)`
into the configurator; the simple case stays one line, the
multicast case stops fighting an abstraction that was never
necessary.
