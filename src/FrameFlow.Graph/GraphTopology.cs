// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// One wired edge, reduced to the facts the rules are about: the ports it joins, and whether it
/// was declared the trunk of a fork.
/// </summary>
/// <param name="From">The output port the edge leaves.</param>
/// <param name="To">The input port the edge enters.</param>
/// <param name="Inherit">Whether the edge takes the incoming ref rather than a clone or an AddRef.</param>
/// <param name="Blocks">
/// Whether a full edge makes its producer wait rather than dropping. A dropping edge anywhere on
/// a path is what keeps a fork-rejoin from deadlocking.
/// </param>
internal readonly record struct EdgeSpec(IPort From, IPort To, bool Inherit, bool Blocks);

/// <summary>
/// A join whose secondary it can stop reading, which is what makes a fork-rejoin able to
/// deadlock.
/// </summary>
internal interface IHoldsItsSecondary
{
    /// <summary>The input the join's cadence comes from.</summary>
    IPort PrimaryInput { get; }

    /// <summary>The input the join stops reading while the secondary leads.</summary>
    IPort SecondaryInput { get; }

    /// <summary>Whether a lead is set, so the join can stop reading at all.</summary>
    bool StopsReadingSecondary { get; }
}

/// <summary>An input port that knows whether an edge has been wired into it.</summary>
internal interface IWireableInput : IPort
{
    /// <summary>Whether <see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeConfig{T})"/> has attached an edge.</summary>
    bool IsWired { get; }
}

/// <summary>
/// A node whose inputs are all required: it reads every one of them before it produces
/// anything, so a missing edge is a graph that cannot work rather than a caller's choice.
/// </summary>
internal interface IRequiresEveryInput
{
    /// <summary>Every input port that has to carry an edge before the graph runs.</summary>
    IEnumerable<IWireableInput> RequiredInputs { get; }
}

/// <summary>
/// The wiring rules a graph has to satisfy, as a total function of its nodes.
/// </summary>
/// <remarks>
/// <para>
/// Pure by design: the answer depends only on which ports carry an edge, so the rules are
/// testable without starting a pump or touching a channel. <see cref="Graph.RunAsync"/> calls
/// this before it resets or wires anything.
/// </para>
/// <para>
/// <b>Why before the pumps.</b> A pump does refuse an unconnected input, but too late to help:
/// the join pump refuses and exits while the producer is still writing into the primary's
/// capacity-1 channel, and the run hangs instead of faulting. Measured by disabling this check,
/// which makes the test for an unwired secondary stop terminating rather than fail.
/// </para>
/// <para>
/// The fork rule is about ownership: two edges leaving one port both claiming the incoming ref
/// is a double release. It is not reachable through
/// <see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeConfig{T})"/>, which never marks
/// an edge, and is reachable by wiring a chain's trunk twice after
/// <see cref="GraphChain{T}.Branch"/>.
/// </para>
/// </remarks>
internal static class GraphTopology
{
    /// <summary>
    /// Returns one message per broken rule, in node order, or an empty list when the wiring is
    /// sound.
    /// </summary>
    internal static IReadOnlyList<string> Validate(
        IReadOnlyList<INode> nodes,
        IReadOnlyList<EdgeSpec> edges
    )
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var errors = new List<string>();

        foreach (var group in edges.Where(e => e.Inherit).GroupBy(e => e.From))
        {
            if (group.Count() > 1)
            {
                errors.Add(
                    $"Output port '{Name(group.Key)}' has {group.Count()} trunk edges. A forked "
                        + "port has one trunk, and the rest are branches that clone or AddRef."
                );
            }
        }

        errors.AddRange(DeadlockedForkRejoins(nodes, edges));

        foreach (var node in nodes)
        {
            if (node is not IRequiresEveryInput required)
                continue;

            foreach (var port in required.RequiredInputs)
            {
                if (!port.IsWired)
                {
                    errors.Add(
                        $"Input port '{Name(port)}' was never connected. Every input of "
                            + $"'{node.Id}' has to carry an edge, or the run hangs waiting for "
                            + "an item that cannot arrive."
                    );
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Finds every fork that rejoins a join which can stop reading, along a path that can only
    /// wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cycle needs four things at once. A port feeds the join's primary. A path leaves that
    /// same port and reaches the join's secondary. Every edge on that path blocks when full. And
    /// the join has a lead, so it stops reading the secondary once the secondary runs ahead.
    /// </para>
    /// <para>
    /// Then: the join stops reading, the full secondary edge blocks the branch, the blocked
    /// branch stalls the fork's write of every branch, and the primary that would release the
    /// lead never arrives. A dropping edge anywhere on the path breaks it, and so does leaving
    /// the lead unset.
    /// </para>
    /// <para>
    /// Conservative in one direction: a blocking fork-rejoin that a large enough lead would have
    /// survived is refused anyway. The message names both escapes.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> DeadlockedForkRejoins(
        IReadOnlyList<INode> nodes,
        IReadOnlyList<EdgeSpec> edges
    )
    {
        foreach (var node in nodes)
        {
            if (node is not IHoldsItsSecondary join || !join.StopsReadingSecondary)
                continue;

            foreach (var primaryEdge in edges.Where(e => e.To == join.PrimaryInput))
            {
                if (ReachesBlocking(primaryEdge.From, join.SecondaryInput, edges))
                {
                    yield return
                        $"'{node.Id}' sets a lead and both of its inputs come from "
                        + $"'{Name(primaryEdge.From)}', over edges that all block when full. The "
                        + "join stops reading the secondary, the branch blocks, and the primary "
                        + "that would release it never arrives. Give one edge on the branch a "
                        + "dropping policy, or leave the lead unset.";
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="target"/> is reachable from <paramref name="from"/> over edges
    /// that all block. Breadth-first over nodes, so a cycle in the wiring cannot loop it.
    /// </summary>
    private static bool ReachesBlocking(
        IPort from,
        IPort target,
        IReadOnlyList<EdgeSpec> edges
    )
    {
        var queue = new Queue<IPort>();
        var seen = new HashSet<INode>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var port = queue.Dequeue();
            foreach (var edge in edges.Where(e => e.From == port && e.Blocks))
            {
                if (edge.To == target)
                    return true;

                // Walk on through the node this edge feeds, by way of every edge leaving it.
                var next = edge.To.Owner;
                if (!seen.Add(next))
                    continue;

                foreach (var onward in edges.Where(e => e.From.Owner == next))
                    queue.Enqueue(onward.From);
            }
        }

        return false;
    }

    private static string Name(IPort port) => $"{port.Owner.Id}/{port.Name}";
}
