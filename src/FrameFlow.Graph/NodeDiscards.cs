// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// What one node has dropped under <see cref="FailureResponse.Discard"/>: how many inputs, and the
/// exception the last of them threw (#501).
/// </summary>
public sealed class NodeDiscards
{
    /// <summary>A count of <paramref name="count"/> discards by <paramref name="nodeId"/>.</summary>
    /// <param name="nodeId">The <see cref="INode.Id"/> of the node that discarded.</param>
    /// <param name="count">How many inputs it discarded.</param>
    /// <param name="lastException">What its body threw on the last of them.</param>
    public NodeDiscards(string nodeId, long count, Exception lastException)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentNullException.ThrowIfNull(lastException);
        NodeId = nodeId;
        Count = count;
        LastException = lastException;
    }

    /// <summary>The <see cref="INode.Id"/> of the node that discarded.</summary>
    public string NodeId { get; }

    /// <summary>How many inputs the node has discarded.</summary>
    public long Count { get; }

    /// <summary>What the node's body threw on the last input it discarded.</summary>
    public Exception LastException { get; }
}

/// <summary>
/// One node's discards in one graph, counted by its pump and read by <see cref="Graph.Discards"/>
/// from any thread.
/// </summary>
internal sealed class DiscardTally(string nodeId)
{
    private readonly object _gate = new();
    private long _count;
    private Exception? _last;

    /// <summary>Counts one discarded input, whose body threw <paramref name="ex"/>.</summary>
    public void Record(Exception ex)
    {
        lock (_gate)
        {
            _count++;
            _last = ex;
        }
    }

    /// <summary>
    /// The count and the last exception, read together, or <see langword="null"/> when the node has
    /// discarded nothing.
    /// </summary>
    public NodeDiscards? Snapshot()
    {
        lock (_gate)
            return _last is null ? null : new NodeDiscards(nodeId, _count, _last);
    }
}
