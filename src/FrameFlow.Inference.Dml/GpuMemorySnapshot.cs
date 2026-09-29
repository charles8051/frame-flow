// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Dml;

/// <summary>
/// This process's memory budget and usage on one GPU adapter at one moment. <see cref="GpuMemory"/>
/// reads one; <see cref="GpuMemoryChange.Between"/> compares two.
/// </summary>
/// <param name="AdapterLuid">The adapter's LUID, its high part above its low part.</param>
/// <param name="Local">
/// The local segment group: the adapter's own memory on a discrete GPU, or the memory the driver sets
/// aside for it on an integrated one. Model weights and intermediate tensors live here.
/// </param>
/// <param name="NonLocal">
/// The non-local segment group: system memory the adapter reaches over the bus, such as the upload
/// buffers weights are copied through.
/// </param>
public readonly record struct GpuMemorySnapshot(ulong AdapterLuid, GpuMemorySegment Local, GpuMemorySegment NonLocal);
