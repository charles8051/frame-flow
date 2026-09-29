// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// What the pumps of one run of a graph share: the token that stops them all, the faults that
/// ended the run, and the graph's tallies of what each node discarded.
/// </summary>
/// <remarks>
/// <para>
/// A pump reports its fault here rather than through its task. A pump is an <c>async</c> method,
/// and one that ends by throwing an <see cref="OperationCanceledException"/> ends Canceled, not
/// Faulted, so a body's own <see cref="TaskCanceledException"/> would otherwise read as the run
/// being cancelled (#489).
/// </para>
/// <para>
/// Recording a fault cancels the run, so the other pumps stop producing before the faulting one
/// drains its inputs. The run keeps the first fault recorded by any node in a slot written once
/// (#499), and each node keeps the first it records. A pump records a fault in the catch that
/// caught it, so the first recorded is the first a pump caught. Two pumps that fault at once are
/// recorded in whichever order they win the run's slot, which need not be the order their bodies
/// threw.
/// </para>
/// </remarks>
internal sealed class GraphRun : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<INode, int> _indexOf;
    private readonly NodeFault?[] _faultOf;
    private readonly IReadOnlyList<DiscardTally> _discardsOf;
    private NodeFault? _firstFault;

    /// <summary>
    /// A run of <paramref name="nodes"/> that is also cancelled when <paramref name="callerToken"/>
    /// is, counting each node's discards in the tally at the same index of
    /// <paramref name="discards"/>.
    /// </summary>
    public GraphRun(
        IReadOnlyList<INode> nodes,
        IReadOnlyList<DiscardTally> discards,
        CancellationToken callerToken
    )
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(discards);
        if (discards.Count != nodes.Count)
            throw new ArgumentException("Every node needs one discard tally.", nameof(discards));

        _indexOf = new Dictionary<INode, int>(nodes.Count, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < nodes.Count; i++)
            _indexOf.Add(nodes[i], i);
        _faultOf = new NodeFault?[nodes.Count];
        _discardsOf = discards;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
    }

    /// <summary>Cancelled when the caller cancels or a pump faults.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Whether any node has recorded a fault.</summary>
    public bool HasFaulted => Volatile.Read(ref _firstFault) is not null;

    /// <summary>
    /// Every node's fault, the first recorded at index 0 and the rest in node order, or an empty
    /// list when none was. Read once the pumps have ended.
    /// </summary>
    public IReadOnlyList<NodeFault> Faults => FaultRules.Ordered(Volatile.Read(ref _firstFault), _faultOf);

    /// <summary>Records <paramref name="ex"/> as the fault of <paramref name="node"/> and stops the run.</summary>
    /// <remarks>
    /// For a caller that has already decided <paramref name="ex"/> is a fault, such as a body's
    /// catch site under <see cref="FailureResponse.Propagate"/>. A node that has recorded a fault
    /// keeps it, so recording the same exception again, from the pump's outer catch, changes
    /// nothing.
    /// </remarks>
    public void Fault(INode node, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(ex);
        var fault = new NodeFault(node.Id, ex);

        // The run's slot first: winning it is what being recorded first means, so no pump can
        // publish its node's slot and then lose the cause to one that came after it.
        Interlocked.CompareExchange(ref _firstFault, fault, null);
        Interlocked.CompareExchange(ref _faultOf[_indexOf[node]], fault, null);
        Cancel();
    }

    /// <summary>
    /// Counts an input <paramref name="node"/> dropped under <see cref="FailureResponse.Discard"/>
    /// after its body threw <paramref name="ex"/>, in the graph's tally and on the
    /// <c>frameflow.graph.discards</c> metric (#501). The run carries on.
    /// </summary>
    public void Discarded(INode node, Exception ex)
    {
        _discardsOf[_indexOf[node]].Record(ex);
        GraphMetrics.Discards.Add(1, new KeyValuePair<string, object?>("node", node.Id));
    }

    /// <summary>
    /// Records <paramref name="ex"/>, which ended <paramref name="node"/>'s pump, as its fault and
    /// stops the run, unless it is the run's cancellation.
    /// </summary>
    public void Ended(INode node, Exception ex)
    {
        if (!FaultRules.IsCancellation(ex, Token.IsCancellationRequested))
            Fault(node, ex);
    }

    private void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Disposed once the run is over, when nothing is left to stop, or a callback
            // registered on the token threw. Neither may replace the fault being recorded.
        }
    }

    public void Dispose() => _cts.Dispose();
}
