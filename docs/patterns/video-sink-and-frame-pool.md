# Video Sink & Frame Pool — CPU-First Presentation Architecture

> **⚠ Historical design reference (pre-Crossbar redesign, 2026-05-15).**
> The architecture below predates Crossbar's substrate consolidation
> (Crossbar ADR-0010 consumer-function unification, Crossbar
> ADR-0012 explicit conversions). The bones — sink owns frame pool,
> pool provides backpressure, sinks present rented frames — are still
> accurate. The type-level shapes are not:
>
> | Body says | Current state |
> |---|---|
> | `IVideoSink : IAsyncDisposable` with a `PresentAsync` method | `IVideoSink : IAsyncDisposable` with `FrameConsumer<IVideoFrame> Consumer { get; }` property; implementers cache `Consumer = PresentAsync;` in their constructor |
> | `IReadOnlyList<FrameMemoryDomain> SupportedMemoryDomains` on `IVideoSink` | **Removed.** Memory-domain conversion is an explicit `Transform` operator at the call site |
> | `PipelineNegotiator.Negotiate(MediaInfo, IVideoSink)` chooses a CPU/GPU path | **Gone.** Pipeline shape is explicit — there is no negotiation step |
> | `Crossbar.IFrameSink<T>` as a base interface on `IVideoSink` | **Deleted from Crossbar.** Library-specific sink interfaces are standalone |
>
> Read this doc for the still-accurate frame-pool ownership / backpressure
> model. Read Crossbar ADR-0010 + ADR-0012 for the current sink/consumer
> shape.
>
Design reference for FrameFlow's video sink, frame pool, and pipeline
negotiation. V1 is CPU-only; the architecture supports zero-copy GPU paths
as a future extension without changing core contracts.

**Governing ADRs:**
- [ADR-0025](../adr/ADR-0025-video-sink-and-frame-pool-architecture.md) — sink & frame pool architecture
- [ADR-0005](../adr/ADR-0005-native-resource-ownership-rules.md) — native resource ownership
- [ADR-0012](../adr/ADR-0012-memory-management-for-decoded-frames.md) — frame memory management
- [ADR-0015](../adr/ADR-0015-gpu-resident-frame-pipeline-extensibility.md) — GPU extensibility seam

**Companion documents:**
- [playback-controller](playback-controller.md) — controller & session architecture
- [implementation-plan](implementation-plan.md) — phased refactor plan

---

## 1. The Problem: Decoder Allocates, Presenter Copies

FrameFlow currently delivers frames through a `Channel<IDecodedVideoFrame>` from
the video decoder to a presenter. The decoder creates `CpuVideoFrame` instances
backed by `IMemoryOwner<byte>` from an `ArrayPool`. The presenter
(`IVideoFramePresenter`) copies pixel data into a platform surface (SDL texture,
Avalonia `WriteableBitmap`).

This works but has three structural problems:

1. **Every frame requires a copy.** The presenter must upload CPU data to a
   GPU texture for rendering. At high resolution/framerate this copy can consume
   a significant portion of the frame budget.

2. **No backpressure from the presenter.** The decoder allocates frames freely.
   If the presenter is slow, frames accumulate in the channel with no bound on
   in-flight frame count.

3. **Frame format is baked in.** `CpuVideoFrame` assumes CPU-resident packed
   pixel data. A future GPU-decoded frame has no way to express itself through
   this contract.

The fix is to **invert frame ownership**: the presenter provides a pool of
surfaces, the decoder fills them in-place, the presenter displays the same
surface directly.

```
Current (1+ copies):
  Decoder allocs CpuVideoFrame → channel → Presenter copies to texture

Inverted (0 copies when domains match):
  Sink's FramePool → Decoder fills in-place → Sink presents directly → back to pool
```

---

## 2. Abstraction Stack

```
┌──────────────────────────────────────────────────────────────┐
│  IVideoSink                                                   │
│  (the destination: SDL window, Avalonia surface, null sink)   │
│  Owns the frame pool. Presents frames.                        │
├──────────────────────────────────────────────────────────────┤
│  IVideoFrame                                                  │
│  (opaque handle with metadata + CPU data accessor)            │
│  Ref-counted. Returns to pool on final release.               │
├──────────────────────────────────────────────────────────────┤
│  IFramePool                                                   │
│  (bounded allocation: decoder rents frames from here)         │
│  Blocks when all frames are in-flight (backpressure).         │
├──────────────────────────────────────────────────────────────┤
│  IFrameConverter                                              │
│  (pixel format conversion: YUV420P → RGBA, etc.)              │
│  CPU-only in v1. Identity/no-op when formats match.           │
└──────────────────────────────────────────────────────────────┘
```

---

## 3. IVideoFrame — Opaque Handle

The frame does **not** expose raw bytes as its primary interface. It is an
opaque handle with metadata and an accessor for the CPU memory domain.

```csharp
public interface IVideoFrame : IDisposable
{
    // ── Metadata (always available, zero cost) ────────────
    TimeSpan          PresentationTime { get; }
    TimeSpan          Duration         { get; }
    int               Width            { get; }
    int               Height           { get; }
    PixelFormat        Format          { get; }
    FrameMemoryDomain MemoryDomain     { get; }

    // ── Ref counting ──────────────────────────────────────
    // Multiple consumers can hold the same frame simultaneously.
    // Frame returns to pool on final release.
    IVideoFrame AddRef();

    // ── CPU memory access ─────────────────────────────────
    // Returns non-null for CPU-domain frames.
    // Future GPU backends will add domain-specific accessors
    // (e.g., AsD3D11()) on their own frame implementations
    // without changing this interface (per ADR-0025).
    CpuFrameData? AsCpu() => null;

    // ── Fallback: force CPU copy (always works) ───────────
    // For CPU frames, delegates to AsCpu().
    // For future GPU frames, performs GPU readback (slow).
    CpuFrameData ToCpu();
}
```

### 3.1 CpuFrameData — the V1 Data Carrier

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

This carries planar YUV data without owning the underlying memory. The memory
is owned by the frame, which is owned by the pool.

For packed formats (RGBA, BGRA), `PlaneY` carries all pixel data and
`PlaneU`/`PlaneV` are empty.

### 3.2 Enums

```csharp
// Already exists in FrameFlow.Media as FrameMemoryKind.
// Rename to FrameMemoryDomain for clarity, or keep existing name.
// V1 only uses Cpu.
public enum FrameMemoryDomain { Cpu }

// Already exists in FrameFlow.Media:
// PixelFormat { Bgra32, Rgba32, Yuv420P, Nv12 }
```

---

## 4. IFramePool — Bounded Allocation with Backpressure

```csharp
public interface IFramePool : IDisposable
{
    /// <summary>
    /// The memory domain this pool allocates in.
    /// </summary>
    FrameMemoryDomain MemoryDomain { get; }

    /// <summary>
    /// Rent a frame surface. Blocks if all surfaces are in use
    /// (backpressure: decoder waits for presenter to release a frame).
    /// </summary>
    ValueTask<IVideoFrame> RentAsync(
        int width, int height, PixelFormat format, CancellationToken ct);

    /// <summary>
    /// Called automatically when frame ref count hits 0.
    /// Returns the surface to the pool for reuse.
    /// </summary>
    void Return(IVideoFrame frame);
}
```

### 4.1 CpuFramePool (V1 Implementation)

```csharp
internal sealed class CpuFramePool : IFramePool
{
    private readonly SemaphoreSlim _available;   // bounds concurrent rentals
    private readonly int _poolSize;              // typically 3-4 frames

    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    public async ValueTask<IVideoFrame> RentAsync(
        int width, int height, PixelFormat format, CancellationToken ct)
    {
        // Blocks when all frames are rented — natural backpressure
        await _available.WaitAsync(ct);

        // Allocate from ArrayPool<byte> or reuse a previously returned buffer
        var buffer = ArrayPool<byte>.Shared.Rent(CalculateSize(width, height, format));
        return new CpuVideoFramePooled(this, buffer, width, height, format);
    }

    public void Return(IVideoFrame frame)
    {
        // Return buffer to ArrayPool, release semaphore
        _available.Release();
    }
}
```

Pool size defaults to 3-4 frames. This provides enough decode-ahead to keep
the pipeline fed while bounding memory usage.

---

## 5. IVideoSink — The Presentation Destination

```csharp
public interface IVideoSink : IAsyncDisposable
{
    /// <summary>
    /// The frame pool this sink provides to the decoder.
    /// Frames allocated here are in the optimal memory domain for this sink.
    /// </summary>
    IFramePool FramePool { get; }

    /// <summary>
    /// Present a frame. The sink may AddRef the frame if it needs to
    /// hold it beyond this call (e.g., for vsync-aligned display).
    /// </summary>
    ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct);

    /// <summary>
    /// Notify the sink of format changes (resolution switch, etc.).
    /// The sink may need to recreate its surface or frame pool.
    /// </summary>
    ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct);

    /// <summary>
    /// Supported memory domains in preference order.
    /// Used during pipeline negotiation to pick the best path.
    /// </summary>
    IReadOnlyList<FrameMemoryDomain> SupportedMemoryDomains { get; }
}

public sealed record VideoFormatInfo(
    int Width, int Height,
    PixelFormat Format,
    double FrameRate
);
```

### 5.1 V1 Concrete Sinks

```csharp
// SDL presenter — wraps SDL_Texture, receives CPU frames
public sealed class SdlVideoSink : IVideoSink
{
    // FramePool: CpuFramePool (3-4 frames)
    // PresentAsync: SDL_UpdateTexture + SDL_RenderCopy
    // SupportedMemoryDomains: [Cpu]
}

// Avalonia presenter — wraps WriteableBitmap, receives CPU frames
public sealed class AvaloniaVideoSink : IVideoSink
{
    // FramePool: CpuFramePool (3-4 frames)
    // PresentAsync: copy into WriteableBitmap, marshal to UI thread
    // SupportedMemoryDomains: [Cpu]
}

// Null sink — drops all frames (audio-only, testing)
public sealed class NullVideoSink : IVideoSink
{
    // FramePool: NullFramePool (returns dummy handles, never blocks)
    // PresentAsync: immediate no-op, disposes frame
    // SupportedMemoryDomains: [Cpu]
}
```

These replace the current `IVideoFramePresenter` interface. The key difference
is that `IVideoSink` **owns the frame pool** — it decides where frames live.

---

## 6. IFrameConverter — Pixel Format Conversion

```csharp
public interface IFrameConverter : IDisposable
{
    /// <summary>
    /// Convert a frame from one format to another.
    /// The output frame is rented from the target pool.
    /// The input frame is NOT disposed — caller still owns it.
    /// </summary>
    ValueTask<IVideoFrame> ConvertAsync(
        IVideoFrame source,
        IFramePool targetPool,
        CancellationToken ct);
}
```

### 6.1 V1 Implementation

```csharp
// Wraps FFmpeg sws_scale for pixel format conversion
internal sealed class SwsFrameConverter : IFrameConverter
{
    // Converts between CPU formats: YUV420P → RGBA, NV12 → BGRA, etc.
    // Rents a frame from the target pool, converts, returns it.
}
```

Domain crossing (GPU→CPU or CPU→GPU) is not needed in v1. When all frames
and all sinks are CPU-domain, the converter only handles pixel format
differences.

---

## 7. Pipeline Negotiation

When `PlaybackSession.InitializeAsync()` runs, it negotiates the pipeline
configuration between the decoder and the sink:

```csharp
internal sealed record PipelineConfig
{
    public required IFramePool FramePool { get; init; }
    public required IFrameConverter? FrameConverter { get; init; }
}

internal static class PipelineNegotiator
{
    public static PipelineConfig Negotiate(MediaInfo info, IVideoSink sink)
    {
        // V1: both decoder and sink are CPU-only.
        // The negotiation always selects the CPU path.
        // The converter is non-null only if pixel formats differ
        // (e.g., decoder outputs YUV420P but sink wants RGBA).

        var sinkPrefs = sink.SupportedMemoryDomains;

        // In v1, this is always true (both are Cpu)
        if (sinkPrefs.Contains(FrameMemoryDomain.Cpu))
        {
            var needsConversion = DecoderOutputFormat(info) != SinkPreferredFormat(sink);

            return new PipelineConfig
            {
                FramePool = sink.FramePool,
                FrameConverter = needsConversion
                    ? new SwsFrameConverter(DecoderOutputFormat(info), SinkPreferredFormat(sink))
                    : null,
            };
        }

        // Future: handle GPU domain matching here
        throw new NotSupportedException("No compatible memory domain");
    }
}
```

---

## 8. Integration with Video Workers

### 8.1 Video Decode Worker

The decoder **rents** from the pool instead of allocating:

```csharp
private async Task RunVideoDecodeAsync(CancellationToken ct)
{
    try
    {
        await foreach (var packet in VideoPackets.Reader.ReadAllAsync(ct))
        {
            await _pauseGate.WaitAsync(ct);

            // Rent a surface from the sink's pool.
            // If all surfaces are in use (presenter + channel hold them),
            // this blocks — natural backpressure.
            var frame = await _framePool.RentAsync(
                _info.Width, _info.Height, _info.Format, ct);

            // Decode directly into the rented surface.
            _codec.Decode(packet, frame);

            await VideoFrames.Writer.WriteAsync(frame, ct);
        }
        VideoFrames.Writer.TryComplete();
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        OnFatalError?.Invoke(new PlaybackError(ErrorCategory.Decode, ex.Message, ex));
    }
}
```

### 8.2 Video Present Worker

The presenter uses the sink and optional converter:

```csharp
private async Task RunVideoPresentAsync(CancellationToken ct)
{
    try
    {
        await foreach (var frame in VideoFrames.Reader.ReadAllAsync(ct))
        {
            await _pauseGate.WaitAsync(ct);

            // ── Timing (sync to audio clock) ──────────────
            var referenceTime = _clock.Position;
            var delay = _syncStrategy.GetVideoDelay(frame.PresentationTime, referenceTime);

            if (delay < -_framePeriod)
            {
                _stats.DroppedFrames++;
                frame.Dispose();        // returns to pool
                continue;
            }

            if (delay > _halfFramePeriod)
                await Task.Delay(delay - _renderMargin, ct);

            // ── Presentation (through sink) ───────────────
            if (_converter is not null)
            {
                using var converted = await _converter.ConvertAsync(
                    frame, _sink.FramePool, ct);
                await _sink.PresentAsync(converted, ct);
            }
            else
            {
                await _sink.PresentAsync(frame, ct);
            }

            frame.Dispose();  // returns to pool
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        OnFatalError?.Invoke(new PlaybackError(ErrorCategory.System, ex.Message, ex));
    }
}
```

---

## 9. Frame Lifecycle

```
 1. Presenter releases frame → CpuFramePool.Return()    [pool: 3 free, 1 in-flight]
 2. Decoder calls pool.RentAsync → gets buffer           [pool: 2 free, 2 in-flight]
 3. FFmpeg decodes into the rented buffer                 [CPU fills buffer in-place]
 4. Decoder writes frame to VideoFrames channel           [frame in queue]
 5. Present worker reads from channel                     [frame dequeued]
 6. Worker checks timing vs audio clock                   [sync decision]
 7. Sink.PresentAsync uploads buffer to texture           [copy here for SDL/Avalonia]
 8. frame.Dispose() returns buffer to pool                [pool: 3 free, 1 in-flight]
```

In v1 there is still one copy at step 7 (CPU buffer → GPU texture upload).
This copy is eliminated in v2 when GPU-backed frame pools provide textures
that the decoder writes into directly.

---

## 10. Relationship to Existing Types

| Current type | Replacement | Notes |
|-------------|-------------|-------|
| `IDecodedVideoFrame` | `IVideoFrame` | Adds pool return, ref counting, domain accessor |
| `CpuVideoFrame` | `CpuVideoFramePooled` (internal) | Backed by pool instead of standalone `IMemoryOwner` |
| `IVideoFramePresenter` | `IVideoSink` | Adds frame pool ownership, format change notification |
| `FrameMemoryKind` | `FrameMemoryDomain` | Rename for clarity (or keep existing name) |
| `PixelFormat` | `PixelFormat` | Unchanged |
| N/A | `IFramePool` | New: bounded allocation with backpressure |
| N/A | `IFrameConverter` | New: pixel format conversion |
| N/A | `PipelineConfig` | New: negotiated pipeline shape |

---

## 11. Ownership Summary

| Concern | Owner | Mechanism |
|---------|-------|-----------|
| "Where do frames live?" | IVideoSink (provides IFramePool) | Pool is in sink's optimal domain |
| "How does the decoder get buffers?" | IFramePool.RentAsync | Decoder rents from sink's pool |
| "How are frames displayed?" | IVideoSink.PresentAsync | Sink receives the same buffer |
| "What if formats don't match?" | IFrameConverter | Single conversion at the boundary |
| "Who decides the pipeline shape?" | PipelineNegotiator | Matches decoder output to sink input |
| "Frame lifetime?" | Ref counting + pool return | Dispose returns to pool; AddRef for multi-hold |
| "Backpressure on decode?" | FramePool.RentAsync blocks | If all surfaces are held, decoder waits |
