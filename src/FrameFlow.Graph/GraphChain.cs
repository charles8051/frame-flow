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
/// back to the explicit <see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeOptions)"/> API — the chain is
/// sugar for linear segments only.
/// </para>
/// </remarks>
public readonly struct GraphChain<T>
    where T : class, IRefCounted
{
    private readonly Graph _graph;
    private readonly OutputPort<T> _head;

    // Set by Branch: the config this chain's next hop uses, and the fact that the next hop is a
    // branch rather than the fork's trunk. A chain that was not produced by Branch carries
    // neither, so nothing about a linear chain changes.
    private readonly EdgeConfig<T>? _pending;
    private readonly bool _isBranch;

    internal GraphChain(Graph graph, OutputPort<T> head)
        : this(graph, head, pending: null, isBranch: false) { }

    private GraphChain(Graph graph, OutputPort<T> head, EdgeConfig<T>? pending, bool isBranch)
    {
        _graph = graph;
        _head = head;
        _pending = pending;
        _isBranch = isBranch;
    }

    /// <summary>
    /// The edge this chain's next hop takes, and whether that edge is the fork's trunk. A branch
    /// uses the config <see cref="Branch"/> was given; anything else uses the options passed at
    /// the call site. The trunk is the first non-branch edge to leave a forked port.
    /// </summary>
    private (EdgeConfig<T> Config, bool Inherit) NextEdge(EdgeOptions? options)
    {
        var config = _pending ?? new EdgeConfig<T>(options ?? EdgeOptions.Default, Cloner: null);
        return (config, !_isBranch && _graph.IsForked(_head));
    }

    /// <summary>
    /// Declares a branch off this chain: a second consumer of the same output, wired with its
    /// own edge config. The chain <see cref="Branch"/> was called on stays the trunk and takes
    /// the incoming ref; the branch clones or <c>AddRef</c>s according to its config.
    /// </summary>
    /// <param name="config">
    /// Required rather than defaulted. An omitted config would be a capacity-1 blocking edge,
    /// which is the wrong shape for a branch and the mistake <see cref="ToSecondary"/> already
    /// warns about: a slow branch would then hold the trunk back frame for frame. A branch off a
    /// one-shot item type needs a cloner here, because its <c>AddRef</c> throws by design.
    /// </param>
    /// <remarks>
    /// <para>
    /// Declare the branch before wiring the trunk's next hop. The trunk is whichever edge leaves
    /// this port next without being a branch, so a trunk wired first is an ordinary fan-out and
    /// falls back to ADR-0054's rule, where the first cloner-less edge inherits.
    /// </para>
    /// <para>
    /// A fan-out made only of branches has no trunk. That is the all-cloner case, where every
    /// consumer holds an independent item and the incoming ref is released after the clones are
    /// made.
    /// </para>
    /// </remarks>
    public GraphChain<T> Branch(EdgeConfig<T> config)
    {
        _graph.DeclareFork(_head);
        return new GraphChain<T>(_graph, _head, config, isBranch: true);
    }

    /// <summary>
    /// Declares a branch that takes its own ref rather than a clone. The item type's
    /// <c>AddRef</c> has to work: a one-shot item needs the
    /// <see cref="Branch(EdgeConfig{T})"/> overload and a cloner.
    /// </summary>
    /// <param name="options">
    /// Required, for the reason on the other overload: a defaulted edge would be capacity-1 and
    /// blocking, so a slow branch would hold the trunk back frame for frame.
    /// </param>
    public GraphChain<T> Branch(EdgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Branch(new EdgeConfig<T>(options, Cloner: null));
    }

    /// <summary>
    /// Rejoins <paramref name="secondary"/> onto this chain through a sync join, and continues
    /// the chain over the join's output. This chain is the primary, which sets the join's firing
    /// cadence.
    /// </summary>
    /// <param name="secondary">The chain to pair onto this one, usually a <see cref="Branch"/>.</param>
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
        var (config, inherit) = NextEdge(options);
        _graph.Connect(_head, next.Input, config, inherit);
        return new GraphChain<TOut>(_graph, next.Output);
    }

    /// <summary>Chains through a 1→N operator.</summary>
    public GraphChain<TOut> Then<TOut>(
        MultiOperatorNode<T, TOut> next,
        EdgeOptions? options = null
    )
        where TOut : class, IRefCounted
    {
        var (config, inherit) = NextEdge(options);
        _graph.Connect(_head, next.Input, config, inherit);
        return new GraphChain<TOut>(_graph, next.Output);
    }

    /// <summary>Terminates by wiring the head into a sink.</summary>
    public void To(SinkNode<T> sink, EdgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var (config, inherit) = NextEdge(options);
        _graph.Connect(_head, sink.Input, config, inherit);
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
        var (config, inherit) = NextEdge(options);
        _graph.Connect(_head, join.Primary, config, inherit);
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
        var (config, inherit) = NextEdge(options);
        _graph.Connect(_head, join.Secondary, config, inherit);
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
