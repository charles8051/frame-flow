// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Dml;

/// <summary>
/// How much this process's usage of one adapter's memory moved between two snapshots, in bytes. A
/// negative value is memory released.
/// </summary>
/// <param name="LocalBytes">The change in the local segment group's usage.</param>
/// <param name="NonLocalBytes">The change in the non-local segment group's usage.</param>
public readonly record struct GpuMemoryChange(long LocalBytes, long NonLocalBytes)
{
    /// <summary>The usage in <paramref name="after"/> less the usage in <paramref name="before"/>.</summary>
    /// <exception cref="ArgumentException">The snapshots are of different adapters.</exception>
    public static GpuMemoryChange Between(GpuMemorySnapshot before, GpuMemorySnapshot after)
    {
        if (before.AdapterLuid != after.AdapterLuid)
        {
            throw new ArgumentException(
                $"The snapshots are of different adapters ({before.AdapterLuid} and {after.AdapterLuid}).",
                nameof(after));
        }

        return new GpuMemoryChange(
            after.Local.UsageBytes - before.Local.UsageBytes,
            after.NonLocal.UsageBytes - before.NonLocal.UsageBytes);
    }
}
