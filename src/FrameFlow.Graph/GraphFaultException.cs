// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Thrown by <see cref="Graph.RunAsync"/> when one or more of the graph's nodes faulted (#499).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Exception.InnerException"/> is the fault recorded first, the first a pump caught,
/// and <see cref="NodeId"/> names its node. The message names that node and carries the inner
/// exception's message. <see cref="Faults"/> holds every node's fault, that one first and the rest
/// in the order their nodes were added to the graph. Once one node faults the run is cancelled, so
/// a later fault is often a body failing on that cancellation rather than a cause of its own.
/// </para>
/// <para>
/// A node faults when its body throws under <see cref="FailureResponse.Propagate"/>, an
/// <see cref="OperationCanceledException"/> the body threw while the graph was not cancelled
/// included (#489), or when its pump fails outside the body, as it does for an input with no edge.
/// A graph refused before its nodes run throws <see cref="InvalidOperationException"/>, and a
/// cancelled one throws <see cref="OperationCanceledException"/>. Neither is wrapped.
/// </para>
/// </remarks>
public sealed class GraphFaultException : Exception
{
    /// <summary>A fault of the nodes in <paramref name="faults"/>, the first of them the cause.</summary>
    /// <param name="faults">Every node's fault, the one recorded first at index 0.</param>
    /// <exception cref="ArgumentException"><paramref name="faults"/> is empty or holds a null.</exception>
    public GraphFaultException(IReadOnlyList<NodeFault> faults)
        : base(MessageFor(Checked(faults)), faults[0].Exception)
    {
        Faults = [.. faults];
    }

    /// <summary>The id of the node whose fault is <see cref="Exception.InnerException"/>.</summary>
    public string NodeId => Faults[0].NodeId;

    /// <summary>Every node's fault, <see cref="NodeId"/>'s first.</summary>
    public IReadOnlyList<NodeFault> Faults { get; }

    /// <summary>
    /// The message for a fault of <paramref name="faults"/>: the first node, how many others
    /// faulted, and the first node's exception message.
    /// </summary>
    internal static string MessageFor(IReadOnlyList<NodeFault> faults)
    {
        var first = faults[0];
        return (faults.Count - 1) switch
        {
            0 => $"Node '{first.NodeId}' faulted: {first.Exception.Message}",
            1 => $"Node '{first.NodeId}' faulted, as did 1 other node: {first.Exception.Message}",
            var others =>
                $"Node '{first.NodeId}' faulted, as did {others} other nodes: {first.Exception.Message}",
        };
    }

    private static IReadOnlyList<NodeFault> Checked(IReadOnlyList<NodeFault> faults)
    {
        ArgumentNullException.ThrowIfNull(faults);
        if (faults.Count == 0)
            throw new ArgumentException("A graph fault needs at least one node's fault.", nameof(faults));
        foreach (var fault in faults)
        {
            if (fault is null)
                throw new ArgumentException("A node's fault is null.", nameof(faults));
        }
        return faults;
    }
}
