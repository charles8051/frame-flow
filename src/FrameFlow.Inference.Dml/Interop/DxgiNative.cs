// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Dml.Interop;

/// <summary>
/// The DXGI calls that read an adapter's memory budget and usage, through the vtables directly, as
/// <see cref="D3D12Native"/> does.
/// </summary>
internal static unsafe class DxgiNative
{
    // IDXGIFactory1::EnumAdapters1, after IUnknown (3), IDXGIObject (4) and IDXGIFactory (5).
    private const int EnumAdapters1Slot = 12;

    // IDXGIFactory4::EnumAdapterByLuid: IDXGIFactory1 (2 methods from slot 12), IDXGIFactory2 (11) and
    // IDXGIFactory3 (1) come before it.
    private const int EnumAdapterByLuidSlot = 26;

    // IDXGIAdapter1::GetDesc1, after IUnknown (3), IDXGIObject (4) and IDXGIAdapter (3).
    private const int GetDesc1Slot = 10;

    // IDXGIAdapter3::QueryVideoMemoryInfo, after IDXGIAdapter2::GetDesc2 and two content-protection methods.
    private const int QueryVideoMemoryInfoSlot = 14;

    private const uint SegmentGroupLocal = 0;
    private const uint SegmentGroupNonLocal = 1;

    private static readonly Guid IidIDXGIFactory4 = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
    private static readonly Guid IidIDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    private static readonly Guid IidIDXGIAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory2(uint flags, in Guid riid, out nint factory);

    /// <summary>The first adapter DXGI enumerates, which is the one DirectML's default device uses.</summary>
    public static GpuMemorySnapshot ReadDefaultAdapter()
    {
        nint factory = CreateFactory();
        try
        {
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)(*(nint**)factory)[EnumAdapters1Slot];
            nint adapter1 = 0;
            Check(enumAdapters1(factory, 0, &adapter1), "IDXGIFactory1::EnumAdapters1");
            try
            {
                Check(Marshal.QueryInterface(adapter1, in IidIDXGIAdapter3, out nint adapter3), "QueryInterface(IDXGIAdapter3)");
                try
                {
                    return Read(adapter3, LuidOf(adapter3));
                }
                finally
                {
                    Marshal.Release(adapter3);
                }
            }
            finally
            {
                Marshal.Release(adapter1);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>The adapter whose LUID is <paramref name="adapterLuid"/>.</summary>
    public static GpuMemorySnapshot ReadAdapter(ulong adapterLuid)
    {
        nint factory = CreateFactory();
        try
        {
            var enumByLuid = (delegate* unmanaged<nint, Luid, Guid*, nint*, int>)(*(nint**)factory)[EnumAdapterByLuidSlot];
            nint adapter3 = 0;
            Guid iid = IidIDXGIAdapter3;
            Check(enumByLuid(factory, Luid.From(adapterLuid), &iid, &adapter3), "IDXGIFactory4::EnumAdapterByLuid");
            try
            {
                return Read(adapter3, adapterLuid);
            }
            finally
            {
                Marshal.Release(adapter3);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static nint CreateFactory()
    {
        Check(CreateDXGIFactory2(0, in IidIDXGIFactory4, out nint factory), "CreateDXGIFactory2");
        return factory;
    }

    private static ulong LuidOf(nint adapter1)
    {
        var getDesc1 = (delegate* unmanaged<nint, AdapterDesc1*, int>)(*(nint**)adapter1)[GetDesc1Slot];
        AdapterDesc1 desc;
        Check(getDesc1(adapter1, &desc), "IDXGIAdapter1::GetDesc1");
        return desc.AdapterLuid.Value;
    }

    private static GpuMemorySnapshot Read(nint adapter3, ulong adapterLuid)
    {
        var query = (delegate* unmanaged<nint, uint, uint, VideoMemoryInfo*, int>)(*(nint**)adapter3)[QueryVideoMemoryInfoSlot];
        VideoMemoryInfo local, nonLocal;
        Check(query(adapter3, 0, SegmentGroupLocal, &local), "IDXGIAdapter3::QueryVideoMemoryInfo(local)");
        Check(query(adapter3, 0, SegmentGroupNonLocal, &nonLocal), "IDXGIAdapter3::QueryVideoMemoryInfo(non-local)");
        return new GpuMemorySnapshot(adapterLuid, local.ToSegment(), nonLocal.ToSegment());
    }

    private static void Check(int hr, string call)
    {
        if (hr < 0)
            throw new COMException($"{call} failed with 0x{hr:X8}.", hr);
    }

    /// <summary>A Windows <c>LUID</c>, passed by value as DXGI takes it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Luid
    {
        private readonly uint _lowPart;
        private readonly int _highPart;

        private Luid(uint lowPart, int highPart)
        {
            _lowPart = lowPart;
            _highPart = highPart;
        }

        /// <summary>The high part above the low part.</summary>
        public ulong Value => ((ulong)(uint)_highPart << 32) | _lowPart;

        public static Luid From(ulong value) => new((uint)value, (int)(uint)(value >> 32));
    }

    /// <summary><c>DXGI_ADAPTER_DESC1</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    /// <summary><c>DXGI_QUERY_VIDEO_MEMORY_INFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;

        public readonly GpuMemorySegment ToSegment() => new(ToBytes(Budget), ToBytes(CurrentUsage));

        private static long ToBytes(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
    }
}
