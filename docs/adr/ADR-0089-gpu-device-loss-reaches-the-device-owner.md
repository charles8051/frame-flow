# ADR-0089: GPU device loss reaches the device's owner, once, and each player's failure names it

**Status:** Accepted. Numbered ADR-0089 on 2026-10-18.

**Date:** 2026-10-16

**Issues:** #759, #530.

**Related:**
- [A hardware decode device the decoders borrow](ADR-0088-borrowed-hardware-device.md): `HardwareDevice`,
  the one public type that owns a GPU device several players share. This record gives it an event.
- [ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md): the presenter owns a device of its
  own and rebuilds it after a loss. Unchanged.
- [ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md): one `ErrorCategory` across the
  playback stack. This record adds a member to it.
- #757 (PR #764): `MediaErrorClassification`, the one place a failure becomes a category, and
  `MediaException`, which carries the FFmpeg code beside the category. This record follows both.
- #758 (PR #762): a load or a replace can leave `PlaybackState.Error`.

## Context

When a GPU is removed or reset (a TDR, a driver update, a driver fault), every device created on it
is lost. Every later call on such a device fails, and it does not come back. The recovery is a new
device.

FrameFlow had three partial answers and no public one.

- The composition-interop presenter detects loss on its own device, drops it and rebuilds on the
  next frame (`PresenterTeardownMetrics.RecordDeviceLostRebuild`). Its devices are private to it.
- The decoder's device was not checked. A decode on a removed device fails in FFmpeg, and the
  player reported that as `ErrorCategory.Decode`, the same as a corrupt packet.
- Two lists of device-loss HRESULTs existed, in `D3D11DeviceLoss` and in the inference factory's
  `ConstructFailure`. Neither had `DXGI_ERROR_DRIVER_INTERNAL_ERROR` (#530).

A caller with several players on one `HardwareDevice` saw each player fail on its own, with a
category that said nothing about the device. It could not tell a lost device from bad media, and it
could not rebuild every player once.

## Decision

### 1. One list of device-loss codes

`FrameFlow.Media.Core.DeviceLoss` is the list: `DXGI_ERROR_DEVICE_REMOVED`, `DXGI_ERROR_DEVICE_HUNG`,
`DXGI_ERROR_DEVICE_RESET`, `DXGI_ERROR_DRIVER_INTERNAL_ERROR` and `D3DDDIERR_DEVICEREMOVED`. The
presenter's `D3D11DeviceLoss`, the inference factory's `ConstructFailure` and the decoder all read
it. `DeviceLostException.IsDeviceLostCode` exposes it publicly, which is how the inference
abstractions reach it without an internals grant.

`DXGI_ERROR_INVALID_CALL` stays out. `GetDeviceRemovedReason` can return it, but it says the
application made a bad call, not that the driver failed.

### 2. `ErrorCategory.DeviceLost`, with the reason beside it

`ErrorCategory` gains `DeviceLost`, appended so the existing values keep their numbers.

`DeviceLostException` carries `RemovedReason`, the HRESULT the device reported, and sets its own
`HResult` to it. It derives from `InvalidOperationException`, as `MediaException` does.
`PlaybackError.DeviceLoss` finds it behind a reported error, as `PlaybackError.MediaFailure` finds a
`MediaException`.

`MediaErrorClassification.Categorize` returns `DeviceLost` when any exception in the chain is a
`DeviceLostException` or carries a device-loss HRESULT. It checks the whole chain before anything
else. A decode failure on a lost device is the loss, not the FFmpeg code that revealed it.

### 3. The decoder asks its device after a fault

When `avcodec_send_packet` or `avcodec_receive_frame` faults on a D3D11VA or D3D12VA decoder, the
decode driver asks the codec for the device's removed reason. `VideoDecoder` reads
`GetDeviceRemovedReason` from the `ID3D11Device` or `ID3D12Device` in its own reference to the
device context (`DeviceRemovedReason.Query`). The pure `DecodeDriver.FaultOnDevice` decides what to
throw: a `DeviceLostException` around the `MediaException` when the reason is a loss, and the
`MediaException` alone otherwise.

The device is asked only after a fault, so a decode that does not fail pays nothing. Other backends
report `S_OK`. They have no removal query FrameFlow can read.

### 4. The shared device raises `DeviceLost` once

`HardwareDevice` gains `event EventHandler<HardwareDeviceLostEventArgs> DeviceLost` and
`bool IsLost`. A decoder borrowing a `HardwareDevice`, lent by the caller or cached by the player,
reports the reason it read to that device. The device advances a pure `DeviceLossLatch`: the first
report of a loss raises, and every later report, of any reason, raises nothing.

The event is raised on the thread pool, not on the decoder's thread. Its handler may dispose the
players on the device, which wait for their decoders, and a handler raised on the decoder's thread
would wait for itself. Each handler runs on its own: one that throws is logged to the logger the
device was created with (`HardwareDevice.Create` and `FromD3D12Device` take an optional `ILogger`),
and the handlers after it still run. Nothing escapes the thread-pool callback, which would end the
process.

`HardwareDevice` is the owner because it is the one public object that outlives the players sharing
a device. No new type was needed.

### 5. Recovery

A player built with a lent `HardwareDevice` decodes only on it, and that device stays lost. The
caller disposes the players and the device, creates a new device and builds the players again. One
`DeviceLost` event says when, however many players were on the device.

A player not lent a device keeps its own per backend, and already drops it when an item faults (the
2026-10-12 amendment to the borrowed-device record). Loading again on the same player creates a new
device. A single-item player whose item fails ends in `Ended`, where a controller unloads before it
loads. A player that reaches `Error` takes a load or a replace directly (#758).

## Consequences

- A caller holding only a player learns of the loss from its error's category. A caller holding the
  device learns of it once, from the device.
- A video sink that throws a device-loss exception reports `DeviceLost` too: the per-frame failure
  and the fault raised after a run of them both go through `Categorize`.
- `DXGI_ERROR_DRIVER_INTERNAL_ERROR` rules an inference provider out for later models, as the other
  loss codes did (#530).
- The presenter keeps rebuilding its own device, and raises nothing. With software decode, playback
  carries on through a loss on the presenter's device alone.

## Limits

- Detection depends on a decode call failing. If FFmpeg conceals a failed hardware decode and keeps
  returning frames, the loss is not seen until a call does fail. This was not measured: no test
  here removes a real device.
- A new decoder opened on a lost lent device fails to bind and falls back to software under
  `HardwareDecodeMode.Auto` without asking the device. The event is raised by the fault that came
  before it, if there was one.
- Only D3D11VA and D3D12VA are asked. A loss on CUDA, VAAPI or Vulkan reports the decode failure's
  own category.

## Verification

- Pure tests: the code list, the latch, the category of every chain shape, the driver's choice of
  exception, and `ConstructFailure` with `0x887A0020` as an HRESULT and in message text.
- Shell tests on a live D3D11VA and D3D12VA device: the removed-reason read returns `S_OK` on a live
  device, a report raises the event once with its reason, and the event runs off the reporting
  thread.
- An integration test plays through a sink that throws `DeviceLostException` and checks the
  player's failure is `DeviceLost` with the reason, then unloads, loads and plays again.
- Each new test was shown to fail with its rule removed: the code dropped from the list, the latch
  ignored, the category pre-pass removed, the driver not wrapping, a wrong vtable slot, and the
  event raised on the reporting thread.
- A real device removal is not tested here. Forcing one is a TDR on the machine running the suite.

## Amendment (2026-10-17)

`IsLost` had no reader in FrameFlow, and the player's cached devices, being internal, had no
subscriber to their event. Two things followed.

- **A healthy device was dropped on unrelated faults.** The item runtime dropped its cached device
  for the bound backend on every fault: a corrupt packet, a sink's escalation, the pacer's own
  fault, even a fault in an item prepared ahead. The next item paid for a new device, which the
  cache exists to avoid. The drop was by backend, so a late fault from an old item could drop the
  device a newer item had just been lent.
- **A lost device kept being lent.** Neither the cache nor a decoder lent a device asked whether it
  was lost.

Decided:

- **The cache never lends a lost device.** `HardwareDeviceCache.Borrow` replaces a cached device
  whose `IsLost` is true with a new one and releases the old.
- **A fault evicts only a lost device, by identity.** On a pump, graph or pacer fault, the item
  runtime asks the cache to evict the device its own decoder borrowed (`IItemMedia.BoundDevice`),
  and the cache does so only if that device is lost and is still the one it lends for the backend.
  A live device stays whatever the fault. This replaces the drop on every fault in decision 5 and
  in decision 7 of the media-clips record.
- **A decoder opened on a lost lent device fails fast.** `VideoDecoder.Open` throws
  `DeviceLostException`, with the device's reason, where it would have borrowed a lent device that
  is lost, under `Auto` as under `Required`. It used to fall back to software under `Auto`, which
  is the second limit above; that limit is withdrawn. `DeviceLostException` and `HardwareDevice`
  documented that every later item opened on a lost device fails, and a caller that reads the loss
  from a player's failure only hears of it if the open says so. A codec with no configuration for
  the device's backend still decodes in software: it never touches the device.

Consequences:

- A corrupt file no longer costs the next item a device. A lost D3D11VA or D3D12VA device is
  released at the fault that revealed it, and replaced at the next borrow in any case.
- Detection is still only D3D11VA and D3D12VA (the third limit above). On CUDA, VAAPI or Vulkan a
  device that a driver failure left unusable used to be replaced after the item faulted; it now
  stays cached until the player is disposed, and every later item on that backend fails the same
  way. Removing the drop on every fault was chosen over keeping it for those backends: the cost it
  imposed on every corrupt packet is certain, the failure it guarded against is rare and
  unmeasured here.

Verification, each shown to fail with its rule removed: a corrupt-packet fault keeps the cached
device (fails with the drop on every fault put back); a fault on a lost device evicts it (fails with
no eviction); a late fault from an old item leaves the newer device (fails with the drop on every
fault, and with eviction by backend rather than identity); the cache does not lend a lost device
again (fails with the `IsLost` check removed from `Borrow`); a decoder opened on a lost lent device
fails with `DeviceLostException` under `Auto` and `Required` (fails with the check removed: both
opened on the device). The devices are FFmpeg device contexts allocated and never opened
(`HardwareDevice.Unopened`), so the tests run without a GPU, and every loss is fed in.
