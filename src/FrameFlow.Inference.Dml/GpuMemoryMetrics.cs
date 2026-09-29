// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace FrameFlow.Inference.Dml;

/// <summary>
/// The <c>FrameFlow.Inference.Dml</c> meter: this process's usage and budget on each adapter a DirectML
/// session has opened on, read from DXGI when the gauges are collected.
/// </summary>
internal static class GpuMemoryMetrics
{
    public const string MeterName = "FrameFlow.Inference.Dml";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // Adapters are few and a process rarely changes which it uses, so the set is never pruned.
    private static readonly ConcurrentDictionary<ulong, string> Adapters = new();

    static GpuMemoryMetrics()
    {
        Meter.CreateObservableGauge(
            "frameflow.inference.dml.gpu_memory_usage",
            () => Observe(static segment => segment.UsageBytes),
            unit: "By",
            description: "This process's usage of each adapter a DirectML session opened on, by segment group. Local usage above the local budget means allocations were moved out and work touching them slows.");
        Meter.CreateObservableGauge(
            "frameflow.inference.dml.gpu_memory_budget",
            () => Observe(static segment => segment.BudgetBytes),
            unit: "By",
            description: "This process's memory budget on each adapter a DirectML session opened on, by segment group, as the operating system sets it.");
    }

    /// <summary>Adds <paramref name="adapterLuid"/> to the adapters the gauges report.</summary>
    public static void Watch(ulong adapterLuid) =>
        Adapters.TryAdd(adapterLuid, adapterLuid.ToString(CultureInfo.InvariantCulture));

    private static List<Measurement<long>> Observe(Func<GpuMemorySegment, long> value)
    {
        var measurements = new List<Measurement<long>>(2 * Adapters.Count);
        foreach (var (luid, tag) in Adapters)
        {
            GpuMemorySnapshot snapshot;
            try
            {
                snapshot = GpuMemory.ReadAdapter(luid);
            }
            catch (Exception)
            {
                // An adapter that cannot be read, such as one removed, reports nothing.
                continue;
            }

            var adapter = KeyValuePair.Create<string, object?>("adapter", tag);
            measurements.Add(new(value(snapshot.Local), adapter, KeyValuePair.Create<string, object?>("segment", "local")));
            measurements.Add(new(value(snapshot.NonLocal), adapter, KeyValuePair.Create<string, object?>("segment", "non_local")));
        }

        return measurements;
    }
}
