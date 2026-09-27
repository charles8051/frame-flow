# A hardware decode device the decoders borrow

**Status:** Accepted. Draft, pending number assignment at merge.

**Date:** 2026-09-27

**Issues:** #428, #422. Part of #288.

**Related:**
- [ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md) — its Decision 2 rejected sharing one
  decode device across a playlist's items, option (b), for the presenter. This record adopts that
  shape for the decode device, as an option, for inference. It leaves the presenter's own device as
  ADR-0064 has it.
- [GPU-resident inference](../feature-specs/gpu-resident-inference/spec.md) — the feature that needs
  a device outliving the decoders.
- [The Windows ML and TensorRT-RTX investigation](../investigations/2026-09-27-d3d12-winml-tensorrt.md),
  finding 4 — the teardown crash (#422).

## Context

Every video decoder creates its own device: `av_hwdevice_ctx_create(..., device: null, ...)`. A
player opens a decoder per playlist item and disposes it at the item's end, so the device goes too.

Inference needs the opposite lifetime. A `D3D12ImageToTensor` or a DirectML session built on the
decoder's device (#427) lives for the whole run and holds that device. When an item ends, the last
frame from its decoder is freed, and FFmpeg tears down the item's frames context and device context
while the inference side still holds the device. With a TensorRT-RTX session loaded, that crashes
the process with an access violation (#422). The mechanism was not found and may be in the driver.

ADR-0064 weighed one device shared across a playlist for the presenter and chose a device of the
presenter's own, bridging each frame across, because that kept FFmpeg as the decode device's owner
and the change inside the presenter. Inference cannot bridge each frame: its point is to read the
decoded texture in place.

## Decision

`FrameFlow.Decoding.HardwareDevice` is a hardware decode device that decoders borrow instead of
creating their own.

- It holds one reference to an FFmpeg `AVHWDeviceContext`. Each decoder that borrows it takes
  another, through `av_buffer_ref`, so the device lives until the last of them lets go.
- `HardwareDevice.Create(backend, device)` has FFmpeg create the device, as a decoder would, and
  FrameFlow holds it. `HardwareDevice.FromD3D12Device(device)` lends FFmpeg a device the caller
  created: `av_hwdevice_ctx_alloc`, `AVD3D12VADeviceContext.device`, `av_hwdevice_ctx_init`.
- `TryGetD3D12Device` gives inference the device to build on.
- `VideoDecoderOptions.Device` makes a decoder borrow it. The decoder then tries only that device's
  backend. A codec with no configuration for it falls back to software under `Auto` and fails under
  `Required`.
- `IPlayerBuilder.WithHardwareDevice` and `IPassBuilder.WithHardwareDevice` pass it to every
  decoder the player or pass opens, through `PlaybackController.Create`.
- The caller owns it. The player and the pass never dispose it. The order is: the player or pass,
  then anything built on the device, then the device.
- The type is backend-neutral, as `AVHWDeviceContext` is. Only `FromD3D12Device` and
  `TryGetD3D12Device` are specific to one API, and others add alongside them.

It is an option. A player without one keeps a device per decoder, as before.

## Evidence

The #421 spike's teardown probe, extended with `owned-ffmpeg-` and `owned-ours-` modes that decode on
a `HardwareDevice`. Each mode runs a TensorRT-RTX session, holds the device and a queue on it, frees
the decoder's frames, and then releases the device. Three runs each, on an RTX 3080 Ti:

| Mode | Device | Exit |
| --- | --- | --- |
| `frames-device-release-after-frames` | the decoder's own | access violation, 3 of 3 |
| `owned-ffmpeg-device-release-after-frames` | `HardwareDevice.Create` | clean, 3 of 3 |
| `owned-ours-device-release-after-frames` | `HardwareDevice.FromD3D12Device` | clean, 3 of 3 |
| `frames-shader-import-bound-release-after-frames` | the decoder's own, full chain | access violation, 3 of 3 |
| `owned-ffmpeg-shader-import-bound-release-after-frames` | `HardwareDevice.Create`, full chain | clean, 3 of 3 |
| `owned-ours-shader-import-bound-release-after-frames` | `HardwareDevice.FromD3D12Device`, full chain | clean, 3 of 3 |

The full chain adds the preprocessing shader, the CUDA import of its buffer and a run with the input
bound. With a borrowed device, freeing a decoder's last frame tears down its frames context but not
the device context, which is released last, after the session.

## Consequences

- **#422 has a supported answer.** A consumer that builds inference on the decoder's device gives
  the player a `HardwareDevice` and builds on `TryGetD3D12Device`. A consumer that takes the device
  from a frame and holds it across the decoder's end still hits the crash with TensorRT-RTX loaded.
- **D3D12VA is reachable from the builders.** A player or pass given a D3D12VA device decodes with
  it, where the builders otherwise pick D3D11VA on Windows (#429 covers choosing a backend without
  a device).
- **The presenter is unchanged.** It keeps its own device and bridges each frame (ADR-0064). A
  D3D12VA frame still has no presenter (#429).
- **Two decoders on one device run at once in a playlist's handover.** FFmpeg's device contexts
  serialize their own use (`AVD3D12VADeviceContext.lock`), and each decoder keeps its own frames
  context and pool.
