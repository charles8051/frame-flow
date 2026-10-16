// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding;

/// <summary>
/// Asks a hardware decode device whether it was removed (#759). The shell half of device-loss
/// detection: it reads the native device, and <c>FrameFlow.Media.Core.DeviceLoss</c> decides what
/// the answer means.
/// </summary>
internal static class DeviceRemovedReason
{
    // Vtable slots of GetDeviceRemovedReason. ID3D11Device: IUnknown's three methods, then 36
    // methods from CreateBuffer to GetCreationFlags. ID3D12Device: IUnknown's three, ID3D12Object's
    // four, then 30 from GetNodeCount to CreateFence.
    private const int D3D11Slot = 39;
    private const int D3D12Slot = 37;

    /// <summary>
    /// What <c>GetDeviceRemovedReason</c> returns for the device behind
    /// <paramref name="deviceCtxRef"/>, an <c>AVBufferRef*</c> holding an
    /// <c>AVHWDeviceContext</c> of <paramref name="backend"/>.
    /// </summary>
    /// <returns>
    /// <c>S_OK</c> (0) for a live device, a backend that cannot report removal, or a null
    /// reference. Only D3D11VA and D3D12VA devices report it.
    /// </returns>
    public static unsafe int Query(nint deviceCtxRef, HardwareDecodeBackendKind backend)
    {
        if (deviceCtxRef == nint.Zero || !OperatingSystem.IsWindows())
            return 0;

        var context = (AVHWDeviceContext*)((AVBufferRef*)deviceCtxRef)->data;
        if (context is null || context->hwctx is null)
            return 0;

        return backend switch
        {
            HardwareDecodeBackendKind.D3D11Va => Call((nint)((AVD3D11VADeviceContext*)context->hwctx)->device, D3D11Slot),
            HardwareDecodeBackendKind.D3D12Va => Call((nint)((AVD3D12VADeviceContext*)context->hwctx)->device, D3D12Slot),
            _ => 0,
        };
    }

    private static unsafe int Call(nint device, int slot)
    {
        if (device == nint.Zero)
            return 0;
        var vtable = *(nint**)device;
        return ((delegate* unmanaged[Stdcall]<nint, int>)vtable[slot])(device);
    }
}
