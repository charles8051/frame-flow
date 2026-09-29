// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Dml;

/// <summary>
/// This process's budget and usage in one segment group of a GPU adapter's memory, in bytes, as
/// <c>IDXGIAdapter3::QueryVideoMemoryInfo</c> reports them.
/// </summary>
/// <param name="BudgetBytes">
/// How much the operating system lets this process keep in the segment group before it moves the
/// process's allocations out. It moves as other processes use the adapter.
/// </param>
/// <param name="UsageBytes">
/// How much this process has allocated there now, across every device it has on the adapter. It can
/// pass the budget, and work that touches the allocations moved out then slows.
/// </param>
public readonly record struct GpuMemorySegment(long BudgetBytes, long UsageBytes);
