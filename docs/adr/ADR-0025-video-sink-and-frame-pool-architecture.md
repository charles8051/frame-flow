# ADR-0025: Video Sink and Frame Pool Architecture

**Status:** Accepted; type identities superseded by [ADR-0030](ADR-0030-unify-frame-contracts-with-crossbar.md)
**Date:** 2026-04-07
**Supersedes:** ADR-0016 (Avalonia presenter frame delivery strategy)
**Related:** ADR-0003 (audio-master sync), ADR-0005 (native resource ownership), ADR-0012 (memory management for decoded frames), ADR-0015 (GPU-resident frame pipeline extensibility, superseded by ADR-0030), ADR-0018 (SDL presenter and audio adapter), ADR-0022 (long-lived workers with pause gate), ADR-0024 (playback controller), ADR-0030 (unify frame contracts with Crossbar)

## Status note

The *architecture* this ADR established — pool-rented frames with
refcount, sink-owned `IFramePool`, sink-provides-pool inversion for
natural backpressure — remains correct and is preserved. The
"pipeline negotiation between decoder output domain and sink
supported domains" part was abandoned per Crossbar ADR-0012.

Two waves of superseding changes apply to the *type identities*:

**Wave 1 — ADR-0030 (2026-05-10).** Adopted Crossbar substrate:
- `FrameFlow.FrameMemoryDomain` → `Crossbar.FrameMemoryDomain`
- `IVideoFrame` extends `Crossbar.IFrame`
- `IVideoSink` extends `Crossbar.IFrameSink<IVideoFrame>` (so
  `PresentAsync` + `SupportedMemoryDomains` were inherited)

**Wave 2 — Crossbar ADR-0010 + ADR-0012 (2026-05-15).** Substrate cleanup:
- `Crossbar.IFrameSink<T>` deleted entirely. `IVideoSink` is now a
  standalone `IAsyncDisposable` interface that exposes
  `FrameConsumer<IVideoFrame> Consumer { get; }` (cached in each
  implementer's constructor as `Consumer = PresentAsync;`).
- `SupportedMemoryDomains` deleted everywhere. Domain conversion is
  an explicit pipeline operator (typically `Transform`) the consumer
  writes at the boundary; the substrate does not negotiate.

The frame-pool inversion + sink-owns-pool model still holds across
both waves. Method signatures changed; the architectural shape did
not.
## Context

### The current frame delivery model

FrameFlow currently delivers decoded video frames through a `Channel<DecodedVideoFrame>` from the video decoder to a presenter. The presenter (SDL or Avalonia) reads frames from the channel, applies sync timing, and renders them. Frames are CPU-resident `byte[]` buffers allocated by the decoder.

This works for software decoding but has fundamental limitations:

1. **The decoder allocates, the presenter copies.** Every frame requires at least one CPU-to-GPU upload when the presenter uses a GPU-backed surface (SDL texture, Avalonia WriteableBitmap). At 4K 60fps, this copy alone can consume the entire frame budget.

2. **Frame format is baked into the contract.** `DecodedVideoFrame` assumes CPU-resident planar data. A GPU-decoded frame (DXVA2, VAAPI, VideoToolbox) that is already a texture in VRAM has no way to express itself through this contract without an unnecessary GPU→CPU readback.

3. **No backpressure from the renderer.** The decoder allocates frames freely. If the renderer is slow, frames accumulate in the channel. There is no mechanism for the renderer to limit how many frames are in-flight, which prevents efficient surface pool reuse.

### The zero-copy insight: invert frame ownership

In a naive pipeline, the decoder allocates frames and the renderer copies them. For zero-copy, the **renderer provides a pool of surfaces** that the decoder fills in-place. The renderer displays the same surface directly. The surface returns to the pool when the renderer is done.

```
Traditional (2 copies):
  Decoder allocs CPU buf → copy → Renderer upload texture → copy → Swapchain

Zero-copy (0 copies):
  Renderer's texture pool → Decoder fills in-place → Renderer presents → back to pool
```

This inversion is how every production video player achieves zero-copy GPU rendering.

### FrameFlow's stated design principle

ADR-0015 establishes that FrameFlow should keep a seam for GPU-resident frame pipelines without implementing them in v1. The project's stated goal is **software-first correctness before hardware acceleration**.

This ADR designs the sink and frame pool architecture with GPU zero-copy as a future capability, while implementing only the CPU software path for v1.

## Decision

### 1. IVideoFrame — opaque handle with domain accessors

The frame contract is an opaque handle carrying metadata and optional per-domain accessors. It does **not** expose raw bytes as its primary interface.

```csharp
public interface IVideoFrame : IDisposable
{
    // ── Metadata (always available, zero cost) ────────
    TimeSpan Pts { get; }
    TimeSpan Duration { get; }
    int Width { get; }
    int Height { get; }
    PixelFormat Format { get; }
    FrameMemoryDomain MemoryDomain { get; }

    // ── Ref counting ──────────────────────────────────
    IVideoFrame AddRef();

    // ── Domain access ─────────────────────────────────
    CpuFrameData? AsCpu() => null;

    // ── Fallback: force CPU copy (slow but universal) ─
    CpuFrameData ToCpu();
}
```

**V1 implementation:** Only `AsCpu()` returns non-null. `ToCpu()` delegates to `AsCpu()`. The ref-counting infrastructure exists but the pool is CPU-backed.

**Why not add GPU accessors now:** The Obsidian design documents define `AsD3D11()`, `AsVulkan()`, `AsMetal()` accessors. These are correct for a production player, but adding them to `IVideoFrame` in v1 would be speculative — FrameFlow has no GPU decode path, no GPU texture pool, and no way to test these accessors. The interface should be extended when the first GPU backend is implemented, not before. ADR-0015 already reserves this seam.

### 2. CpuFrameData — the v1 data carrier

```csharp
public readonly record struct CpuFrameData(
    ReadOnlyMemory<byte> PlaneY,
    ReadOnlyMemory<byte> PlaneU,
    ReadOnlyMemory<byte> PlaneV,
    int StrideY,
    int StrideU,
    int StrideV,
    int Width,
    int Height
);
```

This replaces the current `DecodedVideoFrame` with a struct that carries planar data without owning the underlying memory. The memory is owned by the frame (which is owned by the pool).

### 3. IFramePool — bounded surface allocation with backpressure

```csharp
public interface IFramePool : IDisposable
{
    FrameMemoryDomain MemoryDomain { get; }

    ValueTask<IVideoFrame> RentAsync(
        int width, int height, PixelFormat format, CancellationToken ct);

    void Return(IVideoFrame frame);
}
```

The pool bounds the number of in-flight frames. When all frames are rented (held by the decoder, channel, or renderer), `RentAsync` blocks — this is **natural backpressure** from the renderer to the decoder. The decoder cannot allocate faster than the renderer can present.

**V1 implementation:** `CpuFramePool` backed by `ArrayPool<byte>` with a `SemaphoreSlim` to bound concurrent rentals. Pool size defaults to 3-4 frames (typical decode-ahead depth).

### 4. IVideoSink — the presentation destination

```csharp
public interface IVideoSink : IAsyncDisposable
{
    IFramePool FramePool { get; }

    ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct);

    ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct);

    IReadOnlyList<FrameMemoryDomain> SupportedMemoryDomains { get; }
}
```

The sink **owns the frame pool**. This is the key inversion: the sink knows what memory domain it prefers (CPU, D3D11, Vulkan, etc.) and provides a pool in that domain. The decoder rents from the sink's pool, fills the frame, and the sink presents the same frame — zero copy when domains match.

**V1 implementations:**

- `CpuVideoSink` — base implementation for CPU-backed presenters. Provides a `CpuFramePool`. The SDL and Avalonia presenters can extend or wrap this.
- `NullVideoSink` — drops all frames, returns dummy handles. Used for audio-only playback and testing.

### 5. IFrameConverter — domain boundary crossing

```csharp
public interface IFrameConverter : IDisposable
{
    ValueTask<IVideoFrame> ConvertAsync(
        IVideoFrame source,
        IFramePool targetPool,
        CancellationToken ct);
}
```

When the decoder's output domain does not match the sink's domain, a converter is inserted at the boundary. The converter rents from the target pool, performs the conversion, and returns the new frame. The source frame is not disposed by the converter — the caller retains ownership.

**V1 implementation:** `SwsFrameConverter` wrapping FFmpeg's `sws_scale` for pixel format conversion (e.g., YUV420P → RGBA8). Domain crossing (GPU→CPU or CPU→GPU) is deferred to v2.

### 6. Pipeline negotiation

When a playback session initializes, it negotiates the pipeline configuration:

```csharp
internal sealed record PipelineConfig
{
    public required IFramePool FramePool { get; init; }
    public required IFrameConverter? FrameConverter { get; init; }
}
```

The negotiation logic:

1. Query the sink's `SupportedMemoryDomains` (preference-ordered).
2. Check if the decoder's output domain matches any supported domain.
3. If matched: use the sink's `FramePool` directly (zero-copy path).
4. If not: create a pool for the decoder's domain and insert a converter.

**V1 simplification:** In v1, both the decoder and all sinks are CPU-only. Negotiation always selects the zero-copy CPU path. The negotiation code exists for structural correctness but the converter path is not exercised until GPU backends arrive.

### 7. Video renderer integration

The video renderer worker (from ADR-0022) receives the sink and optional converter:

```csharp
// Inside the video present worker loop
var now = clock.Position;
var delta = frame.Pts - now;

if (delta < -framePeriod)
{
    // Too late — drop
    stats.DroppedFrames++;
    frame.Dispose();  // returns to pool
    continue;
}

if (delta > halfFramePeriod)
    await Task.Delay(delta - renderMargin, ct);

if (converter is not null)
{
    using var converted = await converter.ConvertAsync(frame, sink.FramePool, ct);
    await sink.PresentAsync(converted, ct);
}
else
{
    await sink.PresentAsync(frame, ct);
}

frame.Dispose();  // returns to pool (sink may hold via AddRef)
```

### 8. Frame lifecycle

```
1. Renderer returns frame → pool (ref count hits 0)
2. Decoder rents frame from pool
3. Decoder fills frame with decoded data
4. Frame written to VideoFrames channel
5. Video renderer reads from channel
6. Renderer checks timing vs audio clock
7. Sink.PresentAsync — sink may AddRef if holding for vsync
8. frame.Dispose() — ref count decrements
9. When ref count hits 0 → frame returns to pool
```

## Pushback on the Obsidian Design

### GPU-specific domain accessors are premature

The Obsidian documents define `AsD3D11()`, `AsVulkan()`, `AsMetal()` on `IVideoFrame`, plus `D3D11FrameData`, `VulkanFrameData`, and `MetalFrameData` record structs. **These should not be added to the v1 contracts.**

Reasons:

1. **Untestable.** FrameFlow has no GPU decode pipeline and no CI with GPU access. Adding types that cannot be tested invites rot.
2. **API surface lock-in.** Once `D3D11FrameData` is public, its shape is frozen. GPU interop details (shared handles, queue family indices, subresource indexing) are highly specific to the HW decode implementation. Designing these structs without an implementation to validate against will produce the wrong API.
3. **Violates the project's own principle.** FrameFlow's README and ADR-0015 state: software-first correctness before hardware acceleration. Adding GPU types to core contracts contradicts this.

**The seam is sufficient.** `IVideoFrame` has a `MemoryDomain` property and default-null domain accessors. When the D3D11 backend is implemented, `AsD3D11()` can be overridden in a `D3D11VideoFrame` class in a platform-specific project. No changes to the core interface are needed.

### FrameMemory enum should not enumerate all possible GPU domains upfront

The Obsidian docs define `enum FrameMemory { Cpu, D3D11, Vulkan, Metal, OpenGL }`. In v1, only `Cpu` is meaningful. Enumerating GPU domains now creates the impression that they are supported.

**Decision:** V1 defines:

```csharp
public enum FrameMemoryDomain { Cpu }
```

Additional values are added when their backends are implemented. The enum is not `[Flags]` and is not a closed set — new backends add new values.

### MultiSink fan-out is deferred

The Obsidian docs describe a `MultiSink` that fans out to primary + secondary sinks with AddRef-based zero-copy. This is useful for simultaneous display + encoding but is beyond v1 scope. A single sink per session is sufficient initially.

## Consequences

### Positive

- Frame delivery becomes pool-based with natural backpressure, eliminating unbounded frame accumulation.
- The sink abstraction cleanly separates "where frames live" (pool) from "how they are displayed" (present).
- The architecture is GPU-ready without GPU-specific types in v1 core contracts.
- Pipeline negotiation exists structurally, ready for GPU paths without code changes to the negotiation logic itself.
- Existing presenters (SDL, Avalonia) can be refactored to implement `IVideoSink` incrementally.

### Negative

- The pool adds complexity compared to the current "decoder allocates, channel delivers" model.
- `IVideoFrame` ref counting requires discipline — every `AddRef` must have a matching `Dispose`.
- The negotiation step at session init is new infrastructure that does almost nothing in v1 (CPU→CPU is always the answer).

### Neutral

- ADR-0012's frame ownership rules are preserved and strengthened. Pool return replaces manual `ArrayPool.Return`.
- ADR-0005's native resource ownership is preserved. Frames cross channels as managed handles, not native pointers.
- ADR-0016 (Avalonia presenter frame delivery) is superseded by this more general sink model.
- ADR-0018 (SDL presenter) will be updated to implement `IVideoSink`.

## Migration Path

1. Define `IVideoFrame`, `IFramePool`, `IVideoSink`, `IFrameConverter` interfaces in `FrameFlow.Media`.
2. Implement `CpuFramePool`, `CpuVideoFrame`, `NullVideoSink` in `FrameFlow.Playback`.
3. Refactor `PlaybackSession` to use pool-based frame allocation instead of decoder-owned allocation.
4. Refactor SDL presenter to implement `IVideoSink`.
5. Refactor Avalonia presenter to implement `IVideoSink`.
6. Remove `DecodedVideoFrame` and the old channel-direct delivery path.

Each step is independently testable. The system is never in a half-migrated state.
