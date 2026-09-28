// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>
/// Where a CUDA-decoded frame's samples are in device memory (#289), from
/// <see cref="GpuVideoFrame.TryGetCudaPlanes"/>. Two planes, as NV12 and P010 lay them out: luma,
/// then interleaved chroma at half the height.
/// </summary>
/// <param name="Luma">The luma plane's <c>CUdeviceptr</c>.</param>
/// <param name="LumaPitch">Bytes from one luma row to the next.</param>
/// <param name="Chroma">The interleaved chroma plane's <c>CUdeviceptr</c>.</param>
/// <param name="ChromaPitch">Bytes from one chroma row to the next.</param>
/// <param name="Context">The <c>CUcontext</c> the memory belongs to. Make it current before using the pointers.</param>
/// <param name="Stream">
/// The <c>CUstream</c> the decoder copied the frame into this memory on, or zero for the default
/// stream. The copy is asynchronous: read on this stream, or synchronize with it first.
/// </param>
public readonly record struct CudaFramePlanes(
    nint Luma,
    int LumaPitch,
    nint Chroma,
    int ChromaPitch,
    nint Context,
    nint Stream);
