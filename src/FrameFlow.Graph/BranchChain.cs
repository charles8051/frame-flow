// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// A branch before its first hop, from <see cref="GraphChain{T}.Branch(EdgeOptions)"/>: a second
/// consumer of the chain's output whose first edge is already configured.
/// </summary>
/// <remarks>
/// The first hop takes no edge options, because <see cref="GraphChain{T}.Branch(EdgeOptions)"/>
/// configured it. It returns an ordinary <see cref="GraphChain{T}"/>, whose later hops take options
/// as usual.
/// </remarks>
public readonly struct BranchChain<T>
    where T : class, IRefCounted
{
    private readonly GraphChain<T> _trunk;
    private readonly EdgeOptions _options;

    internal BranchChain(GraphChain<T> trunk, EdgeOptions options)
    {
        _trunk = trunk;
        _options = options;
    }

    /// <summary>Chains through a 1→0..1 operator on the branch's edge.</summary>
    public GraphChain<TOut> Then<TOut>(OperatorNode<T, TOut> next)
        where TOut : class, IRefCounted => _trunk.Then(next, _options);

    /// <summary>Chains through a 1→N operator on the branch's edge.</summary>
    public GraphChain<TOut> Then<TOut>(MultiOperatorNode<T, TOut> next)
        where TOut : class, IRefCounted => _trunk.Then(next, _options);

    /// <summary>Terminates by wiring the branch's edge into a sink.</summary>
    public void To(SinkNode<T> sink) => _trunk.To(sink, _options);

    /// <summary>Terminates by wiring the branch's edge into a sync join's primary input.</summary>
    public void ToPrimary<TSecondary, TOut>(SyncJoinNode<T, TSecondary, TOut> join)
        where TSecondary : class, IRefCounted
        where TOut : class, IRefCounted => _trunk.ToPrimary(join, _options);

    /// <summary>
    /// Terminates by wiring the branch's edge into a sync join's secondary input. The edge needs
    /// the buffer <see cref="GraphChain{T}.ToSecondary"/> describes, so choose the branch's
    /// options for it.
    /// </summary>
    public void ToSecondary<TPrimary, TOut>(SyncJoinNode<TPrimary, T, TOut> join)
        where TPrimary : class, IRefCounted
        where TOut : class, IRefCounted => _trunk.ToSecondary(join, _options);
}
