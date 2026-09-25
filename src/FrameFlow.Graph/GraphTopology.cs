// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// One wired edge, reduced to the facts the rules are about: the ports it joins, and whether it
/// was declared the trunk of a fork.
/// </summary>
/// <param name="From">The output port the edge leaves.</param>
/// <param name="To">The input port the edge enters.</param>
/// <param name="Blocks">
/// Whether a full edge makes its producer wait rather than dropping. A dropping edge anywhere on
/// a path is what keeps a fork-rejoin from deadlocking.
/// </param>
internal readonly record struct EdgeSpec(IPort From, IPort To, bool Blocks);

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

    /// <summary>Whether a lead or a count limit is set, so the join can stop reading at all.</summary>
    bool StopsReadingSecondary { get; }
}

/// <summary>An input port that knows whether an edge has been wired into it.</summary>
internal interface IWireableInput : IPort
{
    /// <summary>Whether <see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeOptions?)"/> has attached an edge.</summary>
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
    /// the join has a lead or a count limit, so it stops reading the secondary once the secondary
    /// runs ahead or fills the limit.
    /// </para>
    /// <para>
    /// Then: the join stops reading, the full secondary edge blocks the branch, the blocked
    /// branch stalls the fork's write of every branch, and the primary that would release the
    /// lead never arrives. A dropping edge anywhere on the path breaks it, and so does leaving
    /// both the lead and the count limit unset.
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

            // Every port whose items can still be blocked on their way to the secondary, and
            // every port whose items reach the primary at all. A port in both feeds both sides,
            // and is the one that stalls.
            var blockedIntoSecondary = PortsThatReach(join.SecondaryInput, edges, blockingOnly: true);
            var feedingPrimary = PortsThatReach(join.PrimaryInput, edges, blockingOnly: false);

            foreach (var shared in blockedIntoSecondary.Where(feedingPrimary.Contains))
            {
                yield return
                    $"'{node.Id}' sets a lead or a count limit, and '{Name(shared)}' feeds both "
                    + "of its inputs over a branch that blocks when full. The join stops reading "
                    + "the secondary, the branch blocks, and the primary that would release it "
                    + "never arrives. Give one edge on the branch a dropping policy, or leave the "
                    + "lead and the count limit unset.";
            }
        }
    }

    /// <summary>
    /// Every output port from which <paramref name="target"/> is reachable downstream. With
    /// <paramref name="blockingOnly"/>, only over edges that make their producer wait: a
    /// dropping edge anywhere breaks the chain of back-pressure, which is what makes a fork-
    /// rejoin safe.
    /// </summary>
    /// <remarks>
    /// Walks upstream rather than down, because the port that matters is the one that feeds both
    /// sides, and it can sit any number of hops above the join. A trunk that passes through an
    /// operator before the join is the case that a search from the primary's own edge misses.
    /// </remarks>
    private static HashSet<IPort> PortsThatReach(
        IPort target,
        IReadOnlyList<EdgeSpec> edges,
        bool blockingOnly
    )
    {
        var reaching = new HashSet<IPort>();
        var pending = new Queue<IPort>();
        var seen = new HashSet<IPort> { target };
        pending.Enqueue(target);

        while (pending.Count > 0)
        {
            var input = pending.Dequeue();
            foreach (var edge in edges.Where(e => e.To == input))
            {
                if (blockingOnly && !edge.Blocks)
                    continue;

                reaching.Add(edge.From);

                // Keep going up: every edge that feeds the node this output belongs to.
                foreach (var upstream in edges.Where(e => e.To.Owner == edge.From.Owner))
                {
                    if (seen.Add(upstream.To))
                        pending.Enqueue(upstream.To);
                }
            }
        }

        return reaching;
    }

    private static string Name(IPort port) => $"{port.Owner.Id}/{port.Name}";
}
