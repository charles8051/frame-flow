// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

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
/// </remarks>
internal static class GraphTopology
{
    /// <summary>
    /// Returns one message per broken rule, in node order, or an empty list when the wiring is
    /// sound.
    /// </summary>
    internal static IReadOnlyList<string> Validate(IReadOnlyList<INode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var errors = new List<string>();

        foreach (var node in nodes)
        {
            if (node is not IRequiresEveryInput required)
                continue;

            foreach (var port in required.RequiredInputs)
            {
                if (!port.IsWired)
                {
                    errors.Add(
                        $"Input port '{port.Owner.Id}/{port.Name}' was never connected. Every "
                            + $"input of '{node.Id}' has to carry an edge, or the run hangs "
                            + "waiting for an item that cannot arrive."
                    );
                }
            }
        }

        return errors;
    }
}
