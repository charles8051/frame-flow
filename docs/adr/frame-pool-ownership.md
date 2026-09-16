# Frame-pool ownership for buffered decoded video

**Status:** Draft, pending number assignment at merge. Not implemented.

**Date:** 2026-09-15

**Related:**
- [ADR-0025](ADR-0025-video-sink-and-frame-pool-architecture.md) — pool-rented frames with a
  refcount, and the sink-owns-the-pool inversion that gives backpressure. This record adds a
  pool on the decode side and leaves that inversion untouched.
- [ADR-0057](ADR-0057-pull-based-master-clock.md) — moved pacing out of the graph into
  `ClockSelectVideoSink` because `PaceUntil` held a decode-texture lease across the clock
  wait. It describes a small ring but names no depth; the constant lives in
  `ClockSelectVideoSink`. This record is about what holding more frames costs, and who owns
  them.
- [ADR-0038](ADR-0038-memory-domain-pipeline-operators.md) — the memory domains and
  `GpuVideoFrame`, whose lease is what a buffered hardware frame pins.
- [ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md) — the converter's own device,
  the decode-side staging texture each frame is copied into, and the rebind on a
  decode-device change.
- [Video lookahead](../feature-specs/video-lookahead/spec.md) — the player surface that
  spends what this record makes available. It is the only planned consumer.
- #125 — where the configurator runs relative to pacing, and the record that a lookahead
  buffer was explored and dropped.
- #7 — memory-aware (byte-bounded) backpressure on graph edges, the same budget question one
  layer down. Its `docs/DEFERRED_WORK.md` bullet cites it as `#3`, a pre-reseed number that
  now points at an unrelated merged PR (#154 tracks that class of stale citation).

## Context

`ClockSelectVideoSink` buffers frames at decode rate and selects the one due on the master
clock. Its ring is `DefaultCapacity = 3`, and `SubstrateSession` never passes a capacity, so
3 is what every paced playback gets. `PlayerSession` has no pacer at all (#125). The
constructor already takes `capacity`, so the buffer is not the missing piece. What is missing
is a frame the buffer can hold without taking something the decoder needs.

### Fixed pools and growable pools

Names, sizes and log lines below are FFmpeg 7.1. Every number here is version-specific.

| Backend | Pool | Effect of holding N frames |
| --- | --- | --- |
| D3D11VA | Fixed texture array on the decode path. `d3d11va_pool_alloc` returns NULL past `ArraySize`, logging "Static surface pool size exceeded." (`hwcontext_d3d11va.c` allocates one texture per frame instead when `BIND_RENDER_TARGET` is set, which the decoder does not use.) | Free until the spare slices run out, then the decoder has nowhere to decode into |
| DXVA2 | Fixed surface set from its own allocator, `dxva2_pool_alloc`, which returns NULL silently past the count. | Same |
| VideoToolbox | No fixed pool. Frames are retained `CVPixelBuffer`s from the decompression session. | Memory, allocated as used |
| Software decode | Ordinary refcounted buffers, no decode-side ceiling | Memory |
| VAAPI, NVDEC, Vulkan | Not yet checked | Unknown |

FFmpeg sizes the D3D11VA pool in `dxva2.c` (`ff_dxva2_common_frame_params`) and `decode.c`:
one work surface, plus 16 for possible references on H.264 and HEVC (8 on VP9 and AV1, 2
otherwise), plus 3 more work surfaces, plus `extra_hw_frames`, plus one per thread when frame
threading is active. For H.264 that is 20 slices. A stream using the full 16 references leaves
3 spare, and nearer 2 once the consumer holds the frame just emitted. That is a floor, not a
typical: most streams reference far fewer, so the real headroom is usually larger. Held frames
cannot exceed the spare slices whatever the ring size, because the decoder stops when it
cannot allocate.

The memory architecture is not the discriminator. An integrated GPU on Windows has unified
memory and still gets the fixed D3D11VA array.

### The buffer does not have to be the decoder's pool

The decoder's output pool has to be what FFmpeg requires. `d3d11va_create_decoder` fails with
"AVD3D11VAFramesContext.texture not set" unless the frames context holds one texture array,
so the decoder writes into a fixed array whether FFmpeg allocates it or we do.

What downstream holds is a separate question. A frame copied out of the decode slice into a
texture FrameFlow owns releases the `AVFrame` immediately, and the pool holding it can grow,
sit under a budget, and change depth during playback.

On the D3D11 presenter path that copy already happens. `D3D11Nv12SharedConverter` copies each
decode slice into a shareable NV12 staging texture with `CopySubresourceRegion` before the
shader converts it to BGRA (`D3D11Nv12SharedConverter.cs:581`, described at `:66-80`). Moving
that copy to decode time and turning the single staging texture into N is not a new copy per
frame, it is the same copy earlier.

That is what changed since #125, which recorded a lookahead buffer as explored and dropped on
two costs: pinned decode-pool leases, or several hundred MB of frame copies per second of
lead. Copying out of the pool avoids the first, and on the D3D11 path the second is already
being paid once per frame by the presenter.

## Decision

1. **Backends are described by pool model, as data.** Fixed pools carry their spare-slice
   count; growable pools do not. A backend nobody has characterised is treated as fixed.

2. **Fixed pools get a FrameFlow-owned pool.** Each decoded frame is copied out of its decode
   slice into a pool of shareable NV12 textures that pool owns, and the `AVFrame` is released.
   The decoder's own pool stays at FFmpeg's default size and never has more than one slice
   held downstream.

3. **Growable pools hold the decoder's frames directly.** No copy on VideoToolbox or the
   software path.

4. **This is a decode-side pool, and ADR-0025's inversion still governs the sink side.**
   `CpuFramePool` is a sink-owned pool with its own capacity (3, `CpuFramePool.cs:40`) whose
   `RentAsync` blocks, which is the backpressure ADR-0025 designed. The new pool sits upstream
   of it and does not replace it, so a depth chosen upstream still has to meet the sink's
   bound.

5. **A pooled hardware frame is a first-class frame, not an `AVFrame` wrapper.**
   `GpuVideoFrame` counts its decode lease and reads back through the `AVFrame`. A pool-owned
   texture does neither, so the memory-domain contract in ADR-0038 grows a second hardware
   shape, and `ReadbackToCpuBgra32` needs a path that does not go through
   `av_hwframe_transfer_data`.

6. **The pool follows the decode device.** The decode device changes at playlist item
   boundaries. ADR-0064 solves the same problem for the converter with a rebind, and the pool
   takes that approach rather than inventing another.

7. **The ceiling is observable.** `DecodePoolMetrics` gains the pool ceiling, so the
   outstanding-lease gauge has a denominator and pool pressure is legible from telemetry.

## Alternatives considered

### A. `extra_hw_frames` on the decoder

Ask FFmpeg for a larger pool at decoder open, and hold decode slices directly. No copy, and
the decoder keeps its own working surfaces. mpv exposes the same setting as
`--hwdec-extra-frames`.

Kept as the fallback for fixed-pool backends where copy-out is not implemented. Rejected as
the primary mechanism: the value is fixed when the decoder opens, so a depth change costs a
decoder reopen, and frame size is not known until the item is open. It also allocates the
whole depth in VRAM up front, whether or not the frames are ever held.

### B. Supply our own `hw_frames_ctx`

Create the frames context in `get_format` with our own size and bind flags. Worth doing for
the bind flags, since shareable shader-readable decode textures would let the presenter drop
its staging copy, which the converter already records as a follow-up. It does not change what
downstream can hold: the decoder still writes into a fixed array, so this is
`extra_hw_frames` with more control.

It also pulls against the copy-out pool. That copy is what frees the depth from the pool
size, so dropping it only benefits playback that holds no frames.

### C. Read back to CPU

About 250 MB per second of held frames at 1080p30 in BGRA, plus the per-frame readback the
GPU path exists to avoid. Rejected.

## Consequences

- Copy-out is per backend. D3D11 first, then VAAPI, CUDA and Vulkan once their pool models
  are known.
- Every component that pattern-matches `GpuVideoFrame` inherits the second hardware shape
  from Decision 5, including the inference readback in the LiveCaptioning example
  (`MainWindow.axaml.cs:594`).
- Nothing spends this on its own. Without the lookahead surface the depth stays 3 and the
  pool is dead weight, so the two land together or not at all.

## Open questions

- Pool model and spare-slice count for VAAPI, NVDEC and Vulkan.
- Whether the copy-out pool replaces the converter's staging texture outright, leaving one
  copy on the path rather than two.
- Whether the pool is per player or process-wide. `FrameFlow.Examples.Multicast` runs several
  players in one process.
