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
/// Pumps receive the *graph CTS itself*, not just the token, so they
/// can trigger cancellation of sibling pumps from inside their own
/// finally blocks. This lets a pump that's exiting via exception
/// signal upstream sources to stop producing *before* the pump
/// async-drains its input channel — otherwise upstream items written
/// after the pump's main loop exits would have nowhere to go.
/// </remarks>
internal interface IPumpableNode : INode
{
    Task RunPumpAsync(CancellationTokenSource graphCts);
}

/// <summary>
/// A node that has state to drop before each run of its graph. The runner calls
/// <see cref="ResetForRun"/> on every such node before it starts any pump, including on the
/// first run.
/// </summary>
/// <remarks>
/// A graph is re-runnable: a loop rewinds its item and runs the same graph again
/// (<c>GraphPolicy.Reuse</c>), so a node that carries state from one run into the next sees
/// timestamps go back with nothing to tell it why. This is that telling.
/// </remarks>
internal interface IResettableNode : INode
{
    void ResetForRun();
}

// ─────────────────────────────────────────────────────────────────
// Source: 0 input, 1 output
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// A source node: produces items via a <see cref="Producer{TOut}"/>
/// function until it returns null (end of stream).
/// </summary>
public sealed class SourceNode<TOut> : IPumpableNode, IResettableNode
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
    /// Optional callback the graph invokes before each run, including the first, for state this
    /// node keeps between items rather than inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A graph is re-runnable, and a loop rewinds its item and runs the same graph again rather
    /// than building a new one, so a node's closure survives the loop while its timestamps go
    /// back to zero. Anything remembered across items — the last timestamp seen, a window of
    /// frames, a running total, an enumerator's position — is cleared here.
    /// </para>
    /// <para>
    /// It runs on the thread that starts the graph, before any pump, so it needs no lock against
    /// the body. It must not throw: a reset that throws fails the run before it starts.
    /// </para>
    /// </remarks>
    public Action? OnReset { get; init; }

    public SourceNode(
        string id,
        Producer<TOut> body,
        FailureResponse onError = FailureResponse.Propagate,
        Func<ValueTask>? cleanup = null
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        Id = id;
        Body = body;
        OnError = onError;
        Cleanup = cleanup;
        Output = new OutputPort<TOut>(this, "output");
    }

    Task IPumpableNode.RunPumpAsync(CancellationTokenSource graphCts) =>
        NodePumps.PumpSourceAsync(this, graphCts);

    void IResettableNode.ResetForRun() => OnReset?.Invoke();
}

// ─────────────────────────────────────────────────────────────────
// Linear operator: 1 input, 0..1 output (per item)
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// 1→0..1 operator node. Receives items, transforms them, forwards
/// outputs. The operator function may return null to drop the input
/// without producing an output.
/// </summary>
public sealed class OperatorNode<TIn, TOut> : IPumpableNode, IResettableNode
    where TIn : class, IRefCounted
    where TOut : class, IRefCounted
{
    public string Id { get; }
    public FailureResponse OnError { get; }
    public Operator<TIn, TOut> Body { get; }
    public InputPort<TIn> Input { get; }
    public OutputPort<TOut> Output { get; }

    /// <summary>
    /// Optional callback the graph invokes before each run, including the first, for state this
    /// node keeps between items rather than inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A graph is re-runnable, and a loop rewinds its item and runs the same graph again rather
    /// than building a new one, so a node's closure survives the loop while its timestamps go
    /// back to zero. Anything remembered across items — the last timestamp seen, a window of
    /// frames, a running total, an enumerator's position — is cleared here.
    /// </para>
    /// <para>
    /// It runs on the thread that starts the graph, before any pump, so it needs no lock against
    /// the body. It must not throw: a reset that throws fails the run before it starts.
    /// </para>
    /// </remarks>
    public Action? OnReset { get; init; }

    public OperatorNode(
        string id,
        Operator<TIn, TOut> body,
        FailureResponse onError = FailureResponse.Propagate
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        Id = id;
        Body = body;
        OnError = onError;
        Input = new InputPort<TIn>(this, "input");
        Output = new OutputPort<TOut>(this, "output");
    }

    Task IPumpableNode.RunPumpAsync(CancellationTokenSource graphCts) =>
        NodePumps.PumpOperatorAsync(this, graphCts);

    void IResettableNode.ResetForRun() => OnReset?.Invoke();
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
public sealed class MultiOperatorNode<TIn, TOut> : IPumpableNode, IResettableNode
    where TIn : class, IRefCounted
    where TOut : class, IRefCounted
{
    public string Id { get; }
    public FailureResponse OnError { get; }
    public MultiOperator<TIn, TOut> Body { get; }
    public InputPort<TIn> Input { get; }
    public OutputPort<TOut> Output { get; }

    /// <summary>
    /// Optional callback the graph invokes before each run, including the first, for state this
    /// node keeps between items rather than inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A graph is re-runnable, and a loop rewinds its item and runs the same graph again rather
    /// than building a new one, so a node's closure survives the loop while its timestamps go
    /// back to zero. Anything remembered across items — the last timestamp seen, a window of
    /// frames, a running total, an enumerator's position — is cleared here.
    /// </para>
    /// <para>
    /// It runs on the thread that starts the graph, before any pump, so it needs no lock against
    /// the body. It must not throw: a reset that throws fails the run before it starts.
    /// </para>
    /// </remarks>
    public Action? OnReset { get; init; }

    public MultiOperatorNode(
        string id,
        MultiOperator<TIn, TOut> body,
        FailureResponse onError = FailureResponse.Propagate
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        Id = id;
        Body = body;
        OnError = onError;
        Input = new InputPort<TIn>(this, "input");
        Output = new OutputPort<TOut>(this, "output");
    }

    Task IPumpableNode.RunPumpAsync(CancellationTokenSource graphCts) =>
        NodePumps.PumpMultiOperatorAsync(this, graphCts);

    void IResettableNode.ResetForRun() => OnReset?.Invoke();
}

// ─────────────────────────────────────────────────────────────────
// Sink: 1 input, 0 output
// ─────────────────────────────────────────────────────────────────

/// <summary>A sink node: receives items, produces side effects, no output.</summary>
public sealed class SinkNode<TIn> : IPumpableNode, IResettableNode
    where TIn : class, IRefCounted
{
    public string Id { get; }
    public FailureResponse OnError { get; }
    public Consumer<TIn> Body { get; }
    public InputPort<TIn> Input { get; }

    /// <summary>
    /// Optional callback the graph invokes before each run, including the first, for state this
    /// node keeps between items rather than inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A graph is re-runnable, and a loop rewinds its item and runs the same graph again rather
    /// than building a new one, so a node's closure survives the loop while its timestamps go
    /// back to zero. Anything remembered across items — the last timestamp seen, a window of
    /// frames, a running total, an enumerator's position — is cleared here.
    /// </para>
    /// <para>
    /// It runs on the thread that starts the graph, before any pump, so it needs no lock against
    /// the body. It must not throw: a reset that throws fails the run before it starts.
    /// </para>
    /// </remarks>
    public Action? OnReset { get; init; }

    public SinkNode(
        string id,
        Consumer<TIn> body,
        FailureResponse onError = FailureResponse.Propagate
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        Id = id;
        Body = body;
        OnError = onError;
        Input = new InputPort<TIn>(this, "input");
    }

    Task IPumpableNode.RunPumpAsync(CancellationTokenSource graphCts) =>
        NodePumps.PumpSinkAsync(this, graphCts);

    void IResettableNode.ResetForRun() => OnReset?.Invoke();
}
