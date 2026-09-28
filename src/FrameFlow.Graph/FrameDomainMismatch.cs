// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// A node that a memory domain can reach and that does not accept it (#435).
/// </summary>
/// <param name="Node">The node's id.</param>
/// <param name="Refused">The domains that can reach the node and that it does not accept.</param>
/// <param name="Accepts">The domains the node accepts.</param>
public sealed record FrameDomainMismatch(string Node, FrameMemoryDomains Refused, FrameMemoryDomains Accepts)
{
    /// <summary>What is wrong, naming the node, for an exception or a log line.</summary>
    public string Message =>
        $"'{Node}' takes {Describe(Accepts)} frames, and {Describe(Refused)} frames can reach it. "
            + (Refused.HasFlag(FrameMemoryDomains.Gpu)
                ? "Put a node that downloads them to system memory, such as VideoOperators.ToCpu, before it, "
                    + "or give it a GPU path."
                : "Put a node that uploads them to the GPU before it.");

    private static string Describe(FrameMemoryDomains domains) =>
        domains switch
        {
            FrameMemoryDomains.Cpu => "CPU",
            FrameMemoryDomains.Gpu => "GPU",
            _ => "CPU and GPU",
        };
}

/// <summary>
/// Checks where a source's memory domains can go against what the nodes on the way accept
/// (#435). A total function of its arguments.
/// </summary>
internal static class FrameDomainChecks
{
    /// <summary>
    /// The first node, walking from <paramref name="source"/>, that one of the domains reaching
    /// it does not accept, or <see langword="null"/> when every node accepts what can reach it.
    /// </summary>
    /// <remarks>
    /// Each node passes on what arrived unless it declares an output domain, as a download does.
    /// A domain the source can hand out reaches every node downstream of it that nothing converts
    /// on the way, on every branch. A node that declares it emits no domain, such as an inference
    /// node whose results carry no frame, ends the walk there.
    /// </remarks>
    /// <param name="source">The port the frames leave from.</param>
    /// <param name="emitted">The domains the source can hand out.</param>
    /// <param name="edges">The graph's edges.</param>
    /// <param name="undeclared">
    /// What a node that declares nothing takes. <see cref="FrameDomainRule.Any"/>, the default,
    /// never refuses one; <see cref="FrameDomainRule.CpuOnly"/> asks whether every node GPU frames
    /// reach has said it takes them.
    /// </param>
    public static FrameDomainMismatch? For(
        IPort source,
        FrameMemoryDomains emitted,
        IReadOnlyList<EdgeSpec> edges,
        FrameDomainRule? undeclared = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edges);
        undeclared ??= FrameDomainRule.Any;

        var reaching = new Dictionary<IPort, FrameMemoryDomains> { [source] = emitted & FrameMemoryDomains.Any };
        var pending = new Queue<IPort>();
        pending.Enqueue(source);

        while (pending.Count > 0)
        {
            var output = pending.Dequeue();
            var domains = reaching[output];
            foreach (var edge in edges)
            {
                if (edge.From != output)
                    continue;

                var rule = RuleAt(edge.To, undeclared);
                var refused = domains & ~rule.Accepts;
                if (refused != FrameMemoryDomains.None)
                    return new FrameDomainMismatch(edge.To.Owner.Id, refused, rule.Accepts);

                var onwardDomains = rule.Emits ?? domains;
                foreach (var onward in edges)
                {
                    if (onward.From.Owner != edge.To.Owner)
                        continue;
                    var before = reaching.GetValueOrDefault(onward.From);
                    var after = before | onwardDomains;
                    if (reaching.ContainsKey(onward.From) && after == before)
                        continue;
                    reaching[onward.From] = after;
                    pending.Enqueue(onward.From);
                }
            }
        }

        return null;
    }

    private static FrameDomainRule RuleAt(IPort input, FrameDomainRule undeclared) =>
        (input.Owner as IDeclaresDomains)?.DomainsAt(input) ?? undeclared;
}
