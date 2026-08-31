# ADR-0038: Memory-Domain Pipeline Operators (Tier 2 of the Crossbar-Shaping Roadmap)

**Status:** Accepted. Phase A landing now; Phase B deferred to a follow-up commit.
**Date:** 2026-05-12
**Supersedes:** None.
**Related:** ADR-0030 (frame-contract unification with Crossbar), ADR-0033 (hardware decode selection — explicitly deferred "Zero-copy GPU delivery" to a follow-up ADR; this is it), ADR-0036 (decode/playback decoupling), ADR-0037 (pixel-domain operators, Tier 1), `docs/CROSSBAR_SHAPING_ROADMAP.html` (Tier 2 audit).

## Context

ADR-0033 set up hardware decode selection (CUDA, VAAPI, D3D11VA, …)
but explicitly punted on **GPU delivery**:

> v1 of hwaccel decodes on the GPU and downloads to CPU
> (`av_hwframe_transfer_data`), producing the same `CpuVideoFrame`
> the software path produces. … Zero-copy GPU *delivery* (a
> producer-side `GpuVideoFrame` flowing through the pipeline to a
> GPU-aware presenter) is the seam ADR-0030 reserves and is
> explicitly a follow-up ADR.

The pixel operators of ADR-0037 close the next gap: video sinks no
longer need internal pixel-format conversion. But the readback the
hwaccel decoder still does internally is the same kind of "the
component makes a memory-domain decision for everyone" problem that
ADR-0036 fixed at the decode/playback boundary:

1. **Inference consumers can't get GPU frames.** A YOLO-on-GPU path
   today gets a CPU readback the decoder did, then would have to
   upload back to the GPU. The captioning demo's eventual
   Whisper-on-GPU has the same shape.
2. **The decoder makes the memory-domain decision for everyone.** A
   consumer that knows their sink wants CPU still gets the readback
   whether they ask or not. A consumer that wants GPU has no path.

The Crossbar-shaping roadmap's Tier 2 prescribes three operators:

| Operator | Purpose |
|---|---|
| `ToCpu()` | Explicit GPU→CPU readback. Cheap when frames are already CPU. |
| `MapToGpu(domain)` | CPU→GPU upload to a specified `FrameMemoryDomain`. |
| `AsDomain(target)` | Convenience that picks `ToCpu`/`MapToGpu` based on current vs. target. |

End state: the decoder produces whichever domain it natively
produces. Sinks that need CPU compose `.ToCpu()` before themselves.
GPU-resident inference paths skip `ToCpu()` and stay on the device.
The decoder's job shrinks to "produce frames in the codec's native
domain."

## Decision

Split Tier 2 into two phases. Phase A lands the **producer side** —
the GPU frame type, the decoder option to yield it, and the `ToCpu()`
operator that converts back. Phase B lands the consumer-and-default
side — `MapToGpu()` for CPU→GPU uploads, GPU-aware sinks, and the
flip of the decoder default to "produce GPU when hwaccel is active."

### Phase A — opt-in GPU yield + ToCpu (this commit)

#### 1. `GpuVideoFrame` type in `FrameFlow.Decoding`

Wraps a cloned `AVFrame*` (cloned via `av_frame_clone` so the
underlying device-side buffer is ref-counted by FFmpeg). On top of that
FFmpeg-level ref, the `GpuVideoFrame` carries its own **atomic
object-level ref count**, starting at 1 for the creating owner — the
same shape as `PooledCpuVideoFrame` / `PcmAudioBuffer`. Implements
`IVideoFrame` with:

- `MemoryDomain` returns `FrameMemoryDomain.Gpu`.
- `Format` reports the *software* pixel format that
  `av_hwframe_transfer_data` would produce on readback —
  typically `Nv12` for CUDA / VAAPI / D3D11VA. This is the format
  consumers should expect after `ToCpu()`.
- `AsCpu()` returns `null` (there is no in-place CPU view; readback
  is a real operation).
- `ToCpu()` (the interface method that returns a `CpuFrameData`
  struct) throws `NotSupportedException` with a redirect comment to
  the `pipeline.ToCpu()` operator — the interface method has no
  natural lifetime story for the temporary buffer.
- `AddRef()` increments the object-level count and returns the **same
  instance** (the codebase-wide contract — `Assert.Same` holds, and the
  graph's fan-out relies on reference equality). See the amendment below.
- `Dispose()` decrements the count; only the **final** release calls
  `av_frame_free`, which unrefs the device buffer and lets the decoder
  recycle the texture slice. Disposes past zero are idempotent no-ops.

The type is `public` so consumers building GPU-aware sinks
(D3D11 presenter, Vulkan presenter — future) can detect and unwrap
it. A future `IGpuVideoFrame` sub-interface can carry device-handle
accessors when the demand is there; ADR-0030 §capability-handles
calls this out as future work.

#### 2. `av_frame_clone` binding in `FrameFlow.Native`

One new P/Invoke in `FFAvUtil_Phase03.cs`. Returns a freshly-
allocated `AVFrame*` whose buffers are ref-counted with the source.

#### 3. `VideoDecoder.YieldHardwareFrames` option

New option, default `false`. When `true` and the decoder is bound to
an hwaccel device context AND the incoming `AVFrame` is in the
hardware pixel format, the decoder yields a `GpuVideoFrame` instead
of doing the internal `av_hwframe_transfer_data + sws_scale` readback.

The default stays `false` so existing consumers — every sink in the
codebase today plus the integration test harness — keep getting
`CpuVideoFrame` like they always have. Opting in is a per-decoder
flag, not a global one.

#### 4. `pipeline.ToCpu()` operator in `FrameFlow.Video`

```csharp
public static FramePipeline<IVideoFrame> ToCpu(this FramePipeline<IVideoFrame> pipeline);
```

Behavior per packet:

- Frame is already `MemoryDomain.Cpu` → pass through unchanged
  (no allocation).
- Frame is `GpuVideoFrame` → perform `av_hwframe_transfer_data` to
  a CPU AVFrame, then `sws_scale` the readback to a packed
  `Bgra32` `CpuVideoFrame` and emit downstream. PTS / Duration
  preserved.
- Frame is something else with `MemoryDomain.Gpu` → throw
  `NotSupportedException` (no way to read it without knowing the
  concrete type).

The Bgra32 output choice matches the existing default of the
software readback path the decoder previously did internally — so
inserting `.ToCpu()` between a `YieldHardwareFrames=true` decoder
and a sink reproduces the pre-ADR-0038 pixel output bit-for-bit.

#### 5. `pipeline.AsDomain(target)` convenience

```csharp
public static FramePipeline<IVideoFrame> AsDomain(
    this FramePipeline<IVideoFrame> pipeline,
    FrameMemoryDomain target);
```

For `target == Cpu`: equivalent to `.ToCpu()`.
For `target == Gpu`: not supported in Phase A — throws
`NotSupportedException` with a pointer to MapToGpu (deferred to
Phase B).

The convenience exists for symmetry and to give consumers a
"target-shaped" API instead of having to know which conversion to
pick.

#### 6. Tests

- Unit tests in `FrameFlow.Video.Tests`: `ToCpu()` pass-through for
  CPU frames; pipeline composition shape (one-in-one-out); error
  surfaces.
- Integration test in `FrameFlow.Decoding.Tests`: open a corpus
  file, configure the decoder with `YieldHardwareFrames=true`, run
  through the `.ToCpu()` operator, assert output frames are
  `Bgra32` CPU frames with the expected dimensions. Skips
  gracefully on hosts without hwaccel.

### Phase B — deferred to follow-up commits

The remaining Tier 2 pieces:

- **`MapToGpu(domain)`** — CPU→GPU upload via
  `av_hwframe_get_buffer` + `av_hwframe_transfer_data`. Requires
  device-context lifecycle management at the operator boundary —
  who owns the device, how is it shared with the decoder's device,
  how does the consumer specify which device. Bigger design
  question than the readback direction; deserves its own pass.
- **GPU-aware sinks** — a Vulkan / D3D11 video sink that consumes
  `GpuVideoFrame` directly and presents without a CPU round-trip.
  Needs swapchain integration with the windowing layer; out of
  scope for the operator landing.
- **Flipping the decoder default** to yield GPU frames when
  hwaccel is active. This is a breaking change for every sink
  consumer; will land after one or more sinks can natively
  consume GPU frames so the default flip doesn't strand anyone.
- **Per-backend GPU frame sub-interfaces** (`ICudaVideoFrame`,
  `ID3D11VideoFrame`) for consumers that need device-handle
  access for zero-copy ML / shader pipelines.

Each gets its own follow-up ADR or commit; the Phase A surface
designed here doesn't constrain those decisions.

## Consequences

### Positive

- **GPU-resident inference workflows become possible.** A
  consumer with a GPU-native model composes
  `decoder.YieldHardwareFrames = true; stream.Video.MapToGpu(Cuda)
  .DetectWith(yoloGpuModel)` (once Phase B lands `MapToGpu`).
  Today they have to upload after the decoder's readback —
  Phase A makes the decoder's internal readback optional.
- **The decoder's job shrinks** when consumers opt in: produce
  in the native domain, let consumers decide the rest.
- **`ToCpu()` is the explicit contract** consumers compose to get
  the existing behavior, which makes the semantics auditable in
  the call site rather than hidden inside the decoder.

### Negative

- **The default is opt-in.** Consumers who set
  `YieldHardwareFrames=true` and don't compose `ToCpu()` before
  their sink will see runtime failures (sinks don't yet accept
  GPU frames). Documented; flagged in the operator's intellisense.
- **One more `IVideoFrame` implementation.** Adds `GpuVideoFrame`
  alongside `Media.CpuVideoFrame` and `Playback.CpuVideoFrame`.
  Mitigated: the new type has narrow responsibility (own a
  cloned AVFrame, expose metadata, expose the underlying pointer
  to the readback operator).

### Neutral

- **Existing tests pass unchanged.** Default `YieldHardwareFrames=
  false` means every existing test continues to see
  `CpuVideoFrame` output. The integration test suite stays green.
- **`FrameFlow.Video` gains the operators but nothing else
  changes there.** Tier 1's pixel operators continue to work
  identically on the CPU frames coming through the default path.

## Alternatives considered

### A. Land all of Tier 2 in one commit

The right architectural shape but multi-week scope. Phase B requires
device-context lifecycle design (who owns it, how it composes with
the decoder's device), GPU sink integration with windowing systems,
and a flip of the decoder default that breaks every sink consumer
without GPU-aware sinks landing first.

Rejected because Phase A delivers concrete value (the GPU yield seam,
the ToCpu operator) without making any of the larger architectural
choices that Phase B requires.

### B. Skip Tier 2, jump to Tier 3 (Whisper/YOLO operators)

The captioning demo doesn't need GPU frames today. Tier 3 has more
concrete demand.

Rejected because user explicitly directed Tier 2 next, and because
Phase A here is the right amount of scaffolding to do *before* a
GPU YOLO operator lands — the GPU operator can compose on top of
the GPU producer side this ADR ships.

### C. Per-backend frame types (`CudaVideoFrame`, `D3D11VideoFrame`)

A single `GpuVideoFrame` carries the AVFrame* across all backends.
This is the FFmpeg-shaped abstraction — `AVFrame.hw_frames_ctx` knows
which backend it lives on. Per-backend types would mirror that
distinction at the C# layer.

Rejected for Phase A: the only consumer of GPU frames in this commit
is the `ToCpu()` operator, which goes through `av_hwframe_transfer_data`
that works uniformly across all backends. A future
`ID3D11VideoFrame` sub-interface (with a `GetD3D11Texture()`
accessor) can land when there's a consumer that needs it.

### D. Use Crossbar's tensor abstractions instead of a new GPU frame type

Crossbar has `ICudaTensor` for GPU-resident ML data. We could wrap
the AVFrame's GPU buffer as a CUDA tensor.

Rejected because (a) the AVFrame's GPU buffer is not always CUDA
(D3D11VA, VAAPI, VideoToolbox, etc.), (b) the tensor abstraction
flattens out frame metadata (PTS, Duration, Format) that video
consumers need. The right shape is a frame type, not a tensor type;
the two can co-exist.

## Implementation plan

1. Add `av_frame_clone` binding in `FrameFlow.Native/Interop/FFAvUtil_Phase03.cs`.
2. Create `GpuVideoFrame` in `FrameFlow.Decoding`.
3. Add `YieldHardwareFrames` option to `VideoDecoder` + the yield path
   (clone the AVFrame when hwaccel active and the flag is set; wrap
   as GpuVideoFrame instead of calling BuildManagedFrame).
4. Add `ToCpu()` operator in `FrameFlow.Video` — internal type that
   knows how to readback a `GpuVideoFrame`.
5. Add `AsDomain(target)` convenience.
6. Tests — unit tests for CPU pass-through, integration test for the
   full hwaccel → GPU → ToCpu pipeline (gated on hwaccel availability).
7. Update `docs/CROSSBAR_SHAPING_ROADMAP.html` — Tier 2 Phase A in
   flight / done.
8. Commit and document Phase B follow-ups.

Phase B (deferred) starts with `MapToGpu` design — what device
context does the operator own, who shares it with downstream
consumers. Likely a dedicated ADR.

## Amendment (2026-06-04): Phase B `ID3D11VideoFrame` accessor + first GPU-aware sink

The first consumer that needs device-handle access has arrived: the Avalonia
**composition-interop zero-copy presenter** (see the ADR-0016 amendment of the same date),
which imports the decoder's D3D11 texture straight into Avalonia's compositor via
`ICompositionGpuInterop`. Per **Alternatives considered C** above ("A future `ID3D11VideoFrame`
sub-interface with a `GetD3D11Texture()` accessor can land when there's a consumer that needs
it"), this unblocks two of the deferred Phase B items.

Landing on branch `spike/zero-copy-presenter`:

- **`GpuVideoFrame` gains backend identity + a D3D11 accessor.** A stored
  `HardwareDecodeBackendKind Backend` (set at `CloneFrom` time from the decoder's bound
  `HardwareBackend`), and `bool TryGetD3D11Texture(out nint texture, out int subresourceIndex)`
  that surfaces `AVFrame.data[0]` (the `ID3D11Texture2D*`) and `AVFrame.data[1]` (the
  texture-array index) when `Backend == D3D11Va`, else returns `false`. This is the minimal,
  backend-specific accessor; CUDA's `ICudaVideoFrame` (exposing the `CUdeviceptr`) follows the
  same shape when an NVDEC consumer lands. Native pointers stay off the cross-assembly
  `IVideoFrame` contract — the accessor lives on the concrete `GpuVideoFrame` that GPU-aware
  sinks downcast to, exactly as Phase A's §1 note anticipated ("consumers building GPU-aware
  sinks can detect and unwrap it").

- **"GPU-aware sinks" is now in progress** — the composition-interop presenter is the first.
  **"Flipping the decoder default"** to yield GPU frames when hwaccel is active **stays deferred**
  until that presenter graduates from spike to a shipped `FrameFlow.Avalonia.Windows` presenter,
  so the default flip doesn't strand the CPU sinks.

## Amendment (2026-06-04): `GpuVideoFrame` ref counting — hardware-frame fan-out

Phase A shipped `GpuVideoFrame.AddRef()` as a `throw new NotSupportedException` ("single
owner; clone again from the decoder to share"). That stranded **multi-pane / multicast** on the
GPU path: the substrate's fan-out (`NodePumps`, `(T)item.AddRef()` per extra branch) and the
`VideoFrameRef` wrapper both go through `AddRef`, so one decode could not be presented in N panes
without a per-pane readback or deep clone — defeating the zero-copy win the presenter exists for.

`GpuVideoFrame` now carries the **same atomic object-level ref count** every other refcounted
frame/buffer in the codebase uses (`PooledCpuVideoFrame`, `PcmAudioBuffer`, `EncodedPacket`,
`CameraVideoFrame`):

- `AddRef()` CAS-increments the count and returns the **same instance** — the codebase-wide
  contract that `Assert.Same` pins and the graph's fan-out relies on (reference equality
  distinguishes the inherit branch from AddRef siblings). Throws `ObjectDisposedException` once
  the count has reached zero.
- `Dispose()` decrements; only the **final** release calls `av_frame_free`. Disposes past zero are
  idempotent no-ops.

**Why same-instance, not clone-on-AddRef.** Returning a fresh `GpuVideoFrame` (each wrapping its
own `av_frame_clone`) would violate the `Assert.Same` contract and break the graph's
reference-equality accounting. The shared-instance model is also *cheaper and more correct* for the
hardware: all holders read the same `AVFrame` → the same D3D11VA decode-texture-array slice, and
because `av_frame_free` is deferred to the last release, FFmpeg cannot recycle that slice back into
the decoder's hwframe pool while any pane still holds the frame. No clone, no readback, no extra VRAM.

**Hardware feasibility of fan-out.** The composition-interop converter binds its `ID3D11VideoProcessor`
to the **decoder's own device** (`_device = nv12.Device`), so N panes each stand up a converter on that
device and all read the same shared decode slice; only the per-pane *output* ring is cross-device
shared into the compositor. The D3D11 immediate context is not free-threaded, but Avalonia serialises
every pane's present on the single UI thread, so the per-pane `VideoProcessorBlt` calls do not race.

This makes **"GPU-aware sinks"** cover the multi-sink case, not just single-surface playback. The
decoder default still **stays `false`** (CPU sinks unchanged); fan-out is available to any consumer
that opts into `YieldHardwareFrames` and wires multiple GPU presenters.
