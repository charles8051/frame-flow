# ADR-0016: Avalonia Presenter Frame Delivery Strategy

## Status

Accepted

## Context

The current `AvaloniaVideoPresenter` copies decoded pixel data into a single `WriteableBitmap` and then
posts a reference to the UI thread via `Dispatcher.UIThread.Post`. This creates a race condition: the
decode thread may begin writing the next frame into the same bitmap while Avalonia's Skia compositor is
still reading the previous frame for GPU upload. The result is a potential torn or corrupted frame on
screen. The single-buffer model is not safe under sustained playback.

A second concern arises at Phase 09, when hardware-accelerated decoders (D3D11VA, VAAPI, VideoToolbox)
will produce frames that reside in GPU memory rather than CPU memory. The naive approach — call
`GpuVideoFrame.ReadbackToCpu()` immediately and continue as today — works but discards the primary
benefit of hardware decode by still performing a full GPU→CPU copy and then a CPU→GPU copy when Skia
uploads the bitmap to a texture.

True zero-copy GPU-to-Skia is not straightforward. Skia has no D3D11 backend. The Windows path uses
ANGLE (an OpenGL ES layer over D3D11), which does expose `EGL_ANGLE_d3d_texture_client_buffer` for
importing D3D11 textures as EGL images — but this requires the hardware decoder's D3D11 device and
ANGLE's internal D3D11 device to be the same device (or share textures via `IDXGIKeyedMutex`). That
coupling requires reaching into Avalonia's platform internals and is fragile across Avalonia versions and
backend configurations (ANGLE vs. Vulkan vs. Metal). It is the right long-term direction but not the
right Phase 09 starting point.

This ADR addresses both concerns:

1. **Immediate** — fix the race condition in the software path with double-buffering.
2. **Phase 09** — define the practical GPU-accelerated path for the Avalonia presenter.

## Decision

### 1. Double-buffer WriteableBitmap in AvaloniaVideoPresenter

`AvaloniaVideoPresenter` will maintain two `WriteableBitmap` instances: a *back buffer* that the decode
thread writes into, and a *front buffer* that the UI thread reads from. After each write the two
references are atomically swapped on the UI thread before `InvalidateVisual()` is called.

The invariant is: the render method always reads from the front buffer, which the decode thread never
touches after the swap. The decode thread always writes to the back buffer, which the compositor never
reads.

Pseudo-structure:

```
_back  ← decode thread writes here
_front ← Avalonia Render() reads from here

After write completes:
  Dispatcher.UIThread.Post(() => {
      (_front, _back) = (_back, _front);   // atomic reference swap
      InvalidateVisual();
  });
```

Because the swap itself executes on the UI thread, it is sequenced after any in-progress `Render` call
and before the next one. The decode thread may already be writing into the new back buffer (formerly the
front) by the time the next render begins — but that is safe because the render reads the new front
buffer (formerly the back) and the two never overlap.

Both bitmaps are allocated lazily when the first frame arrives and reallocated if frame dimensions
change. On dimension change both buffers are replaced together to keep them consistent.

This fix is entirely contained within `AvaloniaVideoPresenter`. No other component is affected.

### 2. Single-copy GPU pipeline for Phase 09 Avalonia

When the decoder produces `GpuVideoFrame` instances, `AvaloniaVideoPresenter` will follow this path:

```
HW decoder → NV12/YUV texture (VRAM)
    → GPU VideoProcessor Blt → BGRA staging texture (VRAM)   [GPU-side color conversion]
    → Map staging texture → CPU pointer                       [one GPU→CPU copy]
    → Copy into WriteableBitmap back buffer                   [one CPU memcpy]
    → Swap front/back → InvalidateVisual
    → Skia uploads front buffer to compositor texture         [one CPU→GPU copy, pageable]
```

This is called the *single-copy* path because only one cross-memory-space transfer occurs in the hot
path that the application controls: the GPU→CPU map of the staging texture. The upstream GPU work
(decode, color conversion) and the downstream Skia upload are both present in the software path too,
so they do not represent new cost attributable to hardware acceleration.

The benefit over software decode is that the decode and NV12→BGRA color conversion are offloaded to the
GPU's fixed-function video processor, which is substantially faster and frees CPU time for other work.

`GpuDeviceContext` will gain a concrete `D3D11DeviceContext` implementation (and platform equivalents)
that exposes a `BltToStagingAndReadback` method. `AvaloniaVideoPresenter.PresentAsync` checks
`frame.MemoryKind` and routes to this path when a `GpuVideoFrame` arrives.

The existing `ReadbackToCpu()` on `GpuVideoFrame` is retained as a convenience fallback for
non-performance-critical uses (thumbnails, screenshots, test harnesses) but is not the hot-path
mechanism for the Avalonia presenter.

### What is explicitly deferred

The ANGLE device-sharing path (true zero-copy: D3D11 texture → EGL image → Skia `GrBackendTexture`)
is acknowledged as theoretically achievable but is deferred past Phase 09. It requires:

- retrieving Avalonia's internal ANGLE D3D11 device at startup
- creating the FFmpeg hardware decode context against that same device
- using `EGL_ANGLE_d3d_texture_client_buffer` to import each frame as an EGL image
- wrapping the EGL image as a Skia `GrBackendTexture` and drawing it without any copy

This is the correct eventual direction for maximum performance on Windows with the Avalonia/ANGLE
backend. It is deferred because it is tightly coupled to Avalonia internals, it does not work on the
Vulkan or Metal backends, and the single-copy path already captures the dominant cost (CPU decode).

The `IVideoFramePresenter` contract is unchanged. A future `AngleInteropVideoPresenter` can implement
this path without touching any other pipeline component.

## Consequences

### Positive

- The race condition in the software path is eliminated with a small, self-contained change
- Double-buffering is a well-understood pattern; the implementation is straightforward
- The single-copy GPU path delivers the primary performance benefit of hardware decode (offloading
  the decode and color conversion) without coupling to Avalonia internals
- `AvaloniaVideoPresenter` remains the only component that changes between software and GPU paths
- The ANGLE zero-copy path remains achievable in a future presenter without architectural change
- On integrated GPUs (Intel UHD, AMD integrated, Apple Silicon) the GPU→CPU copy cost is low
  (~0.5–1.5 ms for 4K BGRA) due to shared memory; the single-copy path is near-optimal on those
  targets

### Negative

- Double-buffering doubles the `WriteableBitmap` memory footprint (~32 MB per buffer at 4K, ~64 MB
  total); this is acceptable but worth noting for memory-constrained environments
- The single-copy path still performs one GPU→CPU transfer per frame, which costs 2–8 ms at 4K on
  discrete GPUs over PCIe 3.0 with pageable memory — this is the ceiling on Avalonia GPU performance
  until the ANGLE interop path is implemented
- `D3D11DeviceContext` adds a platform-specific implementation requirement for the GPU path on Windows;
  equivalent implementations are needed for VAAPI (Linux) and VideoToolbox (macOS)

## Alternatives Considered

### Keep single buffer, rely on Avalonia's internal synchronization

Rejected. Avalonia's internal render pipeline does not guarantee that `WriteableBitmap` pixel data is
not being read during a concurrent `Lock()` call from another thread. The race is real and the fix is
inexpensive.

### Triple buffering

Considered. Triple buffering allows the decode thread to write ahead without ever blocking on the
swap. Rejected for now because double-buffering is sufficient at the frame rates and queue depths
FrameFlow targets. If profiling shows the decode thread blocking on UI-thread post latency, triple
buffering can be adopted by adding a third buffer slot without changing the surrounding architecture.

### Immediate ReadbackToCpu() for all GPU frames

Viable fallback. Rejected as the primary GPU path because it performs two full frame copies (GPU→CPU
readback, then CPU→GPU Skia upload) instead of one, and it foregoes GPU-side color conversion. It
remains available via `GpuVideoFrame.ReadbackToCpu()` for non-hot-path uses.

### ANGLE device-sharing zero-copy path in Phase 09

Rejected as Phase 09 scope because it requires Avalonia platform internals access, does not generalize
to non-ANGLE backends, and would make the presenter fragile across Avalonia updates. The single-copy
path is the correct Phase 09 starting point. The ANGLE path is the correct Phase 10+ optimization.

### Native overlay window (NativeControlHost)

Considered. Rendering video into a native child window (HWND/NSView) punched through the Avalonia
surface would achieve true zero-copy GPU presentation and eliminate Skia from the video hot path
entirely. Rejected for the Avalonia presenter because it removes the video surface from Avalonia's
layout and compositor, making overlays, subtitles, and control compositing significantly harder.
`NativeOverlayVideoPresenter` remains a valid future `IVideoFramePresenter` implementation for
scenarios where maximum GPU performance outweighs layout integration.

## References

- ADR-0012: Memory management for decoded frames
- ADR-0015: GPU-resident frame pipeline extensibility
- Phase 07: Avalonia adapter
- Phase 09: Hardware acceleration and presenters

## Amendment (2026-06-04): compositor GPU interop supersedes the ANGLE/Skia path for true zero-copy

When this ADR was written, the only zero-copy route considered was D3D11 texture →
`EGL_ANGLE_d3d_texture_client_buffer` → Skia `GrBackendTexture` (the "explicitly deferred"
section above), and it was deferred because it reaches into Avalonia's internal ANGLE D3D11
device and breaks on non-ANGLE backends. Avalonia 11.x has since shipped a better seam:
**`ICompositionGpuInterop`** (`Compositor.TryGetCompositionGpuInterop()`) plus
`CompositionDrawingSurface.UpdateWithKeyedMutexAsync(...)`. This imports an external GPU image
straight into the **compositor**, not through Skia — so it neither touches Avalonia's render
device nor couples to the GL/ANGLE-vs-Vulkan backend choice (it negotiates via
`ICompositionGpuInterop.SupportedImageHandleTypes`, which includes
`D3D11TextureGlobalSharedHandle` on the Win32 backend).

**Revised zero-copy decision.** The zero-copy presenter is `CompositionInteropVideoPresenter`
(replacing the hypothetical `AngleInteropVideoPresenter`). Per-frame path:

```
HW decoder → NV12 texture (VRAM, FFmpeg's D3D11 device)
    → ID3D11VideoProcessor Blt → shared BGRA texture (VRAM, ResourceMiscFlag.SharedKeyedmutex)   [GPU color convert]
    → ICompositionGpuInterop.ImportImage(sharedHandle, B8G8R8A8UNorm)   [once; re-import on resize]
    → CompositionDrawingSurface.UpdateWithKeyedMutexAsync(img, acquire: 1, release: 0)
    → compositor composites the surface visual — no CPU crossing, no readback
```

The producer brackets its Blt with the shared texture's keyed mutex `AcquireSync(0)` /
`ReleaseSync(1)`; the compositor takes `acquire: 1` / `release: 0`. Because the texture is shared
by **NT handle**, the compositor opens it on its *own* device — which is exactly what removes this
ADR's original objection (no reach into Avalonia's ANGLE device).

**Relationship to Decision #2 (single-copy).** The `ID3D11VideoProcessor` NV12→BGRA Blt is
*identical* to the single-copy path; the only change is the BGRA result stays GPU-resident in a
shared keyed-mutex texture instead of being mapped to CPU. So **single-copy remains the fallback**
for platforms/backends where `ICompositionGpuInterop` is absent or doesn't advertise the D3D11
shared-handle type. The ANGLE/Skia `GrBackendTexture` path is now superseded as the chosen
direction and retained only as a consideration for cases that must draw the frame *through* Skia
(e.g. applying Skia shaders to the video at draw time).

**Prerequisites (being spiked on branch `spike/zero-copy-presenter`):**

1. `GpuVideoFrame` must expose its D3D11 texture + array index — the `ID3D11VideoFrame` /
   `GetD3D11Texture()` accessor that ADR-0038 "Alternatives considered C" reserved. This spike adds
   `GpuVideoFrame.TryGetD3D11Texture(out nint texture, out int subresourceIndex)`.
2. The decode path must bind D3D11VA and set `YieldHardwareFrames = true`. The capability threading
   that engages D3D11VA is already complete on `main`: the bootstrap-probed
   `HardwareDecodeCapabilities` flow `MediaPlayer.CreateAsync` → `PlaybackController.Create` →
   `SubstrateSessionFactory` → `SubstrateSession`, which passes them to `DecoderFactories.CreateVideo`.
   (The kiosk's *deployed* build predates that completion and still feeds
   the decoder `HardwareDecodeCapabilities.Empty`, so it silently software-decodes until it redeploys
   from current `main`.) `YieldHardwareFrames` is the remaining knob; this spike threads it through the
   same chain, default `false` / opt-in per session, so existing CPU sinks keep getting
   `CpuVideoFrame` and are unaffected.

**Reality check (carried from the Consequences).** On UMA integrated GPUs (an Intel HD 620
kiosk, say) the single-copy path's GPU→CPU map is sub-millisecond, so zero-copy's marginal
win there is small. Its real payoff is discrete GPUs / 4K / many concurrent streams. Single-copy
stays the right default for the kiosk; this amendment unblocks the zero-copy presenter for the
broader fleet and higher-resolution targets.
