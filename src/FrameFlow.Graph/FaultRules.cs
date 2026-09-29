// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// How the graph reads an exception that ends a node body or a pump, as total functions of
/// values the pump already holds.
/// </summary>
internal static class FaultRules
{
    /// <summary>
    /// Whether <paramref name="ex"/> is the run's cancellation rather than a fault: an
    /// <see cref="OperationCanceledException"/> while the run's token is cancelled (#489).
    /// </summary>
    /// <remarks>
    /// Any other <see cref="OperationCanceledException"/> is a body failing for its own reason, such
    /// as an <c>HttpClient</c> timeout, and takes the node's <see cref="FailureResponse"/>.
    /// </remarks>
    /// <param name="ex">The exception that ended the body or the pump.</param>
    /// <param name="runCancelled">Whether the run's token was cancelled when it was caught.</param>
    internal static bool IsCancellation(Exception ex, bool runCancelled) =>
        ex is OperationCanceledException && runCancelled;

    /// <summary>
    /// The faults of a run as <see cref="GraphFaultException"/> lists them: <paramref name="first"/>,
    /// then every other node's fault in node order (#499). Empty when <paramref name="first"/> is
    /// <see langword="null"/>, since no node recorded a fault.
    /// </summary>
    /// <param name="first">The fault recorded first, which is also in <paramref name="byNode"/>.</param>
    /// <param name="byNode">Each node's fault, or <see langword="null"/> for a node that did not fault.</param>
    internal static IReadOnlyList<NodeFault> Ordered(NodeFault? first, IReadOnlyList<NodeFault?> byNode)
    {
        if (first is null)
            return [];

        var ordered = new List<NodeFault>(byNode.Count) { first };
        foreach (var fault in byNode)
        {
            if (fault is not null && !ReferenceEquals(fault, first))
                ordered.Add(fault);
        }
        return ordered;
    }
}
