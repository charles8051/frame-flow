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
/// A fan-out is <see cref="Branch(EdgeOptions)"/>: it declares a second consumer of
/// the same output with its own edge options, and the chain it was called on carries on. <see cref="Join{TSecondary, TOut}(GraphChain{TSecondary}, SyncJoinNode{T, TSecondary, TOut}, EdgeOptions, EdgeOptions)"/>
/// pairs two chains back together. What the chain cannot reach is an input wired from
/// somewhere other than a chain head — for that, take <see cref="Output"/> and
/// <see cref="Graph"/> and call
/// <see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeOptions)"/> directly.
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

    /// <summary>
    /// Declares a branch off this chain: a second consumer of the same output, wired with its
    /// own edge options. Every consumer of the output shares the item by <c>AddRef</c>
    /// (ADR-0080), so it does not matter which one is called the trunk.
    /// </summary>
    /// <param name="options">
    /// The branch's first edge. Required rather than defaulted: an omitted config would be a
    /// capacity-1 blocking edge, which is the wrong shape for a branch and the mistake
    /// <see cref="ToSecondary"/> already warns about. A slow branch would then hold the trunk
    /// back frame for frame.
    /// </param>
    /// <returns>
    /// The branch before its first hop. That hop takes no options of its own, since these
    /// configure it; the chain it returns takes options on every later hop.
    /// </returns>
    public BranchChain<T> Branch(EdgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new BranchChain<T>(this, options);
    }

    /// <summary>
    /// Rejoins <paramref name="secondary"/> onto this chain through a sync join, and continues
    /// the chain over the join's output. This chain is the primary, which sets the join's firing
    /// cadence.
    /// </summary>
    /// <param name="secondary">
    /// The chain to pair onto this one, usually a <see cref="Branch(EdgeOptions)"/> continued
    /// through the node that produces the secondary.
    /// </param>
    /// <param name="join">The join node.</param>
    /// <param name="primaryOptions">The primary edge's options.</param>
    /// <param name="secondaryOptions">
    /// The secondary edge's options. Required, and for the reason on <see cref="ToSecondary"/>:
    /// a latest-wins secondary discards entries a <see cref="SyncMatch.Within"/> window still
    /// needs.
    /// </param>
    public GraphChain<TOut> Join<TSecondary, TOut>(
        GraphChain<TSecondary> secondary,
        SyncJoinNode<T, TSecondary, TOut> join,
        EdgeOptions primaryOptions,
        EdgeOptions secondaryOptions
    )
        where TSecondary : class, IRefCounted
        where TOut : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(join);
        ArgumentNullException.ThrowIfNull(primaryOptions);
        ArgumentNullException.ThrowIfNull(secondaryOptions);

        // Both sides have to belong to the same graph. Wired across two, each graph would hold
        // one of the join's edges: the one that runs reaches a join whose other input was never
        // wired, and refuses to start.
        if (!ReferenceEquals(_graph, secondary.Graph))
        {
            throw new ArgumentException(
                "The secondary chain belongs to a different graph. A join and both of its "
                    + "inputs are wired and run by one graph.",
                nameof(secondary)
            );
        }

        // Everything that can refuse either edge is checked before the first is connected, so a
        // rejected call leaves no half-wired join behind.
        Graph.RequireUnconnected(join.Primary);
        Graph.RequireUnconnected(join.Secondary);

        ToPrimary(join, primaryOptions);
        secondary.ToSecondary(join, secondaryOptions);
        return new GraphChain<TOut>(_graph, join.Output);
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
        ArgumentNullException.ThrowIfNull(sink);
        _graph.Connect(_head, sink.Input, options);
    }

    /// <summary>
    /// Terminates by wiring the head into a sync join's primary input. The
    /// primary sets the join's firing cadence.
    /// </summary>
    public void ToPrimary<TSecondary, TOut>(
        SyncJoinNode<T, TSecondary, TOut> join,
        EdgeOptions? options = null
    )
        where TSecondary : class, IRefCounted
        where TOut : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(join);
        _graph.Connect(_head, join.Primary, options);
    }

    /// <summary>
    /// Terminates by wiring the head into a sync join's secondary input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Give this edge a real buffer. <see cref="EdgeOptions.LatestWins(int)"/>,
    /// the habit carried over from fan-out, discards secondaries that a
    /// <see cref="SyncMatch.Within"/> window still needs.
    /// </para>
    /// <para>
    /// Without <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}.MaxLead"/>, the
    /// join reads this edge as fast as it can, so the buffer only absorbs bursts and
    /// never holds a producer back. With one, the join stops reading once the
    /// secondary leads by more than the lead, and this edge's overflow policy
    /// decides whether the producer waits or items are dropped.
    /// </para>
    /// </remarks>
    public void ToSecondary<TPrimary, TOut>(
        SyncJoinNode<TPrimary, T, TOut> join,
        EdgeOptions? options = null
    )
        where TPrimary : class, IRefCounted
        where TOut : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(join);
        _graph.Connect(_head, join.Secondary, options);
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
