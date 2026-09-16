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
- #218 — the open/closed configurator contract. A consumer whose video path forks and
  rejoins needs that contract before it can spend a lead at all, so it orders first.
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
   ~~`CpuFramePool` is a sink-owned pool whose `RentAsync` blocks, which is the backpressure
   ADR-0025 designed, and the new pool sits upstream of it.~~ Corrected after review:
   `CpuFramePool.RentAsync` has **no production call sites**, and on the D3D11 path the pool is
   not in the chain at all. The sink-side bound this leaned on is not in force, so ADR-0025's
   inversion has to be answered on its own terms. See the amendment.

5. **A pooled hardware frame is a first-class frame, not an `AVFrame` wrapper.**
   `GpuVideoFrame` counts its decode lease and reads back through the `AVFrame`. A pool-owned
   texture does neither, so the memory-domain contract in ADR-0038 grows a second hardware
   shape, and `ReadbackToCpuBgra32` needs a path that does not go through
   `av_hwframe_transfer_data`.

6. **The pool owns a device on the decoder's adapter, and only a bridge rebinds.** ~~The pool
   follows the decode device, the way ADR-0064 rebinds the converter.~~ Corrected after review:
   ADR-0064's durable decision is that the converter stops *borrowing* the decode device. What
   moves on a device change is a thin per-decode-device bridge; the ring survives because it does
   not follow that device. A pool that follows it is the model ADR-0064 retired, with N shared
   textures and N cross-device keyed mutexes where it deliberately kept one.

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
- Whether the pool is per player or process-wide. `FrameFlow.Examples.DualPlayer` and
  `FrameFlow.Examples.ZeroCopyInterop` are the several-players case;
  `FrameFlow.Examples.Multicast` is one player whose frames are `AddRef`'d to three presenters,
  so a ring of N pins N slices until the slowest pane releases. ADR-0044 already says a pooled
  multi-session strategy needs its own design.
- Where the byte budget comes from. Nothing in `src/` queries adapter memory today, so the
  budget in the spec is a pure-function input with no supplier.

## Amendment, 2026-09-15: what three reviews changed

Reviews of the slices under #227 falsified parts of this record. Decisions 1 to 7 stand as
written except where struck through above, and the record as a whole is **provisional on #231**.

**The premise is not demonstrated.** A deeper buffer was argued for on two grounds: a lead for
operators, and absorbing a late wakeup. The second rests on the Windows ~15 ms timer granularity
quoted on `ClockSelectVideoSink.DefaultCapacity`, which ADR-0067 replaced with a high-resolution
timer measuring a 16.39 ms mean against a 16.67 ms frame period. The first is argued against by
[sync-window-join](ADR-0073-sync-window-join.md), which measured 40 ms frame-to-detection lag and calls it
the tightest pairing the join can produce, and by
[lateness-driven-decode-skip](lateness-driven-decode-skip.md), whose table shows a healthy 1080p60
run dropping zero frames and which names the ring never filling as the binding constraint. The 3
was never measured either: its provenance reaches only the squashed initial commit.

**Records this should have cited.** ADR-0063 booked the converter's per-frame copy as debt with a
named exit, which is this record's Alternative B. ADR-0030 sets the pool rule a decode-side pool
would break, that disposing a pool does not invalidate in-flight frames. ADR-0038's Alternative C
is decision 5, rejected there, and its operator contract throws `NotSupportedException` from
`ToCpu()` for a GPU frame of an unknown concrete type, which decision 5 has to answer rather than
name only `ReadbackToCpuBgra32`. ADR-0060 holds that the pump is always paced by a consumed
stream's wait queue, so whether pool rental blocks decides whether the video-only path keeps a
pacer. ADR-0054 scoped pooled refcountable outputs and deferred them. ADR-0062 defers the stable
decode device across items as warranting its own record. ADR-0066 says cheap to add and cheap to
defer is a case for deferring, which decision 5 has to meet head-on.

**The presenter is the silent failure.** `CompositionInteropVideoView.Present` dispatches on the
concrete `GpuVideoFrame`. A GPU frame of another shape logs one unpresentable-frame warning and
is then dropped every frame after, which is a black screen with one log line.

## Validation

None has run. Nothing here has been measured; every number is read off FFmpeg source or computed.
Before any of this is built:

- #231 settles the premise, in three arms: depth 3, a deeper ring, and depth 3 with a
  presented-PTS signal driving the overlay. A lead expressed in seconds is asserted as a value the
  code computed, never as a duration observed, because the wall-clock ban is a build error outside
  the integration suite.
- The copy-out pool carries a before-and-after soak with memory and pool-occupancy numbers.
  `examples/FrameFlow.Examples.ZeroCopyInterop --soak` is the instrument and needs a capacity flag
  and a pool-occupancy column.
- The pure resolve carries a table test per branch, on the model of `ReadAheadCapacityTests`.
- A new `ClockSelectBufferTests` covers depth as a parameterised dimension. The shipped default of
  3 appears in no test today.
