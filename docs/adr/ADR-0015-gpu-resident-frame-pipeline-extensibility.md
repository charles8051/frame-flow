# ADR-0015: GPU-Resident Frame Pipeline Extensibility

## Status

**Superseded by [ADR-0030](ADR-0030-unify-frame-contracts-with-crossbar.md)**

The GPU-extensibility seam this ADR proposed (an `IDecodedVideoFrame`
interface with `FrameMemoryKind` discriminator and `GpuVideoFrame`
sketched as a future texture-handle implementation) is taken over by
the unified Crossbar substrate. The frame interface is now
`FrameFlow.Media.IVideoFrame : Crossbar.IFrame`; the memory-kind
discriminator is `Crossbar.FrameMemoryDomain`; the GPU-resident frame
contract for hardware decode lands as a future type
(`CudaVideoFrame`) that implements both `IVideoFrame` and
`Crossbar.Cuda.ICudaTensor` so a single allocation backs both the
texture-presentation and compute-inference paths.

The body below is preserved as design archaeology — the *reasoning*
about why the seam must exist before the GPU work begins still holds.
Only the type identities change.

## Context

ADR-0012 establishes `IMemoryOwner<byte>` as the canonical buffer contract for decoded frames. The current `DecodedVideoFrame` record carries a `ReadOnlyMemory<byte>` CPU buffer — every frame is copied out of FFmpeg's native memory into a managed pool, queued through `Channel<T>`, and then copied again into the presenter's output surface (e.g., Avalonia `WriteableBitmap`). This is correct, simple, and the right v1 approach.

However, hardware-accelerated decoders (NVDEC, VAAPI, VideoToolbox, D3D11VA, MediaCodec) produce frames that already reside in GPU memory. The ideal path for these frames is:

```
HW decoder → GPU texture → compositor/presenter
```

with zero CPU round-trips. Copying a GPU frame to CPU and back is expensive — 4K BGRA at 60 fps is roughly 2 GB/s of memory bandwidth — and defeats the purpose of hardware acceleration.

Phase 09 is explicitly deferred and optional. But the frame contract (`DecodedVideoFrame`) is used at every boundary between decoder, queue, sync coordinator, and presenter. If we bake in a CPU-only concrete type now and it becomes pervasive across the codebase, retrofitting a GPU-aware path later will require a disruptive rewrite.

The question is: what is the smallest change we can make to the frame contract now that keeps v1 simple while leaving a clean seam for GPU-resident frames later?

## Decision

### Introduce IDecodedVideoFrame as the pipeline contract

The inter-component frame contract will be a thin interface rather than a concrete record:

```csharp
/// <summary>
/// Represents a decoded video frame that can be either CPU-resident
/// or GPU-resident. This is the type that flows through queues,
/// sync coordinators, and into presenters.
/// </summary>
public interface IDecodedVideoFrame : IDisposable
{
    int Width { get; }
    int Height { get; }
    TimeSpan PresentationTime { get; }
    FrameMemoryKind MemoryKind { get; }
}

public enum FrameMemoryKind
{
    Cpu,
    Gpu
}
```

### CPU frames implement the interface with the existing buffer contract

The software-path frame type remains simple and carries the pooled pixel buffer from ADR-0012:

```csharp
public sealed class CpuVideoFrame : IDecodedVideoFrame
{
    public IMemoryOwner<byte> PixelData { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat Format { get; }
    public TimeSpan PresentationTime { get; }
    public FrameMemoryKind MemoryKind => FrameMemoryKind.Cpu;

    public void Dispose() => PixelData.Dispose();
}
```

### GPU frames will carry an opaque texture handle (Phase 09)

When hardware acceleration is implemented, a GPU-resident frame type will be added:

```csharp
public sealed class GpuVideoFrame : IDecodedVideoFrame
{
    public nint TextureHandle { get; }
    public GpuDeviceContext DeviceContext { get; }
    public int Width { get; }
    public int Height { get; }
    public TimeSpan PresentationTime { get; }
    public FrameMemoryKind MemoryKind => FrameMemoryKind.Gpu;

    /// <summary>
    /// Falls back to CPU readback when a presenter cannot consume GPU frames directly.
    /// </summary>
    public CpuVideoFrame ReadbackToCpu() { ... }

    public void Dispose() { /* release GPU resource */ }
}
```

The exact shape of `GpuVideoFrame` and `GpuDeviceContext` will be defined in Phase 09. The important commitment now is the interface contract, not the GPU implementation details.

### Presenters accept the interface and fast-path on MemoryKind

`IVideoFramePresenter` will accept `IDecodedVideoFrame`:

```csharp
public interface IVideoFramePresenter : IAsyncDisposable
{
    ValueTask PresentAsync(IDecodedVideoFrame frame, CancellationToken cancellationToken = default);
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
```

A CPU-only presenter (the Avalonia WriteableBitmap path) pattern-matches or checks `MemoryKind` and calls `ReadbackToCpu()` if it receives a GPU frame. A GPU-aware presenter (e.g., OpenGL texture presenter) handles GPU frames directly and avoids the readback entirely.

This means:

- v1 software path: decoder produces `CpuVideoFrame` → presenter consumes `CpuVideoFrame`. No overhead from the interface indirection — the happy path is a single type check.
- Future GPU path: decoder produces `GpuVideoFrame` → GPU-aware presenter consumes directly. CPU-only presenters fall back to readback transparently.

### Queues and sync coordinators use the interface

`Channel<IDecodedVideoFrame>` replaces `Channel<DecodedVideoFrame>`. The sync coordinator, frame dropper, and any other pipeline stage that touches frames between decoder and presenter operates on `IDecodedVideoFrame`. These stages inspect `Width`, `Height`, and `PresentationTime` for scheduling decisions but never access pixel data directly — so they are agnostic to memory location.

### Ownership transfer semantics are unchanged

ADR-0012's single-owner transfer model applies identically:

1. Decoder allocates frame → owns it
2. Enqueue transfers ownership to the channel
3. Dequeue transfers ownership to the presenter
4. Presenter calls `Dispose()` after use

Whether `Dispose()` returns a CPU buffer to a pool or releases a GPU texture is an implementation detail of the frame type. The pipeline does not change.

## Consequences

### Positive

- The frame contract supports both CPU and GPU frames from the start without speculative GPU implementation
- v1 remains simple — `CpuVideoFrame` is the only concrete type; the interface adds negligible complexity
- Phase 09 can introduce `GpuVideoFrame` and GPU-aware presenters without touching the pipeline plumbing
- CPU-only presenters degrade gracefully via `ReadbackToCpu()` — no all-or-nothing GPU commitment
- Sync coordinator and queue logic stay memory-location-agnostic
- Pattern-match dispatching (`frame is CpuVideoFrame cpu`) is idiomatic C# and zero-cost at runtime

### Negative

- An interface adds one level of indirection vs. a sealed record — negligible for the frame-level granularity we operate at, but worth acknowledging
- `GpuVideoFrame` is speculative; the exact GPU interop shape may differ from what we sketch here
- Developers may be tempted to add GPU logic prematurely; discipline is needed to keep Phase 09 deferred
- `ReadbackToCpu()` on `GpuVideoFrame` hides a potentially expensive GPU→CPU copy behind a simple method call — callers must understand the performance implication

## Alternatives considered

### Keep DecodedVideoFrame as a concrete record, refactor later

Rejected because the frame type is referenced at every pipeline boundary. Changing it later would be a cross-cutting refactor affecting decoder output, channel types, sync coordinator, frame dropper, and every presenter implementation. Introducing the interface now is cheap; introducing it later is expensive.

### Use a generic pipeline (Channel\<T\> where T could be anything)

Rejected because it pushes type safety out of the pipeline. The interface gives us a named contract with known properties that pipeline stages can rely on (dimensions, PTS, memory kind) without downcasting to an unknown T.

### Abstract base class instead of interface

Viable but rejected because the frame types have no shared implementation worth inheriting. An interface is lighter, allows struct implementations if needed in the future, and avoids the single-inheritance constraint. `IDisposable` is the only shared behavior, and it is already on the interface.

### Discriminated union via OneOf or similar

Rejected because it adds a library dependency for something that C# pattern matching handles natively. `if (frame is CpuVideoFrame cpu)` is clear, zero-allocation, and does not require a third-party union type.

## References

- ADR-0005: Native resource ownership rules
- ADR-0006: Extension seams and future-proofing
- ADR-0012: Memory management for decoded frames
- Phase 09: Hardware acceleration and presenters
