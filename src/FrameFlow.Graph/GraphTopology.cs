// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// One wired edge, reduced to the facts the topology rules are about: which ports it joins,
/// whether it was declared the inheritor of the incoming ref, and whether it carries a cloner.
/// </summary>
/// <remarks>
/// The channel, the options and the cloner delegate live on the edge itself. This is what
/// <see cref="GraphTopology.Validate"/> reads, and it holds no behaviour, so the rules over a
/// topology are a total function of the topology.
/// </remarks>
/// <param name="From">The output port the edge leaves.</param>
/// <param name="To">The input port the edge enters.</param>
/// <param name="Inherit">
/// Whether this edge was declared the trunk of a fan-out. Set only by the chain's fork and join
/// wiring; a <see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeConfig{T})"/> edge is
/// never marked, and keeps the first-cloner-less rule from ADR-0054.
/// </param>
/// <param name="HasCloner">Whether the edge produces its item with a cloner rather than a ref.</param>
internal readonly record struct EdgeSpec(IPort From, IPort To, bool Inherit, bool HasCloner);

/// <summary>
/// A node whose inputs are all required, checked before the run rather than when its pump
/// starts. Both refuse; this one refuses earlier and names the node.
/// </summary>
internal interface IRequiresEveryInput
{
    /// <summary>Every input port that has to carry an edge before the graph runs.</summary>
    IEnumerable<IPort> RequiredInputs { get; }
}

/// <summary>
/// The rules a wired graph has to satisfy, as a total function of its edge list.
/// </summary>
/// <remarks>
/// <para>
/// Pure by design: the topology is a value, so the rules over it are testable without running a
/// graph, starting a pump or touching a channel. The shell (<see cref="Graph.RunAsync"/>) calls
/// this once before it wires anything and throws on the first run that would be malformed.
/// </para>
/// <para>
/// The rules are about wiring a run cannot make sense of: a fan-out with two edges each claiming
/// the incoming ref, and a node whose inputs are not all wired. A pump refuses the second too,
/// but too late to help: its own exit leaves the producer blocked on a channel nobody reads, and
/// the run hangs.
/// </para>
/// </remarks>
internal static class GraphTopology
{
    /// <summary>
    /// Returns one message per broken rule, in a stable order, or an empty list when the
    /// topology is sound.
    /// </summary>
    /// <param name="edges">Every edge the graph has wired.</param>
    /// <param name="nodes">Every node in the graph, for the rules that are about a node's inputs.</param>
    internal static IReadOnlyList<string> Validate(
        IReadOnlyList<EdgeSpec> edges,
        IReadOnlyList<INode> nodes
    )
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(nodes);

        var errors = new List<string>();

        // A marked edge that clones is a contradiction: the mark says "you take the incoming
        // ref", the cloner says "make your own".
        foreach (var edge in edges)
        {
            if (edge.Inherit && edge.HasCloner)
            {
                errors.Add(
                    $"Edge {Describe(edge)} is marked as the trunk of its fan-out and also "
                        + "carries a cloner. The trunk inherits the incoming ref; only its "
                        + "siblings clone."
                );
            }
        }

        // Two trunks on one port means two edges each believing they own the incoming ref, which
        // is a double-release the run would discover as a use-after-dispose.
        foreach (var group in edges.Where(e => e.Inherit).GroupBy(e => e.From))
        {
            if (group.Count() > 1)
            {
                errors.Add(
                    $"Output port '{PortName(group.Key)}' has {group.Count()} edges marked as "
                        + "the trunk of its fan-out. Exactly one edge inherits the incoming ref."
                );
            }
        }

        // A pump does refuse an unconnected input, but refusing it there is not enough: the join
        // pump exits while the producer is still writing into the primary's capacity-1 channel,
        // and the run hangs instead of faulting. Measured by disabling this check: the test for
        // an unwired secondary stops terminating. Refusing before any pump starts is what makes
        // it an error rather than a hang.
        var wired = new HashSet<IPort>(edges.Select(e => e.To));
        foreach (var node in nodes)
        {
            if (node is not IRequiresEveryInput required)
                continue;

            foreach (var port in required.RequiredInputs)
            {
                if (!wired.Contains(port))
                {
                    errors.Add(
                        $"Input port '{PortName(port)}' was never connected. Every input of "
                            + $"'{node.Id}' has to carry an edge, or it waits for an item that "
                            + "cannot arrive."
                    );
                }
            }
        }

        return errors;
    }

    private static string Describe(EdgeSpec edge) =>
        $"'{PortName(edge.From)}' -> '{PortName(edge.To)}'";

    private static string PortName(IPort port) => $"{port.Owner.Id}/{port.Name}";
}
