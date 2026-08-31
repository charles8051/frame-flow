# ADR-0064: Zero-copy presenter converter — decode-device identity and ownership

> Drafted on branch `fix/warm-swap-presenter-device`; assigned ADR-0064 on merge
> to `main` per this repository's "number-at-merge" rule.

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
