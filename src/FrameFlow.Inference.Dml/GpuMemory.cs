// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Inference.Dml.Interop;

namespace FrameFlow.Inference.Dml;

/// <summary>
/// Reads this process's memory budget and usage on a GPU adapter from DXGI
/// (<c>IDXGIAdapter3::QueryVideoMemoryInfo</c>).
/// </summary>
/// <remarks>
/// <para>
/// An allocation past the local budget can succeed. On a discrete GPU, DirectML sessions kept
/// allocating until local usage was 1.5 times the budget, more than the GPU has, with no error; the
/// runs whose tensors were allocated past the budget took 30 to 40 times as long. So running out shows
/// as <see cref="GpuMemorySnapshot.Local"/> usage above its budget and a slowdown, and a snapshot is
/// what shows it.
/// </para>
/// <para>
/// Every <see cref="DmlInferenceSession"/> given a logger logs a snapshot before it opens and another,
/// with the <see cref="GpuMemoryChange"/>, once it has. The change counts everything the process
/// allocated on the adapter in between, including another session opening on another thread, and the
/// first run can allocate more.
/// </para>
/// <para>
/// The <c>FrameFlow.Inference.Dml</c> meter publishes <c>frameflow.inference.dml.gpu_memory_usage</c>
/// and <c>frameflow.inference.dml.gpu_memory_budget</c>, in bytes, for each adapter a DirectML session
/// has opened on in this process, tagged with the adapter's LUID and the segment group
/// (<c>local</c> or <c>non_local</c>). Scrape them with <c>dotnet-counters</c>.
/// </para>
/// <para>
/// Every read creates a DXGI factory and asks the adapter afresh, so the numbers are current and a
/// removed adapter fails rather than reading stale. Each reads node 0 of the adapter.
/// </para>
/// </remarks>
public static class GpuMemory
{
    /// <summary>
    /// Reads the default adapter: the first one DXGI enumerates, which is the adapter a
    /// <see cref="DmlInferenceSession"/> from the constructors runs on.
    /// </summary>
    /// <exception cref="COMException">DXGI could not enumerate the adapter or report its memory.</exception>
    public static GpuMemorySnapshot ReadDefaultAdapter() => DxgiNative.ReadDefaultAdapter();

    /// <summary>Reads the adapter whose LUID is <paramref name="adapterLuid"/>.</summary>
    /// <param name="adapterLuid">The adapter's LUID, its high part above its low part.</param>
    /// <exception cref="COMException">No adapter has that LUID, or DXGI could not report its memory.</exception>
    public static GpuMemorySnapshot ReadAdapter(ulong adapterLuid) => DxgiNative.ReadAdapter(adapterLuid);

    /// <summary>
    /// The LUID of the adapter a D3D12 device was created on, such as the one a
    /// <c>HardwareDevice</c> decodes on or the one given to <see cref="DmlInferenceSession.OnDevice(string, nint, nint, Microsoft.Extensions.Logging.ILogger{DmlInferenceSession}?)"/>.
    /// </summary>
    /// <param name="d3d12Device">An <c>ID3D12Device*</c>, or any interface of the device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="d3d12Device"/> is zero.</exception>
    /// <exception cref="InvalidCastException"><paramref name="d3d12Device"/> is not a D3D12 device.</exception>
    public static ulong AdapterLuidOf(nint d3d12Device)
    {
        if (d3d12Device == 0)
            throw new ArgumentNullException(nameof(d3d12Device));
        return D3D12Native.AdapterLuid(d3d12Device);
    }
}
