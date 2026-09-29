// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference.Dml.Interop;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Inference.Dml;

/// <summary>
/// The snapshots a <see cref="DmlInferenceSession"/> takes around its open: one before, logged at
/// debug level, and one after, logged with the change at information level. A read that fails is
/// logged and leaves the open alone.
/// </summary>
internal static partial class GpuMemoryLog
{
    private const double BytesPerMiB = 1024 * 1024;

    /// <summary>
    /// Reads the adapter a session is about to open on, and adds it to the
    /// <see cref="GpuMemoryMetrics"/> gauges. Null when it cannot be read.
    /// </summary>
    /// <param name="d3d12Device">The device the session runs on, or zero for the default adapter's.</param>
    /// <param name="logger">Where the snapshot goes.</param>
    public static GpuMemorySnapshot? Before(nint d3d12Device, ILogger? logger)
    {
        GpuMemorySnapshot before;
        try
        {
            before = d3d12Device == 0
                ? GpuMemory.ReadDefaultAdapter()
                : GpuMemory.ReadAdapter(D3D12Native.AdapterLuid(d3d12Device));
        }
        catch (Exception ex)
        {
            if (logger is not null)
                LogUnreadable(logger, ex);
            return null;
        }

        GpuMemoryMetrics.Watch(before.AdapterLuid);
        if (logger is not null)
        {
            LogOpening(
                logger,
                before.AdapterLuid,
                MiB(before.Local.UsageBytes),
                MiB(before.Local.BudgetBytes),
                MiB(before.NonLocal.UsageBytes),
                MiB(before.NonLocal.BudgetBytes));
        }

        return before;
    }

    /// <summary>Reads the adapter again once the session has opened, and logs the change.</summary>
    public static void After(GpuMemorySnapshot? before, ILogger? logger)
    {
        if (before is not { } earlier || logger is null || !logger.IsEnabled(LogLevel.Information))
            return;

        GpuMemorySnapshot after;
        try
        {
            after = GpuMemory.ReadAdapter(earlier.AdapterLuid);
        }
        catch (Exception ex)
        {
            LogUnreadable(logger, ex);
            return;
        }

        var change = GpuMemoryChange.Between(earlier, after);
        LogOpened(
            logger,
            after.AdapterLuid,
            MiB(change.LocalBytes),
            MiB(change.NonLocalBytes),
            MiB(after.Local.UsageBytes),
            MiB(after.Local.BudgetBytes),
            MiB(after.NonLocal.UsageBytes),
            MiB(after.NonLocal.BudgetBytes));
    }

    private static long MiB(long bytes) => (long)Math.Round(bytes / BytesPerMiB);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Opening a DirectML session on adapter {AdapterLuid}. This process uses {LocalUsageMiB} MiB of its {LocalBudgetMiB} MiB local budget and {NonLocalUsageMiB} MiB of {NonLocalBudgetMiB} MiB non-local.")]
    private static partial void LogOpening(
        ILogger logger, ulong adapterLuid, long localUsageMiB, long localBudgetMiB, long nonLocalUsageMiB, long nonLocalBudgetMiB);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Opened a DirectML session on adapter {AdapterLuid}. This process's usage moved by {LocalChangeMiB:+0;-0;0} MiB local and {NonLocalChangeMiB:+0;-0;0} MiB non-local; it uses {LocalUsageMiB} MiB of its {LocalBudgetMiB} MiB local budget and {NonLocalUsageMiB} MiB of {NonLocalBudgetMiB} MiB non-local.")]
    private static partial void LogOpened(
        ILogger logger,
        ulong adapterLuid,
        long localChangeMiB,
        long nonLocalChangeMiB,
        long localUsageMiB,
        long localBudgetMiB,
        long nonLocalUsageMiB,
        long nonLocalBudgetMiB);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Could not read the GPU's memory budget and usage around a DirectML session's open.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);
}
