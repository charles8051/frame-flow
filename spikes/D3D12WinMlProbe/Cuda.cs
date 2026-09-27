using System.Runtime.InteropServices;

namespace D3D12WinMlProbe;

/// <summary>
/// The CUDA driver API calls that import a D3D12 buffer as CUDA memory. The driver API ships
/// with the NVIDIA driver as <c>nvcuda.dll</c>; nothing else is needed.
/// </summary>
internal static unsafe class Cuda
{
    private const string Library = "nvcuda.dll";

    /// <summary><c>CU_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE</c>.</summary>
    private const int HandleTypeD3D12Resource = 5;

    /// <summary><c>CUDA_EXTERNAL_MEMORY_DEDICATED</c>: required for a committed resource.</summary>
    private const uint Dedicated = 1;

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuInit(uint flags);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuDeviceGetCount(out int count);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuDeviceGet(out int device, int ordinal);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuDeviceGetLuid(long* luid, uint* nodeMask, int device);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuDevicePrimaryCtxRetain(out nint context, int device);

    [DllImport(Library, ExactSpelling = true, EntryPoint = "cuDevicePrimaryCtxRelease_v2")]
    private static extern int cuDevicePrimaryCtxRelease(int device);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuCtxSetCurrent(nint context);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuImportExternalMemory(out nint externalMemory, ExternalMemoryHandleDesc* desc);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuExternalMemoryGetMappedBuffer(out ulong devicePointer, nint externalMemory, ExternalMemoryBufferDesc* desc);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int cuDestroyExternalMemory(nint externalMemory);

    [DllImport(Library, ExactSpelling = true, EntryPoint = "cuMemFree_v2")]
    private static extern int cuMemFree(ulong devicePointer);

    [DllImport(Library, ExactSpelling = true, EntryPoint = "cuMemcpyDtoH_v2")]
    private static extern int cuMemcpyDtoH(void* destination, ulong source, nuint bytes);

    /// <summary>
    /// The CUDA device whose LUID is <paramref name="adapterLuid"/>, the D3D12 adapter's, with its
    /// primary context current. The primary context is the one the CUDA runtime, and so ORT's
    /// CUDA-based providers, use.
    /// </summary>
    public static int OpenDevice(long adapterLuid)
    {
        Check(cuInit(0), "cuInit");
        Check(cuDeviceGetCount(out int count), "cuDeviceGetCount");
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            Check(cuDeviceGet(out int device, ordinal), "cuDeviceGet");
            long luid;
            uint nodeMask;
            Check(cuDeviceGetLuid(&luid, &nodeMask, device), "cuDeviceGetLuid");
            if (luid != adapterLuid)
                continue;
            Check(cuDevicePrimaryCtxRetain(out nint context, device), "cuDevicePrimaryCtxRetain");
            Check(cuCtxSetCurrent(context), "cuCtxSetCurrent");
            return device;
        }

        throw new InvalidOperationException("No CUDA device has the D3D12 adapter's LUID.");
    }

    public static void CloseDevice(int device) => cuDevicePrimaryCtxRelease(device);

    /// <summary>
    /// Imports a committed D3D12 buffer, created on a shared heap, through its NT handle, and maps
    /// all of it. <paramref name="allocationSize"/> is <c>GetResourceAllocationInfo</c>'s size.
    /// </summary>
    public static (nint ExternalMemory, ulong DevicePointer) Import(nint sharedHandle, ulong allocationSize, ulong bytes)
    {
        var handleDesc = new ExternalMemoryHandleDesc
        {
            Type = HandleTypeD3D12Resource,
            Handle = sharedHandle,
            Size = allocationSize,
            Flags = Dedicated,
        };
        Check(cuImportExternalMemory(out nint externalMemory, &handleDesc), "cuImportExternalMemory");
        var bufferDesc = new ExternalMemoryBufferDesc { Offset = 0, Size = bytes };
        Check(cuExternalMemoryGetMappedBuffer(out ulong pointer, externalMemory, &bufferDesc), "cuExternalMemoryGetMappedBuffer");
        return (externalMemory, pointer);
    }

    public static void Release(nint externalMemory, ulong devicePointer)
    {
        cuMemFree(devicePointer);
        cuDestroyExternalMemory(externalMemory);
    }

    /// <summary>Copies CUDA memory to the CPU, to check the import only.</summary>
    public static float[] ReadBack(ulong devicePointer, int count)
    {
        var values = new float[count];
        fixed (float* destination = values)
            Check(cuMemcpyDtoH(destination, devicePointer, (nuint)(count * sizeof(float))), "cuMemcpyDtoH");
        return values;
    }

    private static void Check(int result, string call)
    {
        if (result != 0)
            throw new InvalidOperationException($"{call} failed: CUresult {result}.");
    }

    /// <summary><c>CUDA_EXTERNAL_MEMORY_HANDLE_DESC</c>, x64: the handle union at 8, size at 24, flags at 32.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct ExternalMemoryHandleDesc
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public nint Handle;
        [FieldOffset(16)] public nint Name;
        [FieldOffset(24)] public ulong Size;
        [FieldOffset(32)] public uint Flags;
    }

    /// <summary><c>CUDA_EXTERNAL_MEMORY_BUFFER_DESC</c>, x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    private struct ExternalMemoryBufferDesc
    {
        [FieldOffset(0)] public ulong Offset;
        [FieldOffset(8)] public ulong Size;
        [FieldOffset(16)] public uint Flags;
    }
}
