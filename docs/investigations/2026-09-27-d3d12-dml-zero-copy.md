# A D3D12VA frame into DirectML without a CPU copy

**Date:** 2026-09-27
**Issues:** #288 (GPU-resident inference). Evidence for #298's DirectML question, #289's accessor
and #291's device-side preprocessing. Builds on #415, which found D3D12VA's frame pool grows.

## Question

Can a frame decoded with D3D12VA reach a DirectML session with no CPU copy? That means building the
session on the decoder's own device, waiting on the decoder's fence on the GPU, writing the model
input with a compute shader, and binding that buffer to ORT as the tensor. And what does it save?

## Verdict

**Yes, on an RTX 3080 Ti with FFmpeg 9 and ORT 1.24.4 DirectML.** Every step works. Per 1080p H.264
frame, yolov8n, one consumer, Release build, p50 over two runs of 90 frames:

| Stage | GPU path | CPU path |
| --- | --- | --- |
| Download (`av_hwframe_transfer_data`) | none | 2.5 ms |
| NV12 to BGRA (`sws_scale`) | none; in the shader | 2.9 ms |
| Resize, normalize, lay out | 0.18 ms, compute shader | 0.58 ms, `ImageToTensor` |
| Model run | 2.8 ms, input already on the GPU | 3.25 ms, ORT uploads the input |
| **Total** | **about 3.0 ms** | **about 9.2 ms** |

The saving is the download and the conversion, as the Release re-measurement in the
[GPU-resident spec](../feature-specs/gpu-resident-inference/spec.md) predicted, plus the input
upload inside the model run.

## Findings

### 1. There is one device, so nothing is shared across devices

`D3D12CreateDevice` on the decoder's adapter returns the device FFmpeg decoded on: the same
`ID3D12Device` pointer. D3D12 devices are one per adapter per process. The session needs to be
built on that device. No shared handles, no second device, and no cross-API fence are involved.

### 2. ORT accepts our device and our queue

`DMLCreateDevice1` on the decoder's device, then `OrtDmlApi.SessionOptionsAppendExecutionProvider_DML1`
with that DirectML device and a compute queue we created. The managed binding has no `OrtDmlApi`, so
the spike reaches it through the C API: `OrtApi` entry 195 is `GetExecutionProviderApi` in the
1.24.4 headers.

### 3. The frame carries its own fence

Each `AVD3D12VAFrame` holds its texture, a subresource index (0: the pool is not a texture array),
its own `ID3D12Fence`, and the value the fence reaches once the frame is written (1 for a fresh
frame). `ID3D12CommandQueue::Wait` on our queue orders the shader after the decode on the GPU, with
no CPU wait.

### 4. The binding is exact

`CreateGPUAllocationFromD3DResource` turns a D3D12 buffer into a DirectML allocation. The buffer is
in `UNORDERED_ACCESS`. That allocation, wrapped as an `OrtValue` with `DML` memory info, gives the
model the tensor without a copy. As a check, the CPU path's tensor went through both routes: uploaded
into the buffer and bound, and handed to ORT to upload itself. The outputs were identical over 10
frames, a maximum difference of 0.

### 5. The shader is close to the CPU path, not identical

One dispatch turns the NV12 texture into 640x640 RGB planes in `[0, 1]`. It uses nearest luma and
chroma and BT.601 limited range, which is what swscale assumes for an untagged stream. Against the
CPU path, swscale's NV12 to BGRA followed by `ImageToTensor` nearest, the absolute differences over
10 frames were p50 0.7/255, p99 12.6/255, p99.9 55.8/255 and max 195/255. The model outputs differed
by a mean of 0.041.

The large differences sit on colour edges, where the two chroma upsamplers disagree. At the worst
pixel, the shader's value is right for the chroma sample it reads (Y 76, V 242 gives R 252). The CPU
value, R 57, implies a V between this chroma row and the one above. Of nearest, centred bilinear and
left-sited bilinear chroma, nearest came closest (p99 12.6, against 21.4 and 19.8). swscale's exact
filter was not reproduced. Matching swscale is not the requirement; choosing a chroma filter is.

### 6. Detection agreement is not shown

The corpus clips are synthetic test patterns, and yolov8n finds nothing in them on either path.
Showing that the two paths agree on detections needs footage with objects in it.

## What building it takes

- **A D3D12 accessor on `GpuVideoFrame`.** The spike read the internal `AVFrame*` by reflection.
  The accessor needs the texture, the subresource and the fence with its value. It has the same
  shape as #289's CUDA pointer. Done since: `GpuVideoFrame.TryGetD3D12Texture`, which the spikes
  now use.
- **A DirectML session on a caller's device and queue, with a D3D12 input.** `DmlInferenceSession`
  lets ORT create its device today. Building it on the decoder's device is what makes the binding
  valid.
- **The shader as the device side of `ImageToTensor`** (#291). The spike's kernel is a fixed case:
  stretch, nearest, `[0, 1]`, NCHW, RGB. `ImageToTensorOptions` already describes the general one.
  The chroma filter and the colour matrix become options, the matrix at least until #388 gives
  frames their colour metadata.
- **Frame lifetime.** The frame has to stay alive until the GPU work that reads it completes. On
  D3D12VA the pool grows (#415), so the limit is VRAM (#416), not pool exhaustion.

## Reproducing

```bash
dotnet run --project spikes/D3D12DmlProbe -c Release -- tests/corpus/files/test-1080p-h264-aac.mp4 --frames 90
```

It needs a GPU with D3D12 video decode, the yolov8n model in `%LOCALAPPDATA%\FrameFlow.Yolo\models`,
and a Release build. A Debug build of the referenced FrameFlow projects inflates the CPU stages.
