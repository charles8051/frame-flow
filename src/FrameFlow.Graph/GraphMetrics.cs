// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;

namespace FrameFlow.Graph;

/// <summary>
/// The <c>FrameFlow.Graph</c> meter and the instruments on it, shared so the assembly publishes one
/// meter.
/// </summary>
internal static class GraphMetrics
{
    public static readonly Meter Meter = new("FrameFlow.Graph", "1.0.0");

    /// <summary>
    /// Inputs a node dropped under <see cref="FailureResponse.Discard"/>, tagged <c>node</c> with the
    /// node's id (#501).
    /// </summary>
    public static readonly Counter<long> Discards = Meter.CreateCounter<long>(
        "frameflow.graph.discards",
        unit: "{item}",
        description: "Inputs a node dropped under FailureResponse.Discard after its body threw, by node id."
    );
}
