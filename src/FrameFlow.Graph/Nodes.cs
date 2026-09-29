// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Base node interface: identity + failure policy. All node kinds
/// also implement <see cref="IPumpableNode"/> internally to give the
/// graph runner a single uniform dispatch point.
/// </summary>
public interface INode
{
    /// <summary>Unique identifier for diagnostics and graph traversal.</summary>
    string Id { get; }

    /// <summary>How this node responds when its operator function throws.</summary>
    FailureResponse OnError { get; }
}

/// <summary>
/// Internal contract that every node implements so the graph runner
/// can dispatch the per-node pump loop without reflection. Each node
/// type wires <see cref="RunPumpAsync"/> to the matching pump method
/// in <see cref="NodePumps"/>.
/// </summary>
/// <remarks>
/// Pumps receive the <see cref="GraphRun"/>, not just its token, so a pump
/// that faults can record the fault there, which cancels its siblings. It
/// does so *before* it async-drains its input channel, so upstream sources
/// stop producing — otherwise upstream items written after the pump's main
/// loop exits would have nowhere to go.
/// </remarks>
internal interface IPumpableNode : INode
{
    Task RunPumpAsync(GraphRun run);
}

// ─────────────────────────────────────────────────────────────────
// Source: 0 input, 1 output
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// A source node: produces items via a <see cref="Producer{TOut}"/>
/// function until it returns null (end of stream).
/// </summary>
public sealed class SourceNode<TOut> : IPumpableNode, IBudgetedSource, IDomainSource
    where TOut : class, IRefCounted
{
    public string Id { get; }
    public FailureResponse OnError { get; }
    public Producer<TOut> Body { get; }
    public OutputPort<TOut> Output { get; }

    /// <summary>
    /// Optional cleanup invoked once when the pump exits, regardless
    /// of reason (EOS, cancellation, exception). Used by source
    /// adapters that hold long-lived state (an
    /// <see cref="IAsyncEnumerator{T}"/>, a native handle, a network
    /// connection) and need to release it deterministically. The
    /// substrate doesn't otherwise have a "source pump exiting" hook —
    /// without this, adapter state leaks on cancellation.
    /// </summary>
    public Func<ValueTask>? Cleanup { get; }

    /// <summary>
    /// Called with this source's <see cref="FrameBudget"/> each time a graph containing it
    /// starts a run, before any pump (ADR-0081, decision 4). A source whose items come from a
    /// fixed pool sizes the pool from it, copies when the pool cannot hold it, or throws to
    /// refuse the run.
    /// </summary>
    public Action<FrameBudget>? OnBudget { get; }

    /// <summary>
    /// Read each time a graph containing this source starts a run, before any pump: the memory
    /// domains its frames can be in (#435). The graph refuses the run when one of them can reach
    /// a node that does not accept it. <see langword="null"/> when the source does not say, and
    /// then nothing downstream of it is checked.
    /// </summary>
    public Func<FrameMemoryDomains>? Emits { get; }

    public SourceNode(
        string id,
        Producer<TOut> body,
        FailureResponse onError = FailureResponse.Propagate,
        Func<ValueTask>? cleanup = null,
        Action<FrameBudget>? onBudget = null,
        Func<FrameMemoryDomains>? emits = null
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        Id = id;
        Body = body;
        OnError = onError;
        Cleanup = cleanup;
        OnBudget = onBudget;
        Emits = emits;
        Output = new OutputPort<TOut>(this, "output");
    }

    IPort IDomainSource.DomainOutput => Output;

    FrameMemoryDomains? IDomainSource.EmittedDomains => Emits?.Invoke();

    IPort IBudgetedSource.BudgetedOutput => Output;

    bool IBudgetedSource.WantsBudget => OnBudget is not null;

    void IBudgetedSource.ApplyBudget(FrameBudget budget) => OnBudget?.Invoke(budget);

    Task IPumpableNode.RunPumpAsync(GraphRun run) =>
        NodePumps.PumpSourceAsync(this, run);
}

// ─────────────────────────────────────────────────────────────────
// Linear operator: 1 input, 0..1 output (per item)
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// 1→0..1 operator node. Receives items, transforms them, forwards
/// outputs. The operator function may return null to drop the input
/// without producing an output.
/// </summary>
public sealed class OperatorNode<TIn, TOut> : IPumpableNode, IDeclaresHolding, IDeclaresDomains
    where TIn : class, IRefCounted
    where TOut : class, IRefCounted
{
    public string Id { get; }
    public FailureResponse OnError { get; }
    public Operator<TIn, TOut> Body { get; }
    public InputPort<TIn> Input { get; }
    public OutputPort<TOut> Output { get; }

    /// <summary>
    /// What the node holds of its input, and whether its output can share the input's storage
    /// (ADR-0081). <see cref="FrameHolding.Unbounded"/> when the constructor was given none.
    /// </summary>
    public FrameHolding Holding { get; }

    /// <summary>
    /// The memory domains the node accepts and emits (#435). <see cref="FrameDomainRule.Any"/>
    /// when the constructor was given none.
    /// </summary>
    public FrameDomainRule Domains { get; }

    /// <summary>The rule the constructor was given, or null when it was given none.</summary>
    internal FrameDomainRule? DeclaredDomains { get; }

    /// <summary>
    /// Optional cleanup invoked each time the node's pump exits, whatever the reason (end of
    /// input, cancellation, a fault), after the last call to <see cref="Body"/> and once its
    /// outputs are complete. Once per run, so state that must survive a re-run of the graph is
    /// created again on the next run's first item rather than once at construction. For an
    /// operator that owns a native resource and releases it deterministically, as
    /// <see cref="SourceNode{TOut}.Cleanup"/> does for a source.
    /// </summary>
    public Func<ValueTask>? Cleanup { get; }

    public OperatorNode(
        string id,
        Operator<TIn, TOut> body,
        FailureResponse onError = FailureResponse.Propagate,
        FrameHolding? holding = null,
        FrameDomainRule? domains = null,
        Func<ValueTask>? cleanup = null
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        Id = id;
        Body = body;
        OnError = onError;
        Holding = holding ?? FrameHolding.Unbounded;
        DeclaredDomains = domains;
        Domains = domains ?? FrameDomainRule.Any;
        Cleanup = cleanup;
        Input = new InputPort<TIn>(this, "input");
        Output = new OutputPort<TOut>(this, "output");
    }

    FrameHolding IDeclaresHolding.HoldingAt(IPort input) => Holding;

    FrameDomainRule? IDeclaresDomains.DomainsAt(IPort input) => DeclaredDomains;

    Task IPumpableNode.RunPumpAsync(GraphRun run) =>
        NodePumps.PumpOperatorAsync(this, run);
}

// ─────────────────────────────────────────────────────────────────
// Multi-output operator: 1 input, 0..N outputs (per item)
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// 1→N operator node. The operator body is an async iterator that
/// yields zero or more output items per input. Replaces the
/// historic Channel-bridge boilerplate consumers had to write for
/// 1→N expansion.
/// </summary>
public sealed class MultiOperatorNode<TIn, TOut> : IPumpableNode, IDeclaresHolding, IDeclaresDomains
    where TIn : class, IRefCounted
    where TOut : class, IRefCounted
{
    public string Id { get; }
    public FailureResponse OnError { get; }
    public MultiOperator<TIn, TOut> Body { get; }
    public InputPort<TIn> Input { get; }
    public OutputPort<TOut> Output { get; }

    /// <summary>
    /// What the node holds of its input, and whether its output can share the input's storage
    /// (ADR-0081). <see cref="FrameHolding.Unbounded"/> when the constructor was given none.
    /// </summary>
    public FrameHolding Holding { get; }

    /// <summary>
    /// The memory domains the node accepts and emits (#435). <see cref="FrameDomainRule.Any"/>
    /// when the constructor was given none.
    /// </summary>
    public FrameDomainRule Domains { get; }

    /// <summary>The rule the constructor was given, or null when it was given none.</summary>
    internal FrameDomainRule? DeclaredDomains { get; }

    /// <summary>
    /// Optional cleanup invoked each time the node's pump exits, whatever the reason (end of
    /// input, cancellation, a fault), after the last call to <see cref="Body"/> and once its
    /// outputs are complete. Once per run, so state that must survive a re-run of the graph is
    /// created again on the next run's first item rather than once at construction. For an
    /// operator that owns a native resource and releases it deterministically, as
    /// <see cref="SourceNode{TOut}.Cleanup"/> does for a source.
    /// </summary>
    public Func<ValueTask>? Cleanup { get; }

    public MultiOperatorNode(
        string id,
        MultiOperator<TIn, TOut> body,
        FailureResponse onError = FailureResponse.Propagate,
        FrameHolding? holding = null,
        FrameDomainRule? domains = null,
        Func<ValueTask>? cleanup = null
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        Id = id;
        Body = body;
        OnError = onError;
        Holding = holding ?? FrameHolding.Unbounded;
        DeclaredDomains = domains;
        Domains = domains ?? FrameDomainRule.Any;
        Cleanup = cleanup;
        Input = new InputPort<TIn>(this, "input");
        Output = new OutputPort<TOut>(this, "output");
    }

    FrameHolding IDeclaresHolding.HoldingAt(IPort input) => Holding;

    FrameDomainRule? IDeclaresDomains.DomainsAt(IPort input) => DeclaredDomains;

    Task IPumpableNode.RunPumpAsync(GraphRun run) =>
        NodePumps.PumpMultiOperatorAsync(this, run);
}

// ─────────────────────────────────────────────────────────────────
// Sink: 1 input, 0 output
// ─────────────────────────────────────────────────────────────────

/// <summary>A sink node: receives items, produces side effects, no output.</summary>
public sealed class SinkNode<TIn> : IPumpableNode, IDeclaresHolding, IDeclaresDomains
    where TIn : class, IRefCounted
{
    public string Id { get; }
    public FailureResponse OnError { get; }
    public Consumer<TIn> Body { get; }
    public InputPort<TIn> Input { get; }

    /// <summary>
    /// What the node holds of its input, counting what the body keeps after it returns
    /// (ADR-0081). <see cref="FrameHolding.Unbounded"/> when the constructor was given none.
    /// </summary>
    public FrameHolding Holding { get; }

    /// <summary>
    /// The memory domains the sink accepts (#435). <see cref="FrameDomainRule.Any"/> when the
    /// constructor was given none.
    /// </summary>
    public FrameDomainRule Domains { get; }

    /// <summary>The rule the constructor was given, or null when it was given none.</summary>
    internal FrameDomainRule? DeclaredDomains { get; }

    public SinkNode(
        string id,
        Consumer<TIn> body,
        FailureResponse onError = FailureResponse.Propagate,
        FrameHolding? holding = null,
        FrameDomainRule? domains = null
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        Id = id;
        Body = body;
        OnError = onError;
        Holding = holding ?? FrameHolding.Unbounded;
        DeclaredDomains = domains;
        Domains = domains ?? FrameDomainRule.Any;
        Input = new InputPort<TIn>(this, "input");
    }

    FrameHolding IDeclaresHolding.HoldingAt(IPort input) => Holding;

    FrameDomainRule? IDeclaresDomains.DomainsAt(IPort input) => DeclaredDomains;

    Task IPumpableNode.RunPumpAsync(GraphRun run) =>
        NodePumps.PumpSinkAsync(this, run);
}
