# ADR-0030: Unify Frame Contracts with Crossbar Primitives

**Status:** Accepted; type identities partially superseded 2026-05-15
by Crossbar ADR-0010 (consumer-function unification) and
Crossbar ADR-0012 (explicit conversions over implicit capability
negotiation).

> **Update 2026-05-15 / 16.** The Crossbar substrate this ADR
> unified onto no longer exposes `IFrameSink<TFrame>` or
> `SupportedMemoryDomains`. The substrate's single sink concept is
> now the `FrameConsumer<TFrame>` delegate; resource-owning sinks
> (`IVideoSink`, `IAudioSink`) are standalone `IAsyncDisposable`
> interfaces with a `FrameConsumer<TFrame> Consumer { get; }`
> property. The unification this ADR delivered is intact — frames
> still flow through Crossbar's `FramePipeline<TFrame>`, operators
> are still Crossbar-defined — but the table below's "Shape" column
> for `IVideoSink` / `IFrameSink<T>` is obsolete. The actual current
> shapes:
>
> | Symbol | Current (post-2026-05-15) |
> |---|---|
> | `Crossbar.IFrameSink<TFrame>` | Deleted entirely |
> | `IVideoSink` | `IAsyncDisposable` with `FramePool` + `Consumer` + `OnFormatChangedAsync` + diagnostics |
> | `IAudioSink` | `IAsyncDisposable` with `Capabilities` + `Consumer` + `Activate/Pause/Resume/DeactivateAsync` + diagnostics |
> | `SupportedMemoryDomains` | Removed everywhere; conversions are explicit `Transform` operators |
>
Initial migration shipped 2026-05-10; the
controller-level `FramePipeline` accessor is a deferred follow-up,
see "Migration path" below.
**Date:** 2026-05-10
**Supersedes:** ADR-0015 (GPU-resident frame pipeline extensibility — the seam moves to Crossbar's frame contract). Partially supersedes ADR-0025 (type identities only; the frame-pool / sink-owns-pool architecture is preserved). Builds on, does not supersede, ADR-0029 (its `ChannelVideoSink` proposal landed as the canonical push→pull bridge during this migration).
**Related:** ADR-0012 (memory management for decoded frames), ADR-0016 (Avalonia presenter — already superseded by 0025), ADR-0018 (SDL presenter), ADR-0022 (long-lived workers), ADR-0023 (hierarchical state machine), ADR-0024 (PlaybackController as public API), ADR-0026 (state-bound worker lifecycle binding)

## Context

### What we have today

FrameFlow's frame and pipeline contracts evolved as a self-contained
design. Over the last ~30 ADRs we landed on:

| FrameFlow primitive | Defined in | Shape |
|---|---|---|
| `IVideoFrame` | ADR-0025 | `IDisposable` + `AddRef()`, `Pts`/`Duration`/`Width`/`Height`/`Format`/`MemoryDomain`, optional `AsCpu()` / forced `ToCpu()` |
| `FrameMemoryDomain` | ADR-0025 | enum: `{ Cpu }` (V1); not `[Flags]`, open set |
| `IFramePool` | ADR-0025 | `RentAsync(width, height, format, ct)`, `Return(frame)` |
| `IVideoSink` | ADR-0025 | `PresentAsync(frame, ct)`, owns an `IFramePool`, `SupportedMemoryDomains` |
| `IFrameConverter` | ADR-0025 | `ConvertAsync(source, targetPool, ct)` |
| `ChannelVideoSink` | ADR-0029 | `IVideoSink` whose `PresentAsync` writes to a `Channel<T>` for pull-shaped downstream consumption |

Meanwhile, in parallel,
Crossbar — explicitly
designed as a vendor-neutral graph runtime for media pipelines — grew
a strikingly similar surface:

| Crossbar primitive | Defined in | Shape |
|---|---|---|
| `IFrame` | Crossbar ADR-0001 | `Width`/`Height`/`Timestamp` (the minimal pipeline-routable shape) |
| `FrameMemoryDomain` | Crossbar `dcee5f1` | enum: `{ Cpu, Gpu }`; not `[Flags]`, open set |
| `IFrameSink<TFrame>` | Crossbar ADR-0001 | `PresentAsync(frame, ct)`, `SupportedMemoryDomains` |
| `FramePipeline<TFrame>` | Crossbar ADR-0001 | `IAsyncEnumerable<FramePacket<TFrame>>` substrate with `Transform`/`Enrich`/`Observe`/`Broadcast` |
| `FrameChannelOptions` + `FrameOverflowPolicy` | Crossbar core | capacity + overflow policy for fan-out channels |
| `ITensor` + `CpuTensor<T>` + `CpuTensorPool` | Crossbar ADR-0003 | refcounted (CAS-loop `AddRef`), pool-backed, `Outstanding` / `TotalRented` / `TotalReturned` accounting |
| `ICudaTensor` + `CudaTensor<T>` + `CudaTensorPool` | Crossbar ADR-0004 | GPU-resident sibling of CpuTensor; `FrameMemoryDomain.Gpu` |

The convergence is striking. We independently invented:

- The same enum (`FrameMemoryDomain`, with the same casing).
- The same refcount-and-pool ownership model (`AddRef` + `Dispose`,
  pool-rents, dispose-returns).
- The same sink contract (`PresentAsync` + `SupportedMemoryDomains`).
- The same backpressure-via-pool inversion (sink provides the pool;
  decoder rents from it).
- The same fan-out shape (a buffered Channel with capacity + overflow
  policy choices — `ChannelVideoSink` from ADR-0029 vs.
  `FrameChannelOptions` in Crossbar core).
- The same domain-boundary converter pattern (`IFrameConverter` vs.
  Crossbar operators that bridge domains).

ADR-0029's motivation makes the parallel evolution explicit. It says,
in essence: *FrameFlow is push-shaped, Crossbar is pull-shaped, so
ship a bridge sink to glue them.* That bridge is correct in isolation,
but the deeper truth is that we have two *frameworks* doing the same
job. The bridge papers over a duplication that doesn't need to exist.

### What we don't have

A consumer. As of this ADR, no project depends on FrameFlow's public
surface. The collapse cost is paid against a clean field; the cost of
waiting is paid against every future consumer, plus every cross-
framework bridge that would otherwise be needed.

### Why this matters beyond aesthetics

The architectural payoff is concrete. Two future directions both
benefit:

1. **Hardware-decoded video that's also a compute tensor.** When
   Phase 09 picks up NVDEC, the resulting frame can be allocated as a
   single object that simultaneously satisfies `ICudaTensor` (for
   compute, ORT inference, custom kernels) *and* a graphics-texture
   contract (for presenter consumption via CUDA-D3D11 / CUDA-GL
   interop). No bridge sink. No `ReadbackToCpu` fallback. The same
   `CUdeviceptr` that runs the YOLO inference is the texture that
   renders to screen.
2. **One memory-domain enum, one frame contract, one sink contract.**
   Periphery's `ICameraFrame`, FrameFlow's decoded video frame, and
   future device frames all flow as `Crossbar.IFrame` through one
   substrate. Cross-source pipelines (camera + file overlay, multi-
   camera fusion, captured-video-fed inference) become
   first-class compositions instead of bespoke adapters.

### What collapsing actually entails

FrameFlow loses its parallel-universe primitives and adopts
Crossbar's. The video-specific extensions (PixelFormat, planar data
layout, color-space metadata) stay in FrameFlow — they're not
substrate concerns. The state machine, playback controller, demux/
decode workers, and FFmpeg interop layer are unchanged; only the
output edge — what crosses from playback into a consumer — changes
identity.

## Decision

### 1. Adopt `Crossbar.IFrame` as the substrate frame contract

`FrameFlow.IVideoFrame` is recast as a *refinement* of
`Crossbar.IFrame` plus a parallel refcount interface, rather than a
standalone abstraction:

```csharp
namespace FrameFlow.Media;

public interface IVideoFrame : Crossbar.IFrame, IDisposable
{
    // Crossbar.IFrame already gives us Width, Height, Timestamp.
    // (FrameFlow's "Pts" is Timestamp; "Duration" is FrameFlow-specific
    // and stays on this interface.)

    TimeSpan Duration { get; }
    PixelFormat Format { get; }
    Crossbar.FrameMemoryDomain MemoryDomain { get; }

    IVideoFrame AddRef();

    CpuFrameData? AsCpu();
    CpuFrameData ToCpu();  // forced-copy fallback
}
```

**Implication for Crossbar:** Crossbar's existing `IFrame` is the
minimal Width/Height/Timestamp shape. That's correct — it stays
minimal. The refcount surface is *not* hoisted onto `Crossbar.IFrame`
itself, because Crossbar's existing operators don't need it
(`FramePacket<TFrame>` does the bookkeeping at the packet level per
Crossbar ADR-0001 §2). Refcount on `IVideoFrame` is a FrameFlow-side
concern, matching how `Crossbar.ITensor.AddRef` is a tensor-side
concern. Both refcount surfaces coexist; the substrate doesn't pick
one.

### 2. Drop `FrameFlow.FrameMemoryDomain`; use `Crossbar.FrameMemoryDomain`

The enum is literally the same shape with the same name. FrameFlow's
declaration is deleted; every reference site rebinds to
`Crossbar.FrameMemoryDomain`. The current FrameFlow enum has only
`Cpu`; Crossbar's has `Cpu` and `Gpu`. The CPU-only code path is
unaffected; FrameFlow simply gains the `Gpu` value it would have had
to add anyway for Phase 09.

### 3. Recast `IVideoSink` as `Crossbar.IFrameSink<IVideoFrame>`

`FrameFlow.IVideoSink` is replaced by an alias-shaped specialization:

```csharp
namespace FrameFlow.Media;

// IVideoSink is "an IFrameSink for IVideoFrame, with extra hooks
// FrameFlow needs (format changes, frame pool ownership)."
public interface IVideoSink : Crossbar.IFrameSink<IVideoFrame>, IAsyncDisposable
{
    IFramePool FramePool { get; }
    ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct);
}
```

`Crossbar.IFrameSink<TFrame>` already gives us `PresentAsync` and
`SupportedMemoryDomains`. The FrameFlow-specific surface
(`FramePool`, `OnFormatChangedAsync`) layers on top.

### 4. Pull-shape consumption via `ChannelVideoSink` (revised scope)

The original draft of this ADR proposed adding a
`FramePipeline<IVideoFrame> VideoFrames` accessor to
`IPlaybackController` and removing ADR-0029's `ChannelVideoSink`
proposal. On implementation the cost of rearchitecting the playback
worker loop to source from a pipeline (rather than push to a sink)
exceeded the "while it's cheap" mandate, and would have required
coordinated changes with ADR-0022 (long-lived workers), ADR-0023
(state machine), and ADR-0026 (lifecycle binding) — all of which
are stable.

The revised decision: **ship ADR-0029's `ChannelVideoSink` and rely
on `Crossbar.FramePipelineExtensions.AsPipeline()` at the consumer
edge.** The `ChannelVideoSink` lives in `FrameFlow.Playback`, is
registered as any other `IVideoSink`, and exposes
`IAsyncEnumerable<IVideoFrame>` via `ReadAllAsync(ct)`. Consumers
pipe directly into a Crossbar pipeline:

```csharp
services.AddFrameFlowChannelVideoSink(out var source, framePool);
// later, on a background task:
await source
    .ReadAllAsync(ct)
    .AsPipeline()              // FramePipeline<IVideoFrame>
    .Broadcast(...)
    .RunAsync(ct);
```

This achieves the architectural goal (no per-consumer bridge code;
Crossbar's pipeline operators apply directly) without the playback-
runtime surgery. The push-shape sink registration pattern continues
to work for Avalonia / SDL.

**Deferred follow-up:** a `FramePipeline` accessor directly on
`IPlaybackController` (or a controller-internal teeing model that
serves multiple consumers concurrently) remains a viable future
direction. It would supersede the bridge-sink pattern entirely; that
work belongs in its own ADR when a concrete use case forces it.

### 5. `IFramePool` keeps its FrameFlow-specific shape

`Crossbar.CpuTensorPool` is a *tensor* pool — element-typed, shape-
parameterized, no concept of PixelFormat. FrameFlow's `IFramePool`
stays as the video-frame pool — width/height/format-parameterized.
Both follow the same pattern (rent / return / refcount / pool-
disposal-doesn't-invalidate-in-flight) and the same counter
discipline (`Outstanding`/`TotalRented`/`TotalReturned`). They share
*pattern*, not identity. This is correct — video frames and inference
tensors are different enough that a single pool API would be
awkward.

### 6. GPU video frames carry both protocols

When Phase 09 lands NVDEC (or any future hardware decoder), the
resulting GPU frame implements *both* `IVideoFrame` and
`Crossbar.Cuda.ICudaTensor`:

```csharp
public sealed class CudaVideoFrame
    : IVideoFrame, Crossbar.Cuda.ICudaTensor
{
    // From IVideoFrame: Width, Height, Timestamp, Format, AddRef, AsCpu, ...
    // From ICudaTensor: DevicePtr, DeviceOrdinal, Shape, Dtype, ...
    //
    // The CUdeviceptr is the texture's mapped device pointer
    // (via cuGraphicsD3D11RegisterResource / cuGraphicsGLRegisterImage).
    // The same allocation backs both protocols. No copies.
}
```

A presenter that consumes `IVideoFrame` calls `AsCpu()` (which on a
GPU frame returns null) and falls back to whatever the platform
texture path is. An inference operator that consumes `ICudaTensor`
gets the `CUdeviceptr` directly. Both are zero-copy on the happy
path.

The exact shape of the texture-handle surface
(`AsD3D11()` / `AsGL()` / etc.) is deferred — it's a Phase 09 design
question and ADR-0025's reservation of optional domain accessors on
`IVideoFrame` already covers the seam.

### 7. FrameFlow gains a `PackageReference` on Crossbar core

This is a real dependency change. FrameFlow currently has no
dependency on Crossbar. After this ADR, `FrameFlow.Media` (or
wherever `IVideoFrame` lives) takes `<PackageReference
Include="Crossbar" />`. That's load-bearing — it means FrameFlow's
package consumers transitively get Crossbar's pipeline runtime.
That's not a bug; it's the point. The minor cost is one extra
NuGet package in the dependency closure of any FrameFlow consumer.

The reverse direction (`Crossbar.Cuda.dll` referencing `FrameFlow.dll`
for `CudaVideoFrame`) does **not** happen — the GPU video frame type
lives in FrameFlow, not in Crossbar. Crossbar.Cuda exposes
`ICudaTensor` as the interface that FrameFlow's CudaVideoFrame
implements. Dependency arrow stays one-directional.

## Consequences

### Positive

- One memory-domain enum, one frame interface, one sink contract
  across FrameFlow, Periphery, and Crossbar. Cross-source pipelines
  (camera + decoded file + inference) compose without per-pair
  adapters.
- `BroadcastBridgeSink` and `ChannelVideoSink` (ADR-0029) — the
  push-to-pull adapter and its first-class promotion — both go away
  because the pull shape is the substrate's native shape. The
  playback controller exposes `FramePipeline<IVideoFrame>`
  directly.
- The zero-copy hardware-decode-to-inference path is built into a
  single GPU frame type, not assembled across two frameworks via
  glue code.
- Crossbar gets a real consumer, which is the most valuable
  validation a substrate library can have.
- Several FrameFlow-specific abstractions (the
  `FrameOverflowPolicy` equivalent in `ChannelVideoSinkOptions`, the
  bespoke pull adapter, the parallel `FrameMemoryDomain` enum) stop
  being maintained at all.

### Negative

- Concrete refactor work in FrameFlow: every reference to
  `IDecodedVideoFrame` (legacy) / `IVideoFrame` (ADR-0025) /
  `FrameMemoryDomain` / `IVideoSink` updates. Touches
  decoder output, channel types, sync coordinator, frame dropper,
  every presenter implementation. Mechanical but not zero.
- FrameFlow takes a hard `PackageReference` on Crossbar. Consumers
  that wanted *just* FrameFlow now pull Crossbar too. The
  reverse — Crossbar.Cuda consumers pulling FrameFlow — does **not**
  happen, because the GPU video frame type lives in FrameFlow.
- The CI matrix grows: FrameFlow tests now have a Crossbar version
  to track. SemVer policy across the two repos has to stay aligned
  (a Crossbar major bump could break FrameFlow consumers).
- ADR-0029 was authored *yesterday*. Superseding it the next day is
  uncomfortable but honest — the cost of that ADR's implementation
  hasn't been spent yet (it's "Proposed," no code change). Better to
  catch the duplication before the bridge lands than after.

### Neutral

- ADR-0012's `IMemoryOwner<T>` ownership story stays — `CpuFrameData`
  carries the planar bytes; `IVideoFrame.Dispose` returns them to the
  pool. The contract is preserved.
- ADR-0022 (long-lived workers), ADR-0023 (state machine),
  ADR-0024 (controller as public API), ADR-0026 (lifecycle binding)
  are all preserved. Playback's *control surface* and *lifecycle* are
  orthogonal to its *frame contract*; only the latter changes.
- ADR-0025's frame-pool inversion (sink provides pool, decoder
  rents from it, natural backpressure) stays. It's the right design.
  We're just relabeling the types.

## Migration path

The numbered steps below describe the migration. Steps 1-5, 7, and 8
shipped 2026-05-10 in one pass; step 6 was revised in scope (see
Decision §4 above) and step 9 is the deferred Phase 09 piece.

1. **Crossbar core shipped unchanged.** ✅ The substrate types
   (`IFrame`, `IFrameSink<TFrame>`, `FrameMemoryDomain`,
   `FramePipeline<TFrame>`) were already in Crossbar 0.1.0 shipped
   public API.
2. **FrameFlow.Media added `<PackageReference Include="Crossbar" Version="0.1.0" />`.** ✅
3. **`FrameFlow.Media.FrameMemoryDomain` deleted; rebound to
   `Crossbar.FrameMemoryDomain`.** ✅ `<Using Include="Crossbar" />`
   added to the 8 projects that touch the enum so existing references
   resolve to the substrate type without per-file `using` directives.
4. **`FrameFlow.Media.IVideoFrame` now extends `Crossbar.IFrame`.** ✅
   `Width` / `Height` / `Timestamp` come from the substrate;
   `IVideoFrame.Pts` aliases `Timestamp` via a default interface
   implementation so existing `Pts` callers are source-compatible.
5. **`FrameFlow.Media.IVideoSink` now extends
   `Crossbar.IFrameSink<IVideoFrame>`.** ✅ `PresentAsync` and
   `SupportedMemoryDomains` are inherited from the substrate; the
   FrameFlow-specific surface is `FramePool` + `OnFormatChangedAsync`.
6. **Pull-shape consumption: `ChannelVideoSink` in `FrameFlow.Playback`.** ✅
   (Revised from the original draft — see Decision §4.) The bridge
   sink lives in the library, configured via Crossbar's
   `FrameChannelOptions` and `FrameOverflowPolicy`, registered via
   `AddFrameFlowChannelVideoSink`.
7. **`BroadcastBridgeSink` deleted from the multicast example;
   rewired to `ChannelVideoSink`.** ✅ The example now uses
   `services.AddFrameFlowChannelVideoSink(out var bridge, framePool)`
   and reads via `bridge.ReadAllAsync(ct).AsPipeline().Broadcast(...)`.
8. **ADR-0015 marked "Superseded by ADR-0030." ADR-0025 marked
   "Accepted; type identities superseded by ADR-0030." ADR-0029
   marked "Accepted (implemented as `ChannelVideoSink`)."** ✅
9. **(Deferred to Phase 09)** Implement `CudaVideoFrame : IVideoFrame,
   ICudaTensor` when NVDEC support lands. Until then, V1 stays
   CPU-only and the multi-protocol GPU frame is just a documented
   commitment.

**Test results after the migration:** the full FrameFlow test suite
runs green — 745 tests passed across 7 test assemblies, 1 skipped,
0 failed.

**Deferred work (post-ADR-0030):**

- Controller-level `FramePipeline` accessor (`IPlaybackController.VideoFrames`).
  Requires playback-worker rearchitecture (see Decision §4); separate
  ADR when a concrete need surfaces.
- `CudaVideoFrame` for the hardware-decode zero-copy path. Lands with
  FrameFlow Phase 09 (NVDEC).

## Alternatives considered

### Keep parallel; rely on ADR-0029's bridge

This is the status-quo option. Both frameworks evolve independently;
the `ChannelVideoSink` (and `BroadcastBridgeSink`) bridges them.
Rejected because:

1. The bridge is *only* necessary because two frameworks invented
   parallel primitives. The architectural cost of maintaining two
   universes of "the same thing with different names" never goes
   away.
2. The hardware-decoded-frame-as-tensor zero-copy path requires
   either a `CudaVideoFrame` that satisfies both protocols (this
   ADR's direction) or a runtime copy + bridge (the alternative's
   direction). The collapsed design gets it for free; the parallel
   design pays for it forever.
3. Pre-consumer is the cheapest moment to collapse. Every future
   FrameFlow consumer that ships will hard-code the parallel-universe
   types and make the collapse more expensive.

### Partial collapse: only adopt `FrameMemoryDomain`

Rebind the enum, leave `IVideoFrame` / `IVideoSink` / `IFramePool` as
FrameFlow-native. This is the cheapest move that achieves *some*
unification. Rejected because the enum is the smallest piece of the
duplication and the least valuable to unify in isolation — the real
value is the *frame and sink contracts* flowing through a unified
pipeline. The half-measure pays the migration cost (mechanical
rename, dependency add) without buying the architectural win.

### Move Crossbar into FrameFlow

Make Crossbar a sub-project of FrameFlow rather than the other way
around. Rejected because Crossbar is the more general substrate
(it's already consumed by Periphery; it doesn't depend on FFmpeg; it
handles non-video data via `ITensor`). The dependency arrow goes
FrameFlow → Crossbar, not the reverse. Anything else would mean
FrameFlow's FFmpeg dependency transitively poisoning Crossbar
consumers who don't want video at all.

### Wait until a consumer materializes, then collapse retroactively

Defer this ADR; revisit when a real consumer hits the bridge cost.
Rejected because the rejection ground is the same as "keep parallel"
above — the collapse cost is monotonically increasing in number of
consumers, and "no consumers yet" is the *first time* we can do
this for free. Deferring saves nothing.

## References

- **Crossbar:**
  ADR-0001 — pipeline substrate and ownership;
  ADR-0002 — streaming sensor processing direction;
  ADR-0003 — tensor primitive;
  ADR-0004 — CUDA backend.
- **FrameFlow ADRs superseded or affected (above).**
- **The bridge that this ADR's adoption renders unnecessary:**
  `examples/FrameFlow.Examples.AvaloniaMulticast/BroadcastBridgeSink.cs`
  and the `ChannelVideoSink` proposal in ADR-0029.
