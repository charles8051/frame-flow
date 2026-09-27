// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Dml.Interop;

/// <summary>
/// The few COM calls the device-bound session makes, through the vtables directly: this package
/// does not take a Direct3D binding for three methods.
/// </summary>
internal static unsafe class D3D12Native
{
    // ID3D12DeviceChild::GetDevice, after IUnknown (3) and ID3D12Object (4).
    private const int GetDeviceSlot = 7;

    // ID3D12CommandQueue::Wait, after ID3D12DeviceChild and seven queue methods before it.
    private const int QueueWaitSlot = 15;

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

    /// <summary>Makes <paramref name="queue"/> wait on the GPU until <paramref name="fence"/> reaches <paramref name="value"/>.</summary>
    public static void QueueWait(nint queue, nint fence, ulong value)
    {
        var wait = (delegate* unmanaged<nint, nint, ulong, int>)(*(nint**)queue)[QueueWaitSlot];
        Marshal.ThrowExceptionForHR(wait(queue, fence, value));
    }
}
