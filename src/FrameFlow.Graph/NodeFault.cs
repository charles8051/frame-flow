// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>One node's fault in a run of a graph: the node's id, and what it threw.</summary>
public sealed class NodeFault
{
    /// <summary>The fault of the node <paramref name="nodeId"/>.</summary>
    /// <param name="nodeId">The <see cref="INode.Id"/> of the node that faulted.</param>
    /// <param name="exception">What the node threw.</param>
    public NodeFault(string nodeId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(exception);
        NodeId = nodeId;
        Exception = exception;
    }

    /// <summary>The <see cref="INode.Id"/> of the node that faulted.</summary>
    public string NodeId { get; }

    /// <summary>What the node threw.</summary>
    public Exception Exception { get; }
}
