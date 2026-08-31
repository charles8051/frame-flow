// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading.Channels;

namespace FrameFlow.Graph;

/// <summary>
/// Named connection point on a node where edges attach. Ports decouple
/// node identity from edge wiring: a node exposes one or more ports;
/// graphs wire ports together via <see cref="Graph.Connect"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ports replace the earlier <c>IHasInput&lt;T&gt;</c> /
/// <c>IHasOutput&lt;T&gt;</c> interfaces. The interface-per-port
/// approach couldn't represent multi-input nodes cleanly (a single
/// class can't implement <c>IHasInput&lt;TIn1&gt;</c> and
/// <c>IHasInput&lt;TIn2&gt;</c> with conflicting <c>InputReader</c>
/// properties). Ports as discrete objects compose freely.
/// </para>
/// </remarks>
public interface IPort
{
    /// <summary>The node this port belongs to.</summary>
    INode Owner { get; }

    /// <summary>Human-readable port name for diagnostics (e.g., "input", "A", "captionStream").</summary>
    string Name { get; }
}

/// <summary>
/// An input port: the node reads items of type <typeparamref name="T"/>
/// from this port. Wired by <see cref="Graph.Connect"/> to one
/// <see cref="OutputPort{T}"/> on an upstream node.
/// </summary>
public sealed class InputPort<T> : IPort
    where T : class, IRefCounted
{
    public InputPort(INode owner, string name)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(name);
        Owner = owner;
        Name = name;
    }

    public INode Owner { get; }
    public string Name { get; }

    /// <summary>
    /// Eagerly tracks whether this port has been connected, so
    /// <see cref="Graph.Connect"/> can reject double-connects at
    /// build time rather than at run time.
    /// </summary>
    internal bool IsConnected { get; set; }

    /// <summary>
    /// Set by <see cref="Graph"/> at wire-up time (inside
    /// <see cref="Graph.RunAsync"/>). Null until the graph runs.
    /// </summary>
    internal ChannelReader<T>? Reader { get; set; }
}

/// <summary>
/// An output port: the node writes items of type <typeparamref name="T"/>
/// to this port. A single output port can have multiple downstream
/// connections — each branch either gets a fresh ref via <c>AddRef</c>
/// (the default), or an explicitly-cloned item when the branch supplied
/// a cloner via <see cref="EdgeOptionsExtensions.WithCloner{T}"/>
/// (per ADR-0054, for one-shot frame types whose <c>AddRef</c> throws).
/// </summary>
public sealed class OutputPort<T> : IPort
    where T : class, IRefCounted
{
    public OutputPort(INode owner, string name)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(name);
        Owner = owner;
        Name = name;
    }

    public INode Owner { get; }
    public string Name { get; }

    /// <summary>
    /// One outgoing edge per downstream connection. Appended by
    /// <see cref="Graph.Connect"/> as edges are wired up.
    /// </summary>
    internal List<OutputEdge<T>> Writers { get; } = new();
}

/// <summary>
/// Internal per-branch wireup record carried on an
/// <see cref="OutputPort{T}"/>. Pairs a <see cref="ChannelWriter{T}"/>
/// with an optional cloner used by the fan-out path in
/// <c>NodePumps.ForwardAsync</c>; see ADR-0054.
/// </summary>
/// <param name="Writer">The downstream channel writer.</param>
/// <param name="Cloner">
/// When non-<see langword="null"/>, the fan-out invokes <c>Cloner(item)</c>
/// to produce the per-branch item instead of calling <c>item.AddRef()</c>.
/// Required for one-shot frame types (e.g. converter outputs) where
/// <c>AddRef</c> throws by design.
/// </param>
internal sealed record OutputEdge<T>(ChannelWriter<T> Writer, Func<T, T>? Cloner)
    where T : class, IRefCounted;
