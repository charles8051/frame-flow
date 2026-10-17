# ADR-0064: Zero-copy presenter converter — decode-device identity and ownership

> Drafted on branch `fix/warm-swap-presenter-device`; assigned ADR-0064 on merge
> to `main` per this repository's "number-at-merge" rule.

> **Amended 2026-09-27 by [a borrowed hardware device](borrowed-hardware-device.md).** Option (b),
> one decode device shared across a playlist's items, is now available for the decode device as
> `HardwareDevice`, for inference that reads decoded textures in place. The presenter keeps
> Decision 2's own device.

> **Amended 2026-10-06: a mixed-resolution swap resizes in place.** In the shader mode the
> converter replaces only the parts the frame's size fixes, which are the staging texture, the
> ring and the decode bridge, and keeps its device and shader pipeline
> (`D3D11Nv12SharedConverter.TryResize`, `ConverterAction.ResizeInPlace`, counted by
> `frameflow.presenter.resolution_resizes`). The ring is new, so it is still imported again. The
> video processor mode, and a swap that also changes the sample format, still rebuild as the
> paragraph on mixed-resolution swaps below describes.

> **Amended 2026-10-16: the shader-mode ring follows the view (#767).** The ring is the size the
> visible part of the frame is drawn at, up to that part's coded size, and the shader reads only
> that part through an area filter. A view-driven resize replaces the ring alone and is counted by
> `frameflow.presenter.view_resizes`. See the amendment at the end.

## Status

Accepted. Decision 1 (interim) **implemented** (2026-06-16). Decision 2 (durable —
converter owns its own device) **implemented** (2026-06-21): the converter
no longer borrows FFmpeg's decode device for its lifetime; a warm-sink player swap rebinds
its decode bridge in place and records **zero** converter rebuilds. GPU mechanics validated
on dev hardware + WARP-class logic/telemetry tests; on-kiosk A/B on the Intel HD 620 (the
NV12-share driver gate) is the final validation, per the ADR-0063 convention.

**Date:** 2026-06-16 (Decision 1), 2026-06-21 (Decision 2)
**Tracking:** `charles8051/frame-flow`.
**Related:**
- ADR-0062 (gapless multi-source playlist on a warm presenter — the design this bug
  invalidates without device reuse; ADR-0062 keeps the *sink + view* warm across
  player swaps but does not rebuild the GPU converter, which is where this breaks).
- ADR-0063 (NV12 pixel-shader color conversion — the converter whose device is at issue).
- ADR-0016 (+ zero-copy amendment), ADR-0038 (GPU frame contract / `GpuVideoFrame`).
- The 2026-06-12 teardown-deadlock investigation (§6 step 5), which named "stop
  borrowing FFmpeg's device for the converter" as deferred. Not published with
  this repository.

## Context

`D3D11Nv12SharedConverter` **borrows the first frame's FFmpeg D3D11VA decode device**
(`_device = nv12.Device.QueryInterface<…>()`) and builds its keyed-mutex BGRA ring and
every per-frame cross-device copy / `ExecuteCommandList` on that borrowed device. The
view caches it `_gpuConverter ??= …` and rebuilds it only on (a) a visual-tree detach,
(b) a device-loss HRESULT, or (c) a GPU↔CPU source flip.

A consumer running its own playlist orchestration (a signage host) keeps a **warm
video sink + compositor visual** and rebuilds only the `MediaPlayer` per item, creating
each new player over the **same** sink instance (the ADR-0062 warm-presenter shape). A
player swap disposes the old player's decode device but does **not** trigger any of the
three rebuild conditions, so the cached converter keeps issuing cross-device copies
against the now-disposed decode device. Because the converter's `QueryInterface` AddRef
holds a COM reference, the device never cleanly faults — no device-loss HRESULT, no
rebuild — so the converter silently orphans: the on-screen visual freezes on the last
frame while the new player is healthy (position advances, `FramesPresented` climbs).
Only a full visual-tree detach/re-attach clears it.

The failure was also structurally **invisible**: `_framesPresented` counted at *enqueue*
(right after the present hand-off was posted), and the present-stall watchdog only tripped
when that enqueue counter went flat — which it does not in this mode (enqueue keeps
climbing). See `#445`.

## Decision 1 — Detect a stale decode device and rebuild the converter (interim, implemented)

Surface a **stable decode-device identity** on the GPU frame and rebuild the converter
when it no longer matches the device the cached converter borrowed.

- `GpuVideoFrame.TryGetD3D11Texture` gains an `out nint device`, walking
  `AVFrame.hw_frames_ctx → AVHWFramesContext.device_ctx → AVHWDeviceContext.hwctx
  (AVD3D11VADeviceContext) → device` to return the `ID3D11Device*`. Every frame from one
  decoder reports the same pointer; a new decoder reports a different one. `nint.Zero`
  means "identity unknown" and is never treated as a mismatch.
- `D3D11Nv12SharedConverter` exposes `SourceDevicePointer` (the borrowed device's native
  pointer). The borrowed-device QI AddRef keeps that device alive for the converter's
  lifetime, so its pointer **cannot be reused** by a new device while the old converter
  lives — making the comparison reuse-safe (the decode *texture* pointer is not, since the
  converter does not pin it; hence device identity, not texture identity).
- The view drops + rebuilds the converter (reusing the existing `DropGpuConverter` /
  `DetachImported` machinery) when `cachedDevice != frameDevice`. The decision is a pure,
  unit-tested static (`EvaluateConverterRebuild`); device-loss keeps priority over a
  device change. The surface + interop import stay warm; only the converter build is
  re-paid per swap.

This fully resolves the freeze: a warm-sink player swap now rebuilds the converter on the
new decode device on the first frame after the swap.

## Decision 2 — Converter owns its own D3D11 device (durable, implemented 2026-06-21)

The root awkwardness is that the converter **borrows** the decode device at all. The
durable fix gives the converter its **own** D3D11 device (as the CPU `D3D11BgraUploader`
already does), created on the **same adapter** as the decoder so cross-device shared
textures resolve. The keyed-mutex BGRA ring, the compositor imports of it, and the shader
pipeline all live on that stable own device; they are built once and survive every decode
device swap. Only the thin per-decode-device bridge moves when the decode device changes.

**Bridging the decode slice without a decoder change (sub-option a).** FFmpeg's D3D11VA
decode pool is auto-allocated `D3D11_BIND_DECODER`-only and not shareable, so its slices
cannot be opened on a private device by handle. Rather than make the decoder emit shareable
textures (a `FrameFlow.Decoding` HwAccel change), the converter owns a single **shareable**
`BIND_SHADER_RESOURCE` NV12 staging texture (`SharedKeyedMutex`) on its own device, and
**opens it by shared handle on the current decode device**. Each frame the decode slice is
`CopySubresourceRegion`'d into that decode-side handle on the decoder's
`ID3D11Multithread`-protected immediate context (the serialization-with-decode the old path
relied on), bracketed by the staging keyed mutex so the write is fenced; the own device then
samples the staging texture and draws into the ring. This is the *"stage each slice into a
shareable texture on the decode device first"* path this ADR's original blocker named — kept
**entirely inside `D3D11Nv12SharedConverter`**, off the churny decoder (consistent with
ADR-0063). On a warm-sink swap, `TryRebindDecodeDevice` releases + reopens only the decode-side
handle (+ that device's immediate context); the ring and its compositor imports are untouched.

The in-place rebind applies only when the incoming item's **resolution matches** the cached
converter's: the ring, the staging texture, and the per-frame copy `Box` are all sized at
construction, so a **mixed-resolution** swap still drops + rebuilds the converter at the new size
(`EvaluateConverterAction` returns `RebuildForResolutionChange`, which takes priority over a device
change so a swap that changes both rebuilds rather than rebinding onto a wrong-sized ring). This
also closes a pre-existing latent bug where a *same-device* mid-stream resolution change was never
caught. Counted by `frameflow.presenter.device_resolution_rebuilds`, distinct from the same-size
`device_change_rebinds`.

**Why (a) over (b) — share one decode device across the playlist's runtimes.** Evaluated and
rejected: (b) would have `PlaylistSession` create one D3D11 device and inject it into every
per-item FFmpeg runtime (`av_hwdevice_ctx_create` supplied-device path) so the borrowed device
never changes. (a) wins on every axis but one: it **preserves the ownership model** (FFmpeg
still owns each decode device; the converter owns a *separate* device) where (b) inverts it; it
stays **inside the presenter project** where (b) spans Decoding + Playback; it matches the
present-stall investigation's §6 step 5 ("stop borrowing FFmpeg's device") verbatim where (b)
keeps borrowing; and it **generalizes to any decode-device change**, not just the playlist case.
(b)'s sole edge is avoiding the cross-device NV12 share — the one driver-compat risk (a) carries.

**Residual risk + safety.** Sharing a keyed-mutex NV12 texture has real driver-compat risk on
weak iGPUs (the kiosk's Intel HD 620) — the reason this was originally deferred. Two mitigations:
(1) `TryRebindDecodeDevice` returns `false` on any GPU failure, and the presenter **falls back to
the validated Decision-1 rebuild-per-swap** (bumping the otherwise-zero `device_change_rebuilds`
alarm) rather than breaking or hanging; (2) the teardown safety properties are **preserved and
improved** — the converter is still disposed off the UI thread and only after the compositor
releases the ring's keyed mutex, and it no longer releases a *borrowed* FFmpeg device at all
(Mechanism B is gone; the own device's only GPU work is the shader pass, not gated by DWM). The
per-frame staging keyed-mutex acquires are bounded (1000 ms, device-loss-guarded), the same risk
profile as the existing ring acquire — no new unbounded-hang surface (§9). On-kiosk A/B on the
HD 620 is the final gate, per ADR-0063.

## Observability (implemented alongside Decision 1)

- Split presented into **enqueued** (`FramesPresented`, at hand-off post) vs **committed**
  (`FramesCommitted`, incremented in the `UpdateWithKeyedMutexAsync` task's
  `RanToCompletion` continuation — the compositor actually drained the hand-off). Both are
  surfaced on `VideoSinkDiagnosticsSnapshot` (+ `LastCommittedAtUtc`).
- `PresenterStallEvaluator` gains a second rule and a `PresenterStalledReason`:
  `PresentLoopWedged` (enqueue flat while the sink feeds — the original signature) and
  `OutputNotComposited` (enqueue climbs while commit stays flat — frames reaching the
  compositor's queue but not the screen). Fed through the existing `SampleStall` →
  watchdog plumbing.
- New `frameflow.presenter.device_change_rebuilds` counter, distinct from
  `device_lost_rebuilds`: a healthy warm-sink-swap signal whose climb tracks playlist item
  boundaries. **Superseded by Decision 2:** the healthy signal is now
  `frameflow.presenter.device_change_rebinds` (the in-place bridge rebind), and
  `device_change_rebuilds` becomes the **0-valued fallback/regression alarm** — it climbs only
  if the in-place rebind fails on some GPU and the presenter falls back to a full rebuild.

## Consequences

- The warm-presenter playlist design (ADR-0062) is now **gapless** across player swaps: with
  Decision 2 there is no per-swap converter rebuild and no compositor re-import — only the cheap
  decode-bridge rebind is re-paid. (Decision 1 already removed the freeze by rebuilding per swap;
  Decision 2 removes that rebuild cost too, which is what makes a video→video transition over a
  warm `IMediaPlaylistPlayer` hitch-free — the signage requirement.)
- A small public-contract addition (`out nint device` on `TryGetD3D11Texture`) — acceptable
  under the project's no-external-consumers stance.
- The "frames not reaching the screen" class is now observable in logs, the diagnostics
  snapshot, and the `FrameFlow.Presenter` meter, instead of being structurally invisible.

## Amendment (2026-10-16): the shader-mode ring follows the view

### Context

The shader-mode ring was the frame's size, and the compositor scaled it to the view. A 1080p source
in a 384x216 view held 3 x 1920 x 1080 x 4 bytes = 24.9 MB of ring for a picture that needs
3 x 384 x 216 x 4 bytes = 1.0 MB, and the compositor minified it 5:1 on every compositor frame. With
many small views, ring memory grew with the sources' size, not with the screen area they cover
(#767). A filled view (#766) drew the whole frame and let the control's clip cut the overflow.

### Decision 1: the ring is the visible part's drawn size, capped at its coded size

The view computes `VisibleVideo` for the frame and asks for a ring of `VisibleVideo.PhysicalSize`,
held on each axis to the visible part's coded size (`ViewSizedRingPolicy.Wanted`, `Cap`). At or
above that size the ring is the part at its coded size and the compositor scales it up, as before.
A fitted view's part is the whole frame, so a view at least the frame's size gets the frame-sized
ring it always had. The super-resolution choice is unchanged: for `OutputSettle` every shader ring
is still the "shader at the frame's size" output, and only its size follows the view.

### Decision 2: the shader reads only the visible part

The constant buffer carries the visible part as texture coordinates (`ShaderSampling`), rewritten by
`SetSource` when the part changes, so a filled view crops in the same pass. The surface visual is
sized and placed to the rectangle the converter's part draws at (`VideoPlacement.Shown`), not the
whole frame's, and the 1:1 snap compares the ring with that rectangle. The snap now applies to any
D3D11 ring the size its rectangle is drawn at, not only the video processor's. The other producers,
the CPU uploader and the D3D12 converter, still hold the whole frame and are laid out as before.

### Decision 3: an area filter, not a grid of bilinear taps or a mip chain

One bilinear sample reads a 2x2 block, so past 2:1 it skips texels and fine detail aliases. Each
plane is read with an area (box) filter: where a ring pixel covers more than one texel of the plane,
the shader `Load`s every texel under the pixel's footprint and weights each by how much of it the
footprint covers. Where it covers one texel or less, it takes one bilinear sample as before, so a
ring at the frame's size draws what it drew before.

- **Not a mip chain.** An NV12 or P010 texture has one mip level. A chain would need a full-size
  RGB copy of the frame first, which is the memory the view-sized ring saves, and a `GenerateMips`
  per frame.
- **Not a grid of bilinear taps.** A grid of `ceil(ratio / 2)` taps is an exact box only when the
  taps fall on texel boundaries. At 5:1 it spaced three taps 5/3 texels apart, and one-pixel stripes
  came out 72 levels from mid grey. The area filter's weights are exact at any ratio.
- **Cost.** The area filter reads about one texel per source texel in total, whatever the ratio, and
  at most 64 texels per axis per ring pixel. Past 64:1 it averages the 64 nearest the pixel's
  centre, and fine detail can alias again.

`D3D11ConverterViewRingTests` reads the ring back for synthetic one-pixel stripes. At 1:1 every
stripe shows, and at 5:1 every ring pixel is within 40 levels of mid grey. A single bilinear sample
leaves them 127 levels out.

### Decision 4: hysteresis as a pure rule on the shell's clock

Each resize builds a ring and imports it into the compositor again, so a drag must not rebuild it on
every frame. `RingSettle.Advance` decides, with timestamps from the view's `TimeProvider`
(`RingSizeTime`):

- A ring smaller than the wanted size on either axis grows at once. Each axis that grows is rounded
  up to 64 px and capped, and the other axis keeps its size, so its shrink still waits. A ring too
  small would be scaled up by the compositor, which blurs. A drag that keeps growing rebuilds once
  per 64 px step on either axis.
- Any other change, a shrink or the exact size after a rounded-up grow, applies once the same size
  has been wanted for 500 ms. A shrinking drag rebuilds once when it stops, and a grown ring settles
  on the size it is drawn at, where the 1:1 snap holds.

### Decision 5: a view-driven resize replaces only the ring

`TryResizeRing` builds a new ring over the same staging texture and keeps the decode bridge, the
device and the shader pipeline. The old ring goes to the reaper after its imports are detached, as a
resized converter's parts do. A resize for a frame of another size (`TryResize`) builds its ring at
the view's size at once. Each view-driven resize is counted by `frameflow.presenter.view_resizes`,
apart from `resolution_resizes`, which still counts changes in the source's size.

### Decision 6: a paused view is drawn again when its layout needs it

A paused source sends no frame, so a layout change would leave a too-small ring scaled up, or a
filled view showing a crop that no longer matches. When the view's size or `Placement` changes and
the ring is now too small or holds another part, the view presents the frame it presented last
again. That redraw does not raise `FramePresented`. A smaller ring waits for the next frame.

### Consequences

- A 1080p source in a 384x216 view holds a 1.0 MB ring instead of 24.9 MB.
- A filled view draws only what shows, and on the driver's path upscales only that (#772,
  ADR-0082).
- Decode cost is unchanged. The decoder decodes at full size, and the shareable NV12 staging
  texture stays frame-sized.
- A change of display scaling alone, as when a paused window moves to another monitor, waits for the
  next frame.
- The D3D12 converter keeps a frame-sized ring. It draws in D3D12, copies into the ring and has no
  in-place resize, so the policy does not drop in (#774).
