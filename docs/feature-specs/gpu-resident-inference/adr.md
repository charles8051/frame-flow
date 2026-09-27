# GPU-resident inference: decisions

The decisions behind [the spec](spec.md). Append-only: a decision that changes is superseded by a
new one, not rewritten. A decision a second feature starts citing graduates to a numbered ADR.

## Decision 1: a GPU-resident frame is preprocessed by a D3D12 compute shader

**Date:** 2026-09-27
**Status:** Accepted
**Issue:** #291

### Context

#291 asked where resize, normalize and layout run for an inference model. It was opened on the
claim that preprocessing was the largest stage, at 5.3 ms. That figure was a Debug build. In
Release the CPU preprocess is 0.7 ms since `ImageToTensor` vectorised it (#363), and the download
and NV12 conversion are the cost.

The question still needs an answer, for a different reason: a GPU-resident frame has no CPU pixels
to preprocess, and downloading them is the cost this feature removes.

The two spikes measured the answer on an RTX 3080 Ti:

- **#420, DirectML.** A compute shader reads the D3D12VA NV12 texture after a GPU wait on the
  frame's fence and writes the model input into a D3D12 buffer in 0.18 ms. DirectML binds the
  buffer as the tensor.
- **#421, TensorRT-RTX.** The same buffer, imported into CUDA, is TensorRT-RTX's input.

Both bindings give outputs identical to the CPU path's for identical input.

### Decision

- **One kernel on the device.** A GPU-resident frame is preprocessed by one D3D12 compute shader,
  the device side of `ImageToTensor`. It is configured by the same `ImageToTensorOptions`: crop,
  fit, sampling, normalization, layout and channel order. Its queue waits on the frame's fence
  (`TryGetD3D12Texture`, #424) before it reads.
- **One binding per provider.** The buffer it writes is handed to the execution provider per
  backend: DirectML reads it directly, and TensorRT-RTX reads it through a CUDA import. Everything
  before the binding is shared.
- **The CPU path is unchanged.** A CPU frame keeps the CPU `ImageToTensor`.

### Why D3D12, not a kernel per backend

HLSL compiles to vendor-neutral bytecode, which each vendor's D3D12 driver compiles for its own
hardware. So one kernel serves NVIDIA, AMD and Intel. A CUDA kernel serves NVIDIA only and would
need a second implementation for the others. The per-provider part is the binding, which is a few
calls, not a kernel.

### Rejected for now: folding preprocessing into the model

ORT would run the transform on the provider, with no kernel of ours. But each model would need
editing, the providers take tensors rather than textures, so the NV12 texture would still need
copying into a buffer, and it is untested. `ImageToTensorOptions` already describes the transform
once for every model.

### Consequences

- **Colour becomes an option.** The colour matrix and the chroma filter are `ImageToTensorOptions`
  choices on the device. The spike used BT.601 limited range and nearest chroma, and matched the
  swscale CPU path to p99 12.6/255, with the differences on colour edges. Frames that carry their
  colour metadata (#388) would supply the matrix.
- **Frame lifetime.** The frame stays alive until the GPU work that reads it completes.
- **Only Windows.** This path covers D3D12 hardware decode on Windows. Other platforms need their
  own device side, starting with a CUDA path on Linux, where there is no D3D12.
- **#298 decides which bindings are built.** It does not change the kernel.

## Decision 2: backend handles are `TryGet` accessors; the CUDA pointer waits for a CUDA decode path

**Date:** 2026-09-27
**Status:** Accepted
**Issue:** #289

### Context

#289 asked two things. Should `GpuVideoFrame` expose backend handles as per-backend
sub-interfaces or as `TryGet` methods? And it asked for the CUDA device pointer of an NVDEC frame,
to feed `CudaInferenceSession`'s device-pointer binding.

### Decision

- **`TryGet` methods, one per backend.** `TryGetD3D11Texture` and `TryGetD3D12Texture` (#424)
  set the shape. A caller can hold a frame without knowing its backend, and a `false` return covers
  the frame another backend decoded.
- **The NVDEC CUDA pointer is deferred.** TensorRT-RTX already gets CUDA memory from a D3D12VA
  frame by importing the preprocessed buffer (#421), with no NVDEC frame involved. The accessor
  matters once a path decodes with CUDA directly, which is the Linux path in decision 1's
  consequences.

### Consequences

#289 narrows to the CUDA pointer and drops in priority until a CUDA decode path is planned.

## Decision 3: the device-side stage is its own Windows-only package

**Date:** 2026-09-27
**Status:** Accepted
**Issue:** #425

### Context

Decision 1's shader needs Direct3D 12. `FrameFlow.Inference.Abstractions` has no native dependency
and every execution-provider package references it. The shader's output is bound by the DirectML
package and, through a CUDA import, by the Windows ML package (#423), so it belongs to neither.

### Decision

`FrameFlow.Inference.D3D12` holds `D3D12ImageToTensor`. It is `net10.0` with Vortice, and so
Windows-only at runtime, as `FrameFlow.Avalonia.Windows` is. It compiles its shader at runtime with
the in-box D3DCompiler, as that package does. It reads `ImageToTensorPlan` from
`FrameFlow.Inference.Abstractions` through `InternalsVisibleTo`, so both stages compute the same
plan and return the same `TensorTransform`.

### Consequences

- **The API takes native pointers.** A device and queue come in as `ID3D12Device*` and
  `ID3D12CommandQueue*`, and the tensor and completion fence go out the same way, matching
  `TryGetD3D12Texture`. A consumer is not tied to Vortice's version.
- **Its tensor buffer is on a shared heap.** A CUDA consumer can import it with no copy.
- **The stage holds device references.** With a TensorRT-RTX session loaded, it has to be
  disposed before the decoder's last frame is freed (#422).

