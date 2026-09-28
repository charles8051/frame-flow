// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// What a node declares about the memory domains of the frames it handles (#435): the domains
/// its input accepts, and the domain its output has.
/// </summary>
/// <remarks>
/// <para>
/// A graph checks the declarations before it runs, from each source that says which domains it
/// can hand out, and refuses a path where a domain reaches a node that does not accept it. The
/// message names the node. Without the check, a node that reads CPU pixels fails on the first
/// GPU frame instead.
/// </para>
/// <para>
/// Nothing is converted for a mismatch: a conversion stays an explicit node, such as
/// <c>VideoOperators.ToCpu</c> (ADR-0012). A node that declares nothing accepts either domain and
/// passes on what it received, so it is never refused.
/// </para>
/// </remarks>
public sealed record FrameDomainRule
{
    private FrameDomainRule(FrameMemoryDomains accepts, FrameMemoryDomains? emits)
    {
        Accepts = accepts;
        Emits = emits;
    }

    /// <summary>The domains the node's input accepts.</summary>
    public FrameMemoryDomains Accepts { get; }

    /// <summary>
    /// The domain of the node's output, or <see langword="null"/> when it is whatever arrived.
    /// </summary>
    public FrameMemoryDomains? Emits { get; }

    /// <summary>Accepts either domain and passes on what arrived. The default.</summary>
    public static FrameDomainRule Any { get; } = new(FrameMemoryDomains.Any, null);

    /// <summary>Reads CPU frames only: the node reads their pixels on the CPU.</summary>
    public static FrameDomainRule CpuOnly { get; } = new(FrameMemoryDomains.Cpu, null);

    /// <summary>Accepts either domain and emits CPU frames: a download to system memory.</summary>
    public static FrameDomainRule ToCpu { get; } = new(FrameMemoryDomains.Any, FrameMemoryDomains.Cpu);

    /// <summary>Accepts <paramref name="accepts"/> and emits <paramref name="emits"/>.</summary>
    /// <param name="accepts">The domains the input accepts.</param>
    /// <param name="emits">The output's domain, or <see langword="null"/> for what arrived.</param>
    /// <exception cref="ArgumentException"><paramref name="accepts"/> is empty.</exception>
    public static FrameDomainRule Accepting(FrameMemoryDomains accepts, FrameMemoryDomains? emits = null)
    {
        if ((accepts & FrameMemoryDomains.Any) == FrameMemoryDomains.None)
            throw new ArgumentException("A node has to accept at least one domain.", nameof(accepts));
        return new FrameDomainRule(accepts & FrameMemoryDomains.Any, emits & FrameMemoryDomains.Any);
    }
}
