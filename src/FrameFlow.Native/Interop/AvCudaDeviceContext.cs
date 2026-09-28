// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// FFmpeg's <c>AVCUDADeviceContext</c>: what <c>AVHWDeviceContext.hwctx</c> points to for a CUDA
/// device. FFmpeg.AutoGen does not generate the CUDA hwcontext types, so this mirrors
/// <c>libavutil/hwcontext_cuda.h</c> from FFmpeg 9.0.
/// </summary>
/// <remarks>x64 layout: <c>cuda_ctx</c> at 0, <c>stream</c> at 8, <c>internal</c> at 16.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct AVCUDADeviceContext
{
    /// <summary><c>CUcontext</c>: the context the device's memory belongs to.</summary>
    public nint cuda_ctx;

    /// <summary><c>CUstream</c>: the stream FFmpeg works on, or zero for the default stream.</summary>
    public nint stream;

    /// <summary>FFmpeg's private state.</summary>
    public nint @internal;
}
