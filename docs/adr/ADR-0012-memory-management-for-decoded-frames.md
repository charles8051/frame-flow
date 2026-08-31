# ADR-0012: Memory Management for Decoded Frames

## Status

Accepted — **amended 2026-05-12** for audio buffers (see [Amendment](#amendment-2026-05-12-audio-buffers-are-refcounted) below).

## Context

FrameFlow's decoded media contracts carry pixel and audio data across subsystem boundaries:

- `DecodedVideoFrame` holds `ReadOnlyMemory<byte>` for pixel data
- `PcmAudioBuffer` holds `ReadOnlyMemory<short>` for PCM samples

At 1080p BGRA, a single video frame is approximately 8 MB. At 4K, it is approximately 33 MB. At decode rates of 24–60 fps, this means hundreds of megabytes per second of frame buffer allocations.

These buffers cross the decode-to-queue-to-presenter boundary via `Channel<T>` (ADR-0009). The Avalonia presenter will copy frame data into a `WriteableBitmap`, adding another copy. Audio blocks are smaller but still high-frequency.

The .NET garbage collector handles large allocations on the Large Object Heap (LOH), which fragments over time with frequent allocate-and-free patterns. Without pooling, sustained playback will cause GC pressure and potential latency spikes visible as frame drops or audio glitches.

ADR-0005 requires that native FFmpeg pointers stay within native-owning layers. The managed buffers that cross queue boundaries must be purely managed memory, not pinned native frames.

ADR-0004 prioritizes software-first correctness before optimization. The memory management strategy must support correctness first, with pooling as an optimization that does not change the API contract.

## Decision

### IMemoryOwner\<T\> as the buffer contract

Decoded frame and audio buffers will be backed by `IMemoryOwner<T>` from `System.Buffers`. This interface represents a block of memory with explicit ownership semantics and deterministic disposal.

```csharp
public readonly struct DecodedVideoFrame : IDisposable
{
    public IMemoryOwner<byte> PixelData { get; }
    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }
    public TimeSpan Pts { get; }

    public void Dispose() => PixelData.Dispose();
}
```

The `IMemoryOwner<T>` contract means:

- The holder of the frame owns the memory and is responsible for disposing it
- Disposal returns the memory to its source (pool or GC, depending on implementation)
- `Memory<T>` / `ReadOnlyMemory<T>` can be obtained from the owner for read access without transferring ownership

### Explicit ownership transfer

Ownership transfers at well-defined points:

1. **Decoder** allocates buffer via pool, fills it, wraps in frame struct → owns until enqueue
2. **Channel\<T\>.Writer** receives the frame → ownership transfers to the queue
3. **Channel\<T\>.Reader** dequeues the frame → ownership transfers to the consumer (presenter or audio sink)
4. **Presenter/Sink** copies data to its output surface → disposes the frame, returning the buffer to the pool

Each stage has exactly one owner. There is no shared or reference-counted ownership.

### Frame pool abstraction

FrameFlow will define an internal pool abstraction for allocating frame buffers:

```csharp
internal interface IFrameBufferPool
{
    IMemoryOwner<byte> RentVideoBuffer(int width, int height, PixelFormat format);
    IMemoryOwner<short> RentAudioBuffer(int sampleCount, int channelCount);
}
```

The pool interface allows the implementation to evolve (simple allocation → ArrayPool → custom pool) without changing the decoder or presenter code.

### Simple initial implementation

The v1 pool implementation will use `MemoryPool<T>.Shared` (backed by `ArrayPool<T>.Shared`) for buffers that fit, and direct allocation for oversized buffers. This provides basic reuse without custom pool complexity.

```csharp
internal sealed class SharedMemoryFramePool : IFrameBufferPool
{
    public IMemoryOwner<byte> RentVideoBuffer(int width, int height, PixelFormat format)
    {
        int size = width * height * format.BytesPerPixel;
        return MemoryPool<byte>.Shared.Rent(size);
    }
}
```

### Correctness without pooling

The system must work correctly even with a trivial non-pooling allocator. This is the baseline for testing — if frame lifecycle is correct, pooling is purely an optimization. Tests should verify:

- every allocated frame is eventually disposed exactly once
- no frame is read after disposal
- disposal is safe to call multiple times (idempotent)

## Consequences

### Positive

- `IMemoryOwner<T>` is a standard .NET abstraction, familiar to the ecosystem
- Explicit single-owner semantics prevent use-after-free and double-free bugs
- The pool abstraction allows optimization without API changes
- `MemoryPool<T>.Shared` provides immediate LOH pressure reduction with no custom code
- Correctness can be validated independently of pooling behavior
- The pattern composes well with `Channel<T>` — enqueue transfers ownership cleanly

### Negative

- Every consumer of decoded frames must remember to dispose them; forgetting causes memory leaks (or pool exhaustion)
- `IMemoryOwner<T>.Memory` may return a buffer larger than requested (ArrayPool rounds up); consumers must use the frame's dimension metadata, not the buffer length
- Struct-based frame types with `IDisposable` require care to avoid accidental copies that dispose the same owner twice
- Pool tuning (bucket sizes, max retained buffers) will need attention once real workloads are profiled

## Alternatives considered

### Raw byte arrays without pooling

Rejected because sustained 1080p+ playback at 30–60 fps will allocate hundreds of megabytes per second on the LOH, causing GC pressure and visible playback glitches. While ADR-0004 says software-first, it does not mean ignoring known performance cliffs.

### Pinned native buffers crossing the queue boundary

Rejected because ADR-0005 requires native pointers to stay within native-owning layers. Passing `AVFrame*` pointers through `Channel<T>` would violate subsystem boundaries and make the presenter and audio sink depend on native frame lifecycle.

### Reference-counted buffers

Rejected for v1 because reference counting adds complexity (atomic increments, shared ownership tracking) that is not needed when the ownership chain is linear: decoder → queue → presenter → dispose. If future features require shared frame access (e.g., seeking while presenting), reference counting can be added behind the same `IMemoryOwner<T>` contract.

### Unsafe memory-mapped buffers

Rejected as premature. Memory-mapped I/O is relevant for zero-copy GPU upload paths but adds significant platform-specific complexity. The `IMemoryOwner<T>` abstraction can wrap memory-mapped regions later without changing the consumer contract.

## Amendment (2026-05-12): Audio buffers are refcounted

### Why amend

The original ADR rejected reference-counted buffers "for v1" with this qualifier in [Reference-counted buffers](#reference-counted-buffers):

> *If future features require shared frame access (e.g., seeking while presenting), reference counting can be added behind the same `IMemoryOwner<T>` contract.*

Two such features have arrived:

1. **`IVideoFrame.AddRef()`** has been on the public video surface since the playback layer landed — pooled CPU video frames already participate in refcounted lifecycles (`Playback.CpuVideoFrame` is the canonical implementation). The single-owner stance, in practice, only ever applied to audio.
2. **Audio fan-out is now routine**: live captioning teeing audio into both speakers and Whisper, future recording-while-playback paths, real-time analyzers running alongside a sink. Each of these wants the same buffer to reach two consumers. Without refcounting, the options were buffer cloning (memcpy + pool churn per fan-out point) or an asymmetric "caller-retains" sink surface (`IAudioSink.WriteAsync`) that contradicted the rest of the pipeline's "sink owns" contract.

Crossbar made the call upstream in commit `9b7f039` (2026-05-10, unpublished — see the note on Crossbar citations in the [ADR index](README.md)), renaming `IAudioFrame` → `IAudioBuffer` and adding `AddRef`. Its commit message stated the FrameFlow follow-through plainly: *"FrameFlow's PcmAudioBlock migration will formally amend FrameFlow ADR-0012's single-owner stance for audio buffers in a future ADR."* This amendment is that follow-through; the type renamed itself to `PcmAudioBuffer` the next day in `fdafc5c` but never adopted the new contract.

### What changes

- `PcmAudioBuffer` implements `Crossbar.IAudioBuffer`. Adds `Timestamp` / `Duration` / `ChannelCount` / `FrameCount` / `SampleFormat` / `MemoryDomain` properties matching the substrate; legacy properties (`PresentationTime`, `Channels`, `SampleCount`, `Samples`) stay as aliases.
- A `_refCount` field replaces the implicit single-owner assumption. `AddRef()` increments via CAS; `Dispose()` decrements and returns the wrapped `IMemoryOwner<short>` to `MemoryPool<short>.Shared` only when the count reaches zero.
- Over-dispose (calling `Dispose()` more times than `AddRef()`) is a no-op rather than a double-return-to-pool, preserving the legacy "single `Dispose` returns it to the pool" semantics that all pre-migration call sites assume.
- `AudioPipelineExtensions.TeeTo` uses `AddRef` + the audio sink's `FrameConsumer<IAudioBuffer>` invocation (per Crossbar ADR-0010, originally written here as `IFrameSink.PresentAsync` before the substrate cleanup) instead of `IAudioSink.WriteAsync` — one ownership model, no buffer clone.

### What's unchanged

- Video buffers were already refcounted; nothing on that side moves.
- The `IMemoryOwner<T>` contract from this ADR is preserved as the pool-facing interface. Refcounting is wrapped around it on `PcmAudioBuffer`, not woven into the pool.
- `IAudioSink.WriteAsync`'s caller-retains-ownership surface stays available for one migration cycle; callers should prefer `PresentAsync` (the Crossbar sink contract). A future ADR will retire `WriteAsync` once the public surface migration is done.
- The "exactly one owner per stage" rule from this ADR's [Explicit ownership transfer](#explicit-ownership-transfer) section still applies to single-owner stages. Refcounting is opt-in: a stage that wants shared access calls `AddRef`; everything else flows through unchanged with a single owner.

### Trade-off accepted

The original concern with refcounting was complexity — atomic increments, shared ownership tracking, the possibility of stuck refcounts leaking buffers. The complexity is real but bounded: one `Interlocked.CompareExchange` CAS loop in `AddRef`, one `Interlocked.Decrement` in `Dispose`, ~30 lines total. Audio fan-out has reached the threshold the original ADR cited as the precondition for revisiting this decision.
