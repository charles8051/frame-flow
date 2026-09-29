// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Dml.Interop;

/// <summary>
/// The few COM calls the device-bound session makes, through the vtables directly: this package
/// does not take a Direct3D binding for a handful of methods.
/// </summary>
internal static unsafe class D3D12Native
{
    // ID3D12DeviceChild::GetDevice, after IUnknown (3) and ID3D12Object (4).
    private const int GetDeviceSlot = 7;

    // ID3D12CommandQueue::Wait, after ID3D12DeviceChild and seven queue methods before it.
    private const int QueueWaitSlot = 15;

    // ID3D12Device::GetAdapterLuid, the 37th ID3D12Device method after ID3D12Object's seven slots.
    private const int GetAdapterLuidSlot = 43;

    private const uint DmlFeatureLevel5_0 = 0x5000;

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid IidID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    private static readonly Guid IidIDmlDevice = new("6dbd6437-96fd-423f-a98c-ae5e7c2a573f");

    [DllImport("DirectML.dll", ExactSpelling = true)]
    private static extern int DMLCreateDevice1(
        nint d3d12Device, uint flags, uint minimumFeatureLevel, in Guid riid, out nint device);

    /// <summary>A new <c>IDMLDevice*</c> on <paramref name="d3d12Device"/>. The caller releases it.</summary>
    public static nint CreateDmlDevice(nint d3d12Device)
    {
        Marshal.ThrowExceptionForHR(DMLCreateDevice1(d3d12Device, 0, DmlFeatureLevel5_0, IidIDmlDevice, out nint device));
        return device;
    }

    /// <summary>
    /// The object's <c>IUnknown</c> pointer, which is the same for every interface of one object.
    /// Valid while the caller holds any reference to the object.
    /// </summary>
    public static nint Identity(nint unknown)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IidIUnknown, out nint identity));
        Marshal.Release(identity);
        return identity;
    }

    /// <summary>The identity of the device <paramref name="queue"/> (an <c>ID3D12CommandQueue*</c>) was created on.</summary>
    public static nint DeviceIdentityOf(nint queue)
    {
        var getDevice = (delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)queue)[GetDeviceSlot];
        nint device = 0;
        Guid iid = IidID3D12Device;
        Marshal.ThrowExceptionForHR(getDevice(queue, &iid, &device));
        try
        {
            return Identity(device);
        }
        finally
        {
            Marshal.Release(device);
        }
    }

    /// <summary>
    /// The LUID of the adapter <paramref name="device"/> (any interface of a D3D12 device) was created
    /// on, high part above low part.
    /// </summary>
    public static ulong AdapterLuid(nint device)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IidID3D12Device, out nint d3d12Device));
        try
        {
            // A method returning a struct takes a hidden pointer for it after `this` and returns
            // that pointer, in the Windows x64 ABI for C++ member functions, whatever the struct's
            // size. An 8-byte struct comes back in RAX only from a free function. The SDK's C
            // binding says so: LUID *(STDMETHODCALLTYPE *GetAdapterLuid)(ID3D12Device *This,
            // LUID *RetVal). GpuMemoryTests compares the result with Vortice's on hardware.
            var getAdapterLuid =
                (delegate* unmanaged<nint, DxgiNative.Luid*, DxgiNative.Luid*>)(*(nint**)d3d12Device)[GetAdapterLuidSlot];
            DxgiNative.Luid luid;
            return getAdapterLuid(d3d12Device, &luid)->Value;
        }
        finally
        {
            Marshal.Release(d3d12Device);
        }
    }

    /// <summary>Makes <paramref name="queue"/> wait on the GPU until <paramref name="fence"/> reaches <paramref name="value"/>.</summary>
    public static void QueueWait(nint queue, nint fence, ulong value)
    {
        var wait = (delegate* unmanaged<nint, nint, ulong, int>)(*(nint**)queue)[QueueWaitSlot];
        Marshal.ThrowExceptionForHR(wait(queue, fence, value));
    }
}
