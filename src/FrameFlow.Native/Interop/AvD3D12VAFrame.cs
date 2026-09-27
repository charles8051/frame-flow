// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// FFmpeg's <c>AVD3D12VAFrame</c>, what <c>AVFrame.data[0]</c> points to for a D3D12VA hardware
/// frame. FFmpeg.AutoGen does not generate the D3D12 hwcontext types, so this mirrors
/// <c>libavutil/hwcontext_d3d12va.h</c> from FFmpeg 9.0.
/// </summary>
/// <remarks>
/// x64 layout: <c>texture</c> at 0, <c>subresource_index</c> at 8, <c>sync_ctx</c> at 16
/// (<c>fence</c> 16, <c>event</c> 24, <c>fence_value</c> 32), <c>flags</c> at 40.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct AVD3D12VAFrame
{
    /// <summary><c>ID3D12Resource*</c>: the texture the frame was decoded into.</summary>
    public nint texture;

    /// <summary>The subresource within <see cref="texture"/>; 0 unless the pool is a texture array.</summary>
    public int subresource_index;

    /// <summary>The fence the decoder signals when it has written the frame.</summary>
    public AVD3D12VASyncContext sync_ctx;

    /// <summary><c>AVD3D12VAFrameFlags</c>.</summary>
    public int flags;
}

/// <summary>FFmpeg's <c>AVD3D12VASyncContext</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVD3D12VASyncContext
{
    /// <summary><c>ID3D12Fence*</c>.</summary>
    public nint fence;

    /// <summary>A Win32 event FFmpeg uses to wait on the fence from the CPU.</summary>
    public nint @event;

    /// <summary>The value <see cref="fence"/> reaches once the frame is written.</summary>
    public ulong fence_value;
}

/// <summary>
/// FFmpeg's <c>AVD3D12VADeviceContext</c>: what <c>AVHWDeviceContext.hwctx</c> points to for a
/// D3D12VA device. Mirrors <c>libavutil/hwcontext_d3d12va.h</c> from FFmpeg 9.0. A caller that
/// lends FFmpeg its own device sets <see cref="device"/> before <c>av_hwdevice_ctx_init</c>, which
/// fills in the rest; FFmpeg releases <see cref="device"/> when the context is freed.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVD3D12VADeviceContext
{
    /// <summary><c>ID3D12Device*</c>.</summary>
    public nint device;

    /// <summary><c>ID3D12VideoDevice*</c>, queried from <see cref="device"/> by init when null.</summary>
    public nint video_device;

    /// <summary>The lock FFmpeg takes around the device; a default mutex when null at init.</summary>
    public nint @lock;

    /// <summary>The matching unlock.</summary>
    public nint unlock;

    /// <summary>The argument both are called with.</summary>
    public nint lock_ctx;
}
