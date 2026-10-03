# ADR-0082: Opt-in driver video super resolution on the D3D11 presenter

## Status

Accepted (2026-10-02) and implemented (2026-10-02, #561) for #560.

> **Amended 2026-10-03 ([below](#amendment-2026-10-03-the-driver-upscales-one-stream-and-concurrent-blits-did-not-hang)).** §3's drain is removed. The one-view lease stays,
> as the driver's own limit rather than a guard against a hang.

Reintroduces `VideoProcessorBlt` into the Windows zero-copy presenter as an opt-in path, under
the limits below. [ADR-0063](ADR-0063-nv12-pixel-shader-color-conversion.md) removed it from the
default path, and the default path does not change. Builds on
[ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md) Decision 2: the converter owns its
own device, and the video processor runs on that device.

## Context

### What the driver offers

NVIDIA's driver upscales video with a learned model ("RTX Video Super Resolution") when an
application turns on a stream extension on a D3D11 video processor:

```c
struct { UINT version; UINT method; UINT enable; } ext = { 1, 2 /* super resolution */, 1 };
ID3D11VideoContext::VideoProcessorSetStreamExtension(
    processor, 0, &NVIDIA_PPE_INTERFACE_GUID /* d43ce1b3-1f4b-48ac-baee-c3c25375e6f7 */,
    sizeof(ext), &ext);
```

Chromium (`swap_chain_presenter.cc`) and mpv (`vf_d3d11vpp.c`) make this call. The work happens
inside the following `VideoProcessorBlt`, so it exists only on the video processor. The 3D
pipeline cannot reach it.

### What was measured

On an RTX 3080 Ti with driver 610.47, scaling a 1280x720 NV12 frame to 3840x2160 BGRA:

| Case | Time per blit |
|---|---|
| Plain video-processor scaling | 0.42 ms |
| Extension on, back to back | 3.74 ms (p95 4.0) |
| Extension on, to 2560x1440 | 3.57 ms |
| Extension on, paced at 30 fps | 8.1 ms (p95 10.7) |
| Extension on, 1280x720 to 1280x720 | 3.78 ms |
| Extension on, 1280x720 to 640x360 | 0.53 ms (no change to the output) |

The paced figure is higher because the GPU clocks down between frames (495 MHz, P3).

Timing has to run to readable pixels: copy a 2x2 region of the output to a staging texture and
`Map` it. The blit's D3D11 event query fires after about 0.4 ms whether or not the extension is
on, and the upscale finishes afterwards. A timestamp-query pair around the blit reads 0, because
the video processor does not run on the 3D queue.

A second spike repeated the measurement with the converter's actual resource shapes: its own
device on the decoder's adapter, a keyed-mutex NV12 input and a keyed-mutex BGRA output. The
cost matched (3.74 ms against 0.46 ms). It also found that the driver rejects a video-processor
input view (`E_INVALIDARG`) on an NV12 texture bound `ShaderResource` only, which is how
`D3D11Nv12SharedConverter` creates its staging texture. `RenderTarget | ShaderResource` is
accepted, and so are `None` and `Decoder`. The `VideoSupport` device-creation flag was not
needed on this driver.

### What the API cannot tell us

Whether the driver applied the upscale is not observable from the call:

- `VideoProcessorSetStreamExtension` returns `S_OK` whether or not anything happens.
- `VideoProcessorGetStreamExtension` returns `S_OK` and zeros, before and after the extension is
  turned on.
- When the upscale is inactive, the output is byte-identical to plain scaling. When it is
  active, the output differs: mean absolute difference 2.4–3.3 levels on real footage, 0.59 on a
  frame downscaled from a clean 1080p source.

The upscale is inactive when any of these hold:

- the GPU is not an RTX part;
- "Super Resolution" is off in NVIDIA App → System → Video → RTX Video Enhancements. The user
  controls it, and it carries the quality level;
- the session is remote. Over Remote Desktop the call succeeds and does nothing, and the NVIDIA
  App hides its Video page.

It worked for an arbitrary executable name, so there is no application allow-list.

### The presenter today

`CompositionInteropVideoView` converts each D3D11VA frame to BGRA at the frame's own size, in a
three-buffer keyed-mutex ring, and imports the ring into Avalonia's compositor. The compositor
scales the imported surface into the aspect-fit rectangle that `UpdateSurfaceLayout` computes.
The converter uses a pixel shader on its own device (ADR-0063, ADR-0064).

Super resolution needs the video processor to produce the output at the size it will be shown.
So on this path the ring's size has to follow the view, not the frame.

### The hazard ADR-0063 removed

ADR-0063 replaced `VideoProcessorBlt` because two presenters on one weak Intel iGPU issued
concurrent blits on its single fixed-function unit and hung inside the driver. A thread wedged
in a driver call cannot be killed. Any path that brings the blit back has to make that
configuration impossible, not unlikely.

## Decision

### 1. Opt-in, off by default

`CompositionInteropVideoView` gains a `DriverSuperResolution` property, `false` by default. While
it is `false` the presenter creates exactly the converter it creates today: same device flags,
same staging texture, same shader pass. Turning it on or off drops the converter and builds the
other kind on the next frame.

### 2. When it engages

A pure function in `FrameFlow.Avalonia.Windows.Core` decides, per frame, whether the presenter
scales through the video processor with the extension on. It engages only when all of these
hold:

| Input | Requirement |
|---|---|
| `DriverSuperResolution` | `true` |
| Frame | a D3D11VA `GpuVideoFrame` in NV12 |
| Adapter vendor | NVIDIA (`0x10DE`), read from the decoder's adapter |
| Session | local (`GetSystemMetrics(SM_REMOTESESSION) == 0`) |
| Scale | the target rectangle is larger than the frame on both axes |
| Lease | this view holds the process-wide lease (§3), or can take it |
| Video processor | it has not failed to set up for this view (§5) |

Otherwise the presenter uses the shader path at the frame's size, as today. The function also
returns a status that names the first failing condition (§6).

The scale rule follows the measurements. Downscaling with the extension on changes nothing, and
1:1 costs the full price for a small sharpening effect, so neither engages. The function does not
gate on the frame's resolution. Which input sizes the driver upscales is the driver's policy, and
a frame it declines costs plain video-processor scaling (0.4 ms).

There is no check for an RTX part. The PCI vendor ID does not distinguish RTX from GTX, and a
device-ID table would go stale. On a GTX part the extension does nothing and the cost is plain
video-processor scaling.

### 3. The concurrent-blit hazard

Three limits keep ADR-0063's hang out of reach:

1. **NVIDIA only.** The video processor path never runs on another vendor's adapter. The hang
   was on an Intel iGPU, and the extension does not exist elsewhere.
2. **One view per process.** A process-wide lease admits one view at a time to the video
   processor path. A second view that wants it stays on the shader path and reports
   `InUseByAnotherView`. A view takes the lease when it builds a video-processor converter and
   releases it when it drops that converter or tears down. The GPU may still be running the
   dropped converter's blits at that point, so the release also opens a drain. No holder blits
   until an event query on the dropped converter's device reports its work complete. A view that
   takes the lease in that interval fills its ring with the shader. The wait runs off the UI
   thread and gives up after 2 s, the default GPU timeout (TDR).
3. **Upscale only.** The shader path handles every frame the scale rule rejects.

The lease is per process, not per adapter. Two views on two different GPUs in one process is
rare, and a per-adapter lease would add a key to get wrong for that case.

### 4. Where the blit runs

The blit runs on the converter's own device (ADR-0064), on its immediate context, on the UI
thread where the shader pass runs today. In video-processor mode the converter differs from the
shader converter in four ways:

- The staging NV12 texture is created `RenderTarget | ShaderResource`, so that the driver accepts
  an input view on it (Context).
- The ring is sized to the target rectangle, not the frame.
- It holds a video device, a video context, an enumerator for frame size → target size, one
  processor, one input view over the staging texture and one output view per ring buffer.
- `ConvertInto` copies the decode slice into the staging texture as it does today, then calls
  `VideoProcessorBlt` instead of replaying the shader command list. The keyed-mutex brackets do
  not change.

The processor is set up once per converter: input colour space `YCBCR_STUDIO_G22_LEFT_P709`,
output `RGB_FULL_G22_NONE_P709` (the conversion the shader implements), progressive frames, auto
processing off, source and destination rectangles covering the whole input and output, and the
extension on. It does not issue a separate call per frame.

The decode bridge, warm-swap rebind and device-loss handling are unchanged.

### 5. Failure

If any video-processor object fails to create, the converter disposes what it created, logs the
failure once, and falls back to the shader path at the frame's size. The view records the
failure and the decision function returns `Unavailable` until the decode device changes or the
property is toggled. A converter that fails this way releases the lease.

A `VideoProcessorBlt` that fails with anything other than device loss turns the video processor
off for that converter, and the shader fills the ring from then on. The view records the failure
the same way, and the settle planner (§7) rebuilds the converter at the frame's size once the
shader output has been wanted for the interval. Device loss goes to the existing device-loss path.

### 6. Status

The view exposes `DriverSuperResolutionStatus`, updated on each present:

| Value | Meaning |
|---|---|
| `Off` | `DriverSuperResolution` is `false` |
| `Requested` | The presenter scales through the video processor with the extension on |
| `NotUpscaling` | The target is not larger than the frame on both axes |
| `UnsupportedAdapter` | The decoder's adapter is not NVIDIA |
| `UnsupportedFrames` | The frames are not D3D11VA NV12 (D3D12VA, or a software frame) |
| `RemoteSession` | The session is remote |
| `InUseByAnotherView` | Another view holds the lease |
| `Unavailable` | The video processor failed to set up |

The engaged value is `Requested`, not `Active`. The presenter knows it made the call. It cannot
know whether the user's NVIDIA setting let the driver act on it (Context).

### 7. The ring follows the view

In video-processor mode the target is the frame's drawn rectangle in physical pixels: the
`VideoPlacement` draw rectangle `UpdateSurfaceLayout` uses (#542), multiplied by the top level's
`RenderScaling`, rounded. It is the rectangle before rotation, at the display shape, so the ring
holds the frame as coded and the visual turns it. The layout and the target both come from
`VideoPlacement.Fit`, and `SuperResolutionPolicy.TargetSize` rounds it, so the layout and the ring
cannot disagree. While the ring is exactly that size and the frame is not turned a quarter, the
surface visual's offset and size are snapped to physical pixels, so the compositor draws the ring
1:1 rather than resampling it by a fraction of a pixel. Mid-resize, while the ring is still the old
size, the visual follows the window and the compositor scales the ring as before.

A change of target rebuilds the converter through the same drop path a resolution change uses.
The ring is shared with the compositor, and that path already disposes it in the order the
teardown investigation requires: imported images first, through the compositor, then the
producer off the UI thread.

A live window resize changes the target on every layout pass, and each rebuild allocates three
textures at the new size and re-imports them. So the rebuild waits for the target to settle. A
pure planner takes the applied output, the wanted output and a timestamp, and applies a change
only when the wanted output has been the same for 200 ms. The first frame applies at once. Until
the change applies, the compositor scales the current ring. The shell supplies the timestamp, and
the planner reads no clock.

### 8. Out of scope

- **D3D12VA frames.** They are converted on the decoder's D3D12 device (#429). The extension is
  defined on the D3D11 video processor, and no D3D12 equivalent was found.
- **Software frames.** The CPU upload path stays as it is.
- **P010 and HDR.** The D3D11 converter handles NV12 only.
- **Intel's equivalent**, a Video Processing Extension interface on Intel drivers. The decision
  function and the converter's video-processor mode are where it would attach. Tracked as a
  follow-up issue.
- **Reading the NVIDIA App setting.** No documented API for it was found.

## Verification

On an RTX 3080 Ti, driver 610.47, in a local session with Super Resolution on in the NVIDIA App:

- **Colour parity.** The converter's video-processor mode, with the extension off, matches its
  shader at the frame's size and at twice it in luma (under 0.5/255 on average) and in each
  channel's average (under 0.5/255). The two reconstruct chroma differently at sharp colour
  edges: up to 188/255 there on the test pattern, 2.6 on average, against 0.2 for luma. With a
  BT.601 input matrix luma moves 17/255, and with full-range input 10/255, so both tests fail on
  either mistake.
- **The extension through the converter.** 1080p to 3840x2160 with the extension on differs from
  the same blit with it off by 0.751/255 on average, worst 87. That test runs only when
  `FRAMEFLOW_EXPECT_DRIVER_VSR=1` says the machine is set up for it, since the driver's upscale
  is not observable otherwise.
- **On screen.** The ZeroCopyInterop example, a 1280x720 still from real footage, fullscreen on a
  2560x1440 display, `S` toggling the property while it plays: the converter rebuilt at
  2560x1440 with the video processor, then back to 1280x720 with the shader. Screen captures with
  it off were identical before and after; with it on they differed by 0.782/255 on average, and
  the Laplacian variance, a measure of fine detail, rose from 1.14 to 6.2.

## Consequences

- A consumer can get the driver's upscale on an RTX GPU by setting one property. Everyone else
  is unaffected: the default converter is the one that ships today.
- **GPU cost.** About 3.7 ms per frame back to back, or 8 ms at 30 fps once the GPU clocks down,
  for 720p to 4K on an RTX 3080 Ti. It is paid only while the view is larger than the frame.
- **Memory.** The ring is three BGRA textures at the target size: about 99 MB at 3840x2160,
  against about 11 MB for a 1280x720 ring.
- **A resize costs a converter rebuild**, once the size has settled. That includes creating a
  device and compiling the shaders, as a resolution change does today.
- **A paused video keeps the old ring after a resize.** The presenter keeps no frame after
  presenting it, so the compositor scales the last ring until the next frame arrives.
- **Status cannot confirm the upscale.** `Requested` means the call was made. Confirming the
  effect takes a pixel comparison against plain scaling, which is a diagnostic, not something the
  presenter does per frame.
- **One view per process** gets the upscale. Lifting that needs a soak of two video-processor
  views on one NVIDIA GPU, which this ADR does not have.

## Alternatives considered

- **Scale through the video processor on every frame, with or without the extension.** Brings
  the blit back for every consumer on every vendor, which is what ADR-0063 removed.
- **A process-wide lock around `VideoProcessorBlt`.** Serializes the CPU-side calls, but the GPU
  runs blits asynchronously after the call returns, so two views' blits can still overlap on the
  unit. The lease removes the second view's blits instead.
- **Resize the ring in place.** Saves the device creation and shader compile on a resize. The
  ring is co-owned with the compositor, and disposing it safely needs the ordering the drop path
  already implements. Reuse that path first and revisit if rebuild cost shows up in practice.
- **Detect engagement with a pixel comparison.** Blitting each frame twice and reading both back
  would answer the question, at more than the cost of the feature. It belongs in a diagnostic or
  a test.
- **Default the staging texture to `RenderTarget | ShaderResource` on the shader path too.** It
  would remove one difference between the two converters, at the price of changing the default
  path's resources for no benefit to it.

## Amendment (2026-10-03): the driver upscales one stream, and concurrent blits did not hang

### What was measured

`D3D11VideoProcessorSoakTests` runs two converters in the video processor mode on one adapter.
Each has its own decoder, its own device (ADR-0064), its own compositor-side reader and its own
thread. Each blits 1920x1080 to 3840x2160 with the extension on, unpaced. A blit or ring that does
not finish within 10 s fails the run. A 20 s hold injected into one stream showed that the check
fires. The test runs only when `FRAMEFLOW_VP_SOAK_MINUTES` is set.

On an RTX 3080 Ti with driver 610.47, in a local session, for 30 minutes:

| | Stream A | Stream B |
|---|---|---|
| Blits | 188,367 | 346,461 |
| Slowest ring of three blits | 45 ms | 17 ms |
| Upscaled by the driver | 30 of 30 checks | 0 of 30 checks |

There was no stall, no failed blit, no device loss, and no display-driver event in the System
log.

Each check blits frame 0 and compares the result with plain video processor scaling of the same
frame. Stream A differed by 0.751 to 0.763 levels on average, as a single converter does. Stream B
was byte-identical to plain scaling every time.

Two processes with one stream each split the same way. The second process got plain scaling until
the first exited, and was upscaled on its next check.

From these runs:

- **The driver upscales one stream at a time across the system.** The first stream to blit with
  the extension gets the upscale and the others get plain scaling. Nothing in the API says which.
  When the holder is released, another stream gets it.
- **Concurrent blits did not hang.** Two converters blitting at once on one NVIDIA GPU ran 30
  minutes without a stall. ADR-0063's mechanism, concurrent blits wedging the one fixed-function
  unit, did not reproduce here. The soak says nothing about other vendors' drivers, or about a
  display-mode change during a blit.

VLC calls `VideoProcessorBlt` on every frame in its video processor and super resolution upscale
modes (`modules/video_output/win32/d3d11_scaler.cpp`), with NVIDIA's extension, Intel's VPE and
AMD's AMF, and sets no limit on how many videos use it.

### Decision

1. **The drain goes.** §3's drain held every view's blits back until a dropped converter's blits
   had completed, for up to 2 s. It guarded against the overlap the soak ran for 30 minutes.
   `SuperResolutionLease.BeginDrain` and `MayBlit` go with it, and so does `ConvertInto`'s
   `videoProcessor` argument, which existed for the drain. A view releases the lease when it drops
   its video processor converter, and disposes the converter off the UI thread like any other.
2. **The lease stays, for the driver's limit.** A second view on the video processor path would pay
   for a ring at the size it is shown and still get plain scaling. It stays on the shader path and
   reports `InUseByAnotherView`, as before.
3. **NVIDIA only stays**, because the extension is NVIDIA's. It is no longer a safety argument.
   Intel's equivalent (#562) needs a soak of its own on Intel hardware: the hang ADR-0063 describes
   was on an Intel iGPU.
4. **Upscale only stays.** It rests on the measurements under Context, not on the hang.

### Consequences

- A view in another process can hold the driver's upscale. This view then reports `Requested` and
  gets plain scaling at the size it is shown, paying for the larger ring. The status already could
  not confirm the upscale, and this is one more case it cannot see.
- `WaitForSubmittedBlits` has no caller outside tests. It stays for them: the soak uses it to bound
  the blits in flight and to catch one that never completes.
