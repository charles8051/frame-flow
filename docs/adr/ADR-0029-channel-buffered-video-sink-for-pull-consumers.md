# ADR-0029: Channel-Buffered Video Sink for Pull-Style Consumers

**Status:** Superseded by ADR-0032 (pull-shape playback controller) and
finalized by ADR-0036 Phase 3. `ChannelVideoSink` and its
`AddFrameFlowChannelVideoSink` DI helper were deleted once the in-tree
examples (inference, multicast) migrated to
`IPlaybackController.VideoFrames` and `IDecodedMediaStream.Video`. Pull
consumers now get a `FramePipeline<IVideoFrame>` directly without a
push→pull bridge in between. This ADR's design lives on as the
*shape* of the pull surface — bounded channel with configurable
overflow policy — but is no longer a separate component.
**Date:** 2026-05-10
**Supersedes:** None
**Related:** ADR-0024 (PlaybackController as public API), ADR-0025 (video sink and frame pool architecture), ADR-0028 (internal layering and ownership cleanup), ADR-0030 (unify frame contracts with Crossbar — adopted this ADR's `ChannelVideoSink` proposal and unified the consumer-facing pipeline shape with Crossbar's `FramePipeline<TFrame>`), ADR-0032 (pull-shape playback controller — superseded this ADR), ADR-0036 (decode-stream decoupling — completed the dissolution)

## Status note

This ADR's proposal — `ChannelVideoSink` as a first-class
`IVideoSink` that exposes pull-shaped consumption via a buffered
channel — landed during the ADR-0030 migration. The shipped type:

- Lives at `src/FrameFlow.Playback/ChannelVideoSink.cs`.
- Uses `Crossbar.FrameChannelOptions` and `Crossbar.FrameOverflowPolicy`
  for capacity / overflow configuration (rather than a FrameFlow-
  specific `ChannelVideoSinkOptions` / `ChannelOverflowMode` — the
  unification with Crossbar's primitives means one configuration shape
  for buffered fan-out across the runtime).
- Exposes `ReadAllAsync(ct)` as `IAsyncEnumerable<IVideoFrame>`;
  consumers compose with Crossbar's `AsPipeline()` extension to land
  in a `Crossbar.FramePipeline<IVideoFrame>`.
- Has a DI helper, `AddFrameFlowChannelVideoSink(out var sink, ...)`,
  matching the `AddFrameFlowSdlVideoSink` / `AddFrameFlowAvaloniaVideoSink`
  pattern.
- Replaces the `BroadcastBridgeSink` adapter that the
  `FrameFlow.Examples.AvaloniaMulticast` example used to carry —
  example now wires up the public sink in one DI line.

The "Native pull-shaped playback" alternative this ADR rejected
remains future work; ADR-0030 carries forward the same deferral. The
buffered-sink approach is the canonical bridge from FrameFlow's
push-shaped playback runtime to pull-shaped downstream pipelines.

**Update 2026-05-15 / 16 (Crossbar ADR-0010 / ADR-0012).** The
`ChannelVideoSink` shape sketched below still applies, but two
type-level changes from the Crossbar substrate cleanup propagate:
- `SupportedMemoryDomains` was deleted from `IVideoSink` (and from
  Crossbar entirely). The sketch in §"Decision" still shows it;
  read as historical.
- `IVideoSink` no longer inherits from `Crossbar.IFrameSink<T>` — it's
  a standalone `IAsyncDisposable` with a `FrameConsumer<IVideoFrame>
  Consumer { get; }` property. `ChannelVideoSink` follows the same
  convention.

Crossbar's `PipelineBridge<TFrame>` (the substrate primitive
introduced after this ADR landed — see Crossbar ADR-0009) covers a
related but more general bridging case: it lets a *producer
pipeline* feed a *consumer pipeline* across schedules. ChannelVideoSink
remains the right answer when the upstream side is the playback
runtime's video pump (push-shaped, owned by `PlaybackSession`); use
`PipelineBridge<T>` when both sides are pipelines you control.

## Context

FrameFlow's playback architecture (per ADR-0025) commits the
`IVideoSink` interface to a **push** shape: the
`PipelineController.RunVideoSinkWorkerAsync` worker decodes frames at
their PTS-paced cadence and calls `sink.PresentAsync(frame, ct)` on
the registered sink. This is a sound choice for the load-bearing case
— a single Avalonia or SDL surface receiving frames into a pending
slot, render thread consumes — but it leaks a hard problem onto any
consumer that wants to compose FrameFlow output into a pull-shaped
downstream pipeline.

### The push-pull mismatch in practice

The motivating concrete case is
Crossbar, a vendor-neutral
graph runtime for media pipelines. Crossbar's substrate
(Crossbar ADR-0001)
is intentionally pull-shaped — operators compose via
`IAsyncEnumerable<FramePacket<TFrame>>`, with `Channel<T>` for fan-out
and structural backpressure. To plug FrameFlow's playback into a
Crossbar pipeline today, the consumer has to write a hand-rolled
adapter: an `IVideoSink` whose `PresentAsync` writes into a
`Channel<IVideoFrame>`, exposing `IAsyncEnumerable<IVideoFrame>` via
the channel reader.

The `FrameFlow.Examples.AvaloniaMulticast` example contained exactly this
adapter — `BroadcastBridgeSink`, ~100 lines.
Every Crossbar consumer of FrameFlow that ships will write essentially
the same code, with essentially the same overflow policy choices, and
essentially the same drop semantics. The pattern is reusable; the
implementation cost is paid by every consumer.

### Why not redesign the playback pipeline as pull-native?

A native pull-shaped redesign — exposing
`IAsyncEnumerable<IVideoFrame> PlaybackSession.VideoFrames(ct)` and
relocating the A/V sync delay from the worker task into the iterator
body — is conceptually cleaner. But the cost is large:

- `PipelineController` would need a "no video worker" mode coordinated
  with the existing demux pump, audio worker, gate, barrier, and cycle
  latch (per ADR-0022 §workers and ADR-0026 §state-bound binding).
- Seek and loop-restart logic (the latter just stabilized in
  `aeec5dc`) would need the iterator to participate in flush
  semantics.
- Pause/resume semantics for an `await foreach` consumer would need
  defining (presumably: `MoveNextAsync` blocks while paused — which
  is essentially a Channel-with-a-gate again, just internalized).
- Audio remains push-shaped at the device boundary regardless (audio
  hardware drains a buffer at hardware rate; you cannot `await foreach`
  your way out of that). The pull API is video-only by physics.
- Every existing integration test exercising push semantics needs a
  pull twin.

That's ~1500–2500 lines of changed/added code with real coordination
work and regression risk on every existing test. Worth doing only if
the resulting design enables something the channel-buffered approach
does not.

It does not. The buffer between the decoder and the consumer doesn't
disappear in a pull-native design — it just moves from an explicit
`Channel<T>` to an implicit "decoded ahead of yield point" state
inside the iterator. From the consumer's perspective the API looks the
same; only the internal plumbing differs. That's a refactor for taste,
not for correctness or performance.

## Decision

Ship a **`ChannelVideoSink`** as a first-class type in
`FrameFlow.Playback`, plus a small DI helper, plus a thin accessor on
`IPlaybackController` (or the helper's `out`-parameter pattern, see
Decision 3). The internal playback architecture is unchanged; the new
type is an `IVideoSink` like any other.

### Decision 1: New public type `ChannelVideoSink`

**Location:** `src/FrameFlow.Playback/ChannelVideoSink.cs`

**Shape (sketch — names final at PR time):**

```csharp
namespace FrameFlow.Playback;

public sealed class ChannelVideoSink : IVideoSink
{
    public ChannelVideoSink(
        IFramePool framePool,
        ChannelVideoSinkOptions? options = null);

    public IFramePool FramePool { get; }
    public IReadOnlyList<FrameMemoryDomain> SupportedMemoryDomains { get; }

    /// <summary>Frames received via PresentAsync; readable
    /// concurrently with playback.</summary>
    public IAsyncEnumerable<IVideoFrame> ReadAllAsync(CancellationToken ct);

    /// <summary>Diagnostic counters analogous to AvaloniaVideoSink's
    /// RenderedFrameCount / DroppedFrameCount.</summary>
    public long PresentedFrameCount { get; }
    public long DroppedOnOverflowCount { get; }

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct);
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct);
    public ValueTask DisposeAsync();
}

public sealed class ChannelVideoSinkOptions
{
    public int Capacity { get; init; } = 1;
    public ChannelOverflowMode Overflow { get; init; }
        = ChannelOverflowMode.DropIncoming;
}

public enum ChannelOverflowMode
{
    /// <summary>WriteAsync blocks the producer until space exists.</summary>
    BlockProducer,

    /// <summary>TryWrite drops the new frame on full channel.</summary>
    DropIncoming,

    /// <summary>Channel evicts the oldest queued frame to admit the new one.</summary>
    DropOldestQueued,
}
```

The defaults (capacity 1, drop-incoming) match the live-preview
contract that `AvaloniaVideoSink` and `SdlVideoSink` already enforce
implicitly via their `Interlocked.Exchange` pending-frame slots. A
consumer with different needs (recording, batch processing) sets
non-default options.

### Decision 2: Frame ownership through the channel

`PresentAsync` takes ownership of the frame per ADR-0025
§frame-ownership. The sink either:

- Hands the frame to the channel writer (consumer takes ownership on
  read); or
- Disposes the frame locally on overflow (drop-incoming mode) or on
  eviction (drop-oldest mode, via the channel's `ItemDropped` callback).

`DisposeAsync` drains the channel and disposes any remaining frames so
the underlying pool buffers recycle promptly.

This mirrors the pattern proven in `BroadcastBridgeSink` and matches
the ownership protocol Crossbar already requires of any `IVideoSink`
adapter (Crossbar ADR-0001 §3).

### Decision 3: DI registration shape

Add an extension method to
`FrameFlowPlaybackServiceCollectionExtensions`:

```csharp
public static IServiceCollection AddFrameFlowChannelVideoSink(
    this IServiceCollection services,
    out ChannelVideoSink sink,
    ChannelVideoSinkOptions? options = null);
```

The `out` parameter mirrors the
`AddFrameFlowSdlVideoSink(out var sink, ...)` pattern landed in
`a193260` and gives the caller a handle to the sink for `ReadAllAsync`
without an extra `provider.GetRequiredService<ChannelVideoSink>()` round
trip.

The extension constructs a default `CpuFramePool` (consistent with
`AddFrameFlowAvaloniaVideoSink` / `AddFrameFlowSdlVideoSink`) and
registers the sink as `IVideoSink`, the pool as `IFramePool`, and the
concrete `ChannelVideoSink` so callers can resolve it later if they
prefer that pattern.

### Decision 4: No changes to existing types or workers

`PlaybackController`, `PlaybackSession`, `PipelineController`,
`AvaloniaVideoSink`, `SdlVideoSink`, the demux/decode/sync pipeline,
the state machines, and the existing integration tests are all
unchanged. `ChannelVideoSink` is just another `IVideoSink`
implementation — the runtime treats it identically to the Avalonia or
SDL sinks.

### Decision 5: Promote the multicast example's bridge

After `ChannelVideoSink` lands, refactor
`FrameFlow.Examples.AvaloniaMulticast.BroadcastBridgeSink` to either:

- Delete and replace with `ChannelVideoSink`; or
- Keep as a thin subclass that adds the multicast-example-specific
  diagnostics surface (overflow-on-format-change, etc.).

Option 1 is preferred — the example becomes a smaller, more direct
demonstration of "FrameFlow + Crossbar."

## Consequences

### Positive

- Crossbar consumers of FrameFlow get a one-line wire-up:
  `services.AddFrameFlowChannelVideoSink(out var source); …;
  await source.ReadAllAsync(ct).AsPipeline().Broadcast(...).RunAsync(ct);`.
  No hand-rolled bridge.
- Consumers building any other pull-shaped processing graph (custom
  recording pipelines, frame analyzers, encoder chains) get the same
  benefit. The adapter is no longer a per-consumer concern.
- The decision is fully reversible. If a future architectural review
  decides a pull-native playback design is worth the cost,
  `ChannelVideoSink` can stay as a convenience wrapper over the new
  pull surface; consumers don't have to migrate.
- `ChannelOverflowMode` makes the buffering policy explicit at
  construction time. Today's `IVideoSink` implementations bake the
  policy into their internals (the `Interlocked.Exchange` slot is
  drop-incoming with capacity 1, hard-coded). The new sink lets
  consumers pick.

### Negative

- New public type to maintain. `IVideoSink` already had two known
  implementations (Avalonia, SDL); now three.
- The buffering policy choices (capacity, overflow mode) become user-
  facing API. Future changes to defaults are minor breaking changes
  per ADR-0027 §SemVer. Mitigated by sensible defaults
  (capacity 1, drop-incoming) matching existing sink semantics.
- Slight redundancy with `BroadcastBridgeSink` until that example is
  refactored. Tracked in Decision 5.

### Neutral

- This ADR does not commit to or preclude a future native pull API.
  It explicitly defers that question, and the channel-buffered approach
  is compatible with either future direction.
- Audio is unchanged. Audio remains push-shaped at the device
  boundary; the pull video API and the push audio API coexist as they
  do in the existing playback session (and as they would in any
  future pull-native video design).

## Alternatives considered

### A. Native pull-shaped playback (`PlaybackSession.VideoFrames(ct)`)

Discussed at length in §Context. ~1500–2500 lines of work for an API
that, from the consumer's perspective, is indistinguishable from the
channel-buffered approach. Rejected on cost-vs-benefit grounds. May
revisit if a concrete benefit emerges that this ADR cannot capture.

### B. Keep status quo; document the bridge pattern

Leave consumers to write their own `IVideoSink → IAsyncEnumerable`
adapters; provide guidance in `docs/patterns/`. Rejected because the
adapter is mechanical, the policy choices are repeated, and the
implementation diverges across consumers (each one chooses different
capacity / overflow behavior, often without thinking).

### C. Add an `IPlaybackController.VideoFramesAsync(ct)` method

Bypass the sink registration and expose the iterator directly on the
controller. Rejected because it requires either:

1. Promoting one specific `IVideoSink` to "the canonical" status (a
   `ChannelVideoSink` registered automatically), forcing consumers
   who don't want pull behavior to opt out; or
2. Introducing a parallel pipeline path inside the controller, which
   is the native-pull option (Alternative A) under a different name.

The current proposal keeps `IVideoSink` as the canonical contract and
makes pull-shaped consumption a sink-level concern, which is the
right level of abstraction.

## References

- [`docs/adr/ADR-0025-video-sink-and-frame-pool-architecture.md`](ADR-0025-video-sink-and-frame-pool-architecture.md):
  the existing IVideoSink + IFramePool design that this ADR builds on.
- [`docs/adr/ADR-0024-playback-controller-as-public-api-surface.md`](ADR-0024-playback-controller-as-public-api-surface.md):
  the controller-level API stability commitment that this ADR
  preserves.
- `BroadcastBridgeSink`: the per-example adapter that motivated this ADR.
  It lived at `examples/FrameFlow.Examples.AvaloniaMulticast/BroadcastBridgeSink.cs`
  and is **no longer in the tree** — the AvaloniaMulticast example was removed
  when [ADR-0030](ADR-0030-unify-frame-contracts-with-crossbar.md) unified the
  frame contracts, which is the replacement this entry anticipated.
- Crossbar ADR-0001:
  the substrate this ADR's consumers most often want to plug into.
