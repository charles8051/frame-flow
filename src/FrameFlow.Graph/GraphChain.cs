// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Fluent chain over a graph's output port. Reduces port-based wiring
/// verbosity for the common case of linear pipelines: instead of
/// repeating <c>graph.Connect(a.Output, b.Input)</c> on every edge,
/// callers write <c>graph.Pipeline(source).Then(b).Then(c).To(sink)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The chain is a transient builder — it holds a reference to the
/// graph and the current "head" output port; each <see cref="Then{TOut}(OperatorNode{T, TOut}, EdgeOptions?)"/>
/// call wires the head into the next node's input and returns a new
/// chain over that node's output. <see cref="To(SinkNode{T}, EdgeOptions?)"/>
/// terminates by wiring into a sink and returns void.
/// </para>
/// <para>
/// For multi-output topologies (fan-out to multiple consumers), drop
/// back to the explicit <see cref="Graph.Connect"/> API — the chain is
/// sugar for linear segments only.
/// </para>
/// </remarks>
public readonly struct GraphChain<T>
    where T : class, IRefCounted
{
    private readonly Graph _graph;
    private readonly OutputPort<T> _head;

    internal GraphChain(Graph graph, OutputPort<T> head)
    {
        _graph = graph;
        _head = head;
    }

    /// <summary>The current head of the chain. Use this to drop back into the explicit API.</summary>
    public OutputPort<T> Output => _head;

    /// <summary>The graph this chain is building into.</summary>
    public Graph Graph => _graph;

    /// <summary>Chains through a 1→0..1 operator.</summary>
    public GraphChain<TOut> Then<TOut>(
        OperatorNode<T, TOut> next,
        EdgeOptions? options = null
    )
        where TOut : class, IRefCounted
    {
        _graph.Connect(_head, next.Input, options);
        return new GraphChain<TOut>(_graph, next.Output);
    }

    /// <summary>Chains through a 1→N operator.</summary>
    public GraphChain<TOut> Then<TOut>(
        MultiOperatorNode<T, TOut> next,
        EdgeOptions? options = null
    )
        where TOut : class, IRefCounted
    {
        _graph.Connect(_head, next.Input, options);
        return new GraphChain<TOut>(_graph, next.Output);
    }

    /// <summary>Terminates by wiring the head into a sink.</summary>
    public void To(SinkNode<T> sink, EdgeOptions? options = null)
    {
        _graph.Connect(_head, sink.Input, options);
    }
}

/// <summary>Fluent-chain entry points on <see cref="Graph"/>.</summary>
public static class GraphChainExtensions
{
    /// <summary>Starts a fluent chain from an arbitrary output port.</summary>
    public static GraphChain<T> Pipeline<T>(this Graph graph, OutputPort<T> from)
        where T : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(from);
        graph.Add(from.Owner);
        return new GraphChain<T>(graph, from);
    }

    /// <summary>Starts a fluent chain from a source node's output.</summary>
    public static GraphChain<T> Pipeline<T>(this Graph graph, SourceNode<T> source)
        where T : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);
        graph.Add(source);
        return new GraphChain<T>(graph, source.Output);
    }
}
