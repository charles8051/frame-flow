# ADR-0042: Pipeline Error Handling via Fluent Operators

## Status

Accepted. Crossbar primitives (`CatchPolicy`, `Catch`, `OnError`,
`SinkErrorPolicy`, policy-aware `ToSinkAsync` overload) are implemented
and tested. The video pump in `FrameFlow.Playback.PipelineController`
has adopted the new sink error policy (`SkipFrame`). Wholesale migration
of pump-level fault reporting from outer `try/catch` to fluent
`OnError` is **deferred** — the operators exist, but a richer policy
pattern is not yet needed in playback. The pattern is documented here
so future consumers (capture, transcoding, multi-source compositing)
can adopt it intentionally.

**Date:** 2026-05-13
**Supersedes:** None.
**Related:** ADR-0001 (frame ownership / operator-owns-upstream),
ADR-0023 (hierarchical state machine — XR007 worker-fault contract),
ADR-0030 (Crossbar frame substrate),
ADR-0034 (diagnostics surfaces),
ADR-0036 (decoded media stream decoupled from playback).

## Context

The playback pumps (`RunVideoPumpAsync`, `RunAudioPumpAsync`) wrap their
composed pipelines in a single outer `try/catch`:

```csharp
try {
    await pipeline.Compose(...).ToSinkAsync(sink, ct);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
catch (Exception ex) {
    ReportWorkerFault("VideoPump", ex);
}
```

That shape collapses every distinct failure into a single resolution
path: "something went wrong, surface it as a worker fault, state
machine goes to Error." A video pump's composed pipeline has at least
four distinct origins of `Exception`:

1. **Decoder workers inside the stream** — codec-native failure, packet
   routing crash. Genuinely fatal; the stream is dead.
2. **`PacedUntil`'s `WaitUntilAsync`** — typically
   `ObjectDisposedException` when the clock source is disposed during
   shutdown. Often a benign race with cancellation.
3. **`Observe` callback** — bug in the per-frame metric code, or a
   malformed packet leaking through. Should be a logged anomaly, not
   necessarily a state-machine fault.
4. **Sink `PresentAsync`** — transient presenter issue (Avalonia drops
   a frame under GPU pressure, OpenAL underrun in flight). Often
   recoverable on the next frame.

Today the four cases all funnel into the same outer catch with the
same resolution. That works in practice partly by luck: cancellation
almost always wins the shutdown race, masking case 2; cases 3 and 4
have not yet surfaced often enough to need different treatment. As
soon as the system grows additional consumers — capture pipelines
talking to hardware that can transiently disconnect, transcoder
pipelines where one stream's failure shouldn't kill the other,
multi-source compositors with per-input policy — the single-catch
shape stops being adequate.

This ADR introduces **error handling as composable pipeline operators**
so failures can be addressed at the layer where they occur, with the
policy that's appropriate to that layer, without expanding the
per-consumer outer-catch surface every time a new failure mode is
worth distinguishing.

## Decision

### 1. Two new Crossbar primitives in the public surface

**`CatchPolicy`** — what to do after a handler runs.

```csharp
public enum CatchPolicy
{
    /// Invoke handler, then re-throw. Handler is a diagnostics tap.
    Rethrow,
    /// Invoke handler, then terminate the pipeline cleanly. The
    /// downstream enumeration sees a normal completion.
    Stop,
}
```

`Skip` is intentionally absent. Once an exception escapes an
`IAsyncEnumerable<>` enumerator, the enumerator is in an undefined
state and "skip the bad packet and continue iterating" cannot be made
safe at the operator-chain level. For per-packet recovery, the right
scope is a `try/catch` inside the per-packet callback (the
`Transform`/`Observe`/`Enrich` delegate).

**`Catch(policy, handler)`** — generic exception interception. Manual
enumeration of the upstream so `MoveNextAsync` exceptions are catchable.

**`OnError(handler)`** — sugar for `Catch(CatchPolicy.Stop, handler)`,
the common case.

**Cancellation is never caught.** `OperationCanceledException`
propagates through `Catch` unchanged regardless of policy. Cancellation
has its own teardown contract; accidentally swallowing it would break
pause / seek / dispose semantics in every downstream consumer.

### 2. Per-sink error policy on the terminal

**`SinkErrorPolicy`** — three values:

- `Rethrow` (default): propagate, matching the existing simple
  `ToSinkAsync` overload's behaviour.
- `Stop`: terminate the pipeline cleanly. Pump task succeeds; any
  frames still in flight upstream are drained-and-disposed by the
  pipeline's tail.
- `SkipFrame`: continue with the next frame. The handler runs first
  so the failure isn't silently swallowed.

**`SkipFrame` exists here but not on `Catch`** because the sink is the
terminal — there is no enumerator state to recover, just "try the
next frame." For upstream-operator errors that semantic doesn't
compose.

**Ownership.** Per ADR-0001 §3, the sink owns the frame from the
moment `PresentAsync` is invoked even on throw. `ToSinkAsync` does not
dispose the frame on any policy — the sink's own `catch` is the
responsible party.

### 3. Fluent error handlers are state-machine-friendly

The pattern that's enabled but not yet required:

```csharp
await pipeline.Compose(...)
    .OnError((ex, _) => {
        ReportWorkerFault("VideoPump", ex);   // state-machine side effect
        return ValueTask.CompletedTask;
    })
    .ToSinkAsync(sink, ct);
```

Same observable behaviour as the outer `try/catch` (state machine
transitions to `Error`), but the report is **inside** the pipeline.
The pump's outer `try/catch` shrinks to a true backstop for things
that genuinely escape.

This composes: multiple `Catch`/`OnError` operators at different points
in the chain can apply different policies and report different
operator-level context to the state machine.

### 4. The playback pumps adopt only the sink policy for now

`RunVideoPumpAsync` uses `SinkErrorPolicy.SkipFrame` so transient
presenter failures don't kill the entire video pump for the rest of
playback. The outer `try/catch` still handles upstream faults
(decoder failure, clock-disposed) because the worker-fault contract
(XR007) requires them to route through `ReportWorkerFault` →
`FatalError` → `Error` state.

`RunAudioPumpAsync` keeps the default `Rethrow` policy. Audio is less
forgiving of skipped buffers than video — dropping a PCM block causes
audible gaps — so a sink failure escalates to a worker fault rather
than being absorbed.

This is a deliberate "use only what we need now" decision. The fluent
surface exists for future consumers that have richer policy needs;
playback's current shape is honest about its single resolution path
for upstream faults.

## Consequences

### Positive

- **Locality.** Error policies live at the operator boundary where the
  failure originates rather than collapsed into one outer catch.
  Reviewers reading the pump body can see which failures the pipeline
  is prepared to absorb and which it isn't.
- **Transient sink failures stop killing playback.** With
  `SinkErrorPolicy.SkipFrame`, the video pump survives one bad
  `PresentAsync` call. A counter (`_videoFramesSkippedAtSink`) records
  how often this happens.
- **Cancellation invariant preserved.** Both `Catch` and the
  policy-aware `ToSinkAsync` explicitly propagate
  `OperationCanceledException` without invoking handlers — pause /
  seek / dispose teardown semantics survive composition.
- **Surface for capture and transcode tiers.** ADR-0040's capture
  sources and encoder terminals will have multiple distinct failure
  modes per consumer (hardware disconnect, disk full, encoder error,
  container-mux error). Each will want a different recovery policy.
  The fluent surface is the natural place to express that.
- **Skip semantics are honest.** `Catch` deliberately doesn't offer
  Skip; `SinkErrorPolicy.SkipFrame` does, and only because the
  terminal position makes it well-defined. Consumers can't accidentally
  ask for an undefined "skip and continue from a faulted enumerator"
  semantic.

### Negative

- **More API surface in Crossbar.** Two enums, one new operator, one
  new `ToSinkAsync` overload. Surface that has to be documented,
  versioned, and explained.
- **Verbosity when fully adopted.** A pump that uses fluent
  per-operator handlers grows from ~10 lines to ~25-30. The fluent
  shape only pays off when the consumer genuinely has more than one
  distinct failure-resolution path.
- **Single-fire semantics.** A `Catch(Stop, …)` consumes the
  exception. If three observers all want to see a fault (metric, log,
  state machine), the first two must use `Rethrow` and only the third
  uses `Stop`. Easy to get the policy order wrong.
- **Ordering pitfalls.** A `Catch` after `Observe` covers `Observe`'s
  exceptions too. Operator-level error policy doesn't reach inside a
  single operator's callback — for that the callback needs its own
  internal `try/catch`. Composition does not eliminate per-callback
  defensive coding.

### Neutral

- **The outer `try/catch` in the pumps does not disappear.** It
  shrinks in role to "did we forget a handler somewhere?" but it
  remains because (a) `OperationCanceledException` still needs the
  cancellation-check filter, and (b) any uncaught composition bug
  should surface somewhere observable.
- **No retry operator.** `Catch` and `OnError` do not retry. If a
  consumer wants retry semantics, it implements them by wrapping its
  per-packet callback. The pipeline operators are deliberately
  policy-not-mechanism.
- **No typed exception channel.** The handler signature is
  `Func<Exception, CancellationToken, ValueTask>`. Structured context
  (operator name, frame PTS, phase) is captured by the lexical
  location of the handler in the pipeline composition; if richer
  metadata is needed, an `Enrich` upstream of the failure point can
  record it for inspection inside the handler.

## Alternatives considered

### A. `Result<T>` over every operator

Every operator becomes `FramePipeline<Result<TFrame>>`. Failures
travel as values rather than exceptions; consumers pattern-match on
`Success` / `Failure`.

Rejected:

- Frames are owned resources (`TFrame : IDisposable`). A
  `Result<TFrame>.Failure` still needs to dispose the failed frame; the
  type system gives no help with that protocol. The same try/dispose
  discipline that exists today inside operators is required, just
  wrapped in extra ceremony.
- Most "errors" aren't per-frame failures. Decoder died, clock
  disposed, presenter went away — these are pipeline-lifetime events,
  not bad-frame events. A `Result` wrapper per packet implies "any
  given frame might be a failure," which is the wrong mental model
  for media streams whose failure rate is near zero.
- Adopting `Result<T>` as a Crossbar primitive forces every existing
  operator, every consumer, and every adapter into the new shape for
  a marginal gain at the integration seam.

### B. Telemetry-only side channel

Pipelines emit structured events (operator name, phase, error) on a
side channel; consumers subscribe. The pipeline itself doesn't change.

Rejected as insufficient: this answers "what happened" but not "what
should happen next." Today's playback consumer still needs the state
machine to react, which the side channel doesn't drive.

### C. Per-operator `onError` parameter on every operator

`Transform(t, onError: ...)`, `Observe(o, onError: ...)`, etc. Every
operator gains an optional error parameter.

Rejected as API bloat. The same effect is achievable with a separate
`Catch` operator inserted in the chain, which is more composable (you
can place it anywhere) and doesn't double the per-operator surface
area.

### D. Adopt fluent `OnError` in the playback pumps now

Replace each pump's outer `try/catch` with `.OnError(ReportWorkerFault)`
on the upstream and let the pump task always complete cleanly.

Rejected for now. It would be a behavioral substitution without
adding any observable value in playback's current shape — both
approaches end at the same state-machine transition. The fluent
substitution is only worth the verbosity when there's a real
distinction to draw between failure modes. The two cases that would
benefit (clock-disposed-during-shutdown and per-operator metric
tagging) are flagged below as concrete follow-ups; until they're
needed, the simpler shape stays.

## Concrete follow-ups identified

1. **Clock-disposed shutdown race.** `PacedUntil`'s
   `WaitUntilAsync` can throw `ObjectDisposedException` if the clock
   source is disposed before the pump's cancellation token fires —
   currently this would be reported as a real worker fault. A single
   `Catch` after `PacedUntil` could treat
   `ObjectDisposedException && _shutdownCts.IsCancellationRequested`
   as benign and route everything else through
   `ReportWorkerFault("VideoPump.Clock", ex)`. Not urgent; a real
   instance has not been observed in test runs because cancellation
   usually wins the race.

2. **Surface `_videoFramesSkippedAtSink` in
   `PipelineDiagnosticsSnapshot`.** The counter exists but is
   currently only observable via logs. Promoting it to the
   diagnostics surface (ADR-0034) makes "is the presenter rejecting
   frames?" a queryable signal for the UI.

3. **Capture and transcode adoption.** ADR-0040's capture pipelines
   and encoder terminals will exercise the fluent surface in earnest
   — that's the right moment to validate the design against real
   consumer needs and refine it if necessary.

## References

- ADR-0001 §3: sink ownership contract (frame owned from the moment
  `PresentAsync` is invoked, even on throw)
- ADR-0023: hierarchical state machine — XR007 worker-fault contract
- ADR-0036: decoded-media-stream / playback split (the seam where the
  pumps live)
- `src/Crossbar/CatchPolicy.cs`, `src/Crossbar/SinkErrorPolicy.cs`,
  `src/Crossbar/FramePipelineExtensions.cs` (Catch + ToSinkAsync
  policy overload) — the implementation
- `tests/Crossbar.Tests/Operators/CatchTests.cs`,
  `tests/Crossbar.Tests/Operators/ToSinkAsyncPolicyTests.cs` — the
  contract suite (17 tests across both target frameworks)
- `src/FrameFlow.Playback/PipelineController.cs` — the consumer
  adopting `SinkErrorPolicy.SkipFrame` in the video pump
