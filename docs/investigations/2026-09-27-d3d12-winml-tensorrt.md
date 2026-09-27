# A D3D12VA frame into Windows ML's TensorRT-RTX without a CPU copy

**Date:** 2026-09-27
**Issues:** #288 (GPU-resident inference). Extends
[the DirectML spike](2026-09-27-d3d12-dml-zero-copy.md) to the provider
[the Windows ML investigation](2026-09-09-windows-ml-ep-selection.md) measured at about twice
DirectML's speed.

## Question

With Windows ML, an NVIDIA GPU runs models on TensorRT-RTX instead of DirectML, and TensorRT-RTX
reads CUDA memory, not a D3D12 buffer. Can the D3D12 tensor the DirectML spike builds reach it
without a copy, and what does the whole path cost?

## Verdict

**Yes, on an RTX 3080 Ti with Windows ML 2.3.42 (ORT 1.27.1).** CUDA imports the shader's D3D12
buffer, and ORT binds the CUDA pointer as the input of the TensorRT-RTX session. Per 1080p H.264
frame, yolov8n, one consumer, Release build, p50 of the second of two runs:

| Stage | GPU path | CPU path |
| --- | --- | --- |
| Download (`av_hwframe_transfer_data`) | none | 2.4 ms |
| NV12 to BGRA (`sws_scale`) | none; in the shader | 2.9 ms |
| Resize, normalize, lay out | 0.22 ms, compute shader | 0.47 ms, `ImageToTensor` |
| Model run, TensorRT-RTX | 2.0 ms, input already on the GPU | 2.2 ms, ORT uploads the input |
| **Total** | **about 2.2 ms** | **about 8.0 ms** |

Over both runs the GPU path was 2.2 to 2.7 ms and the CPU path 8.0 to 8.5 ms. Against the DirectML
spike's 3.0 ms, the model run drops from 2.8 ms to 2.0 ms.

## Findings

### 1. CUDA imports the D3D12 buffer

The tensor buffer is committed on a shared heap (`D3D12_HEAP_FLAG_SHARED`), and
`CreateSharedHandle` gives an NT handle for it. `cuImportExternalMemory` takes that handle, with
`CU_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE`, the dedicated flag, and the allocation size from
`GetResourceAllocationInfo` (4,915,200 bytes for 3 x 640 x 640 floats).
`cuExternalMemoryGetMappedBuffer` then maps the buffer. The CUDA device is the one whose LUID matches
the D3D12 adapter, and the spike uses its primary context. The tensor read back through CUDA equals
the tensor read back through D3D12 byte for byte. Only the CUDA driver API is used, from
`nvcuda.dll`, which ships with the driver.

### 2. ORT reads the imported memory in place

The TensorRT-RTX provider is chosen on the decoder's adapter. ORT's hardware-device metadata
carries the adapter's LUID (`LUID=88584`, the D3D12 adapter's 0x15A08), so the spike selects the
provider device by it, not by taking the first one. `InferenceSession.GetMemoryInfosForInputs()`
then gives the memory the session wants its input in: `TensorRTRTX` on CUDA device 0, the device
that imported the buffer. An `OrtValue` over the CUDA pointer in that memory is the model
input. As a check, the CPU path's tensor went through both routes: uploaded into the D3D12 buffer
and read through CUDA, and handed to ORT to upload itself. The outputs were identical, a maximum
difference of 0.

### 3. The spike synchronizes on the CPU

The shader's GPU work is waited on before the model runs, so CUDA reads a finished buffer. Building
it would keep the order on the GPU. That means importing the D3D12 fence as a CUDA external
semaphore and waiting on it on the stream the provider runs on. ORT 1.27's sync-stream API
(`RunOptionsSetSyncStream`) is the likely place to hand that stream over. This is untested.

### 4. Teardown order can crash the process

The process crashes with `0xC0000005` when three things hold together:

- TensorRT-RTX has run in the process.
- We still hold a reference to the decoder's D3D12 device and a queue on it.
- The decoder's last frame is freed, which tears down FFmpeg's D3D12 device context.

The crash comes while that last frame is freed, or later, when our queue is released or the session
disposed. Any two of the three exit cleanly. The DirectML spike has no TensorRT-RTX. Decoded frames
without our device reference are clean. So is our own device with nothing decoded.

Releasing our references before the decoder's last frame is freed is clean, with the whole chain in
place: shader, import and bound run. The mechanism was not found. The device's reference count stays
at 48 through every frame but the last, so the crash comes from the teardown, not from a count
that drifts. `--teardown-probe <mode>` reproduces each case.

This constrains the design. In a product the inference session usually outlives the decoders:
each playlist item opens its own decoder. That is the shape that crashes. The ownership model from
the device discussion is the first thing to try: FrameFlow creates the device and lends it to
FFmpeg with `av_hwdevice_ctx_alloc`. That changes which teardown releases what. It is untested.

**Follow-up, 2026-09-27 (#428):** tested, and it holds. With the decoder borrowing a
`HardwareDevice`, whether FFmpeg created the device (`owned-ffmpeg-` modes) or the probe lent its
own (`owned-ours-`), the crashing shape exits cleanly three runs of three, with and without the full
chain, while the same modes on the decoder's own device crash three of three. Freeing a decoder's
last frame then tears down its frames context but not the device context, which goes last. The
table is in [the borrowed-device record](../adr/borrowed-hardware-device.md).

### 5. The shader's parity is the DirectML spike's

The shader is the same code, so the tensor differences are the same: p99 12.6/255, on colour edges.
The model outputs differ from the CPU path's by a mean of 0.042. The first session open took
4.0 to 5.3 s, which is TensorRT's engine build (Windows ML investigation, cost/benefit). Detection
agreement is not shown, for the same reason as before: the corpus is synthetic.

## What it adds to building it

- **The binding is per provider; everything before it is shared.** DirectML reads the D3D12 buffer
  through `OrtDmlApi`. TensorRT-RTX reads the same buffer through a CUDA import. The decode, the
  frame accessor, the shader and the frame's lifetime are the same for both.
- **CUDA interop for the TensorRT-RTX path.** External memory, and an external semaphore for the
  fence once synchronization moves to the GPU.
- **The teardown order in finding 4**, until it is explained or the owned-device model removes it.
- **One ORT per process.** Windows ML ships its own `onnxruntime.dll` (1.27.1) and the DirectML
  package another (1.24.4), so the two spikes run in separate processes. Windows ML includes the
  DirectML provider, as its fallback.

## Reproducing

```bash
dotnet run --project spikes/D3D12WinMlProbe -c Release -- tests/corpus/files/test-1080p-h264-aac.mp4 --frames 90
```

It needs Windows 11 24H2 or later, an NVIDIA GPU with D3D12 video decode, and the TensorRT-RTX
provider already installed. The spike only registers providers; `WinMlProbe --acquire` installs
them. The teardown cases run with `--teardown-probe`: `frames-device-release-first` exits cleanly,
and `frames-device-release-after-frames` crashes.
