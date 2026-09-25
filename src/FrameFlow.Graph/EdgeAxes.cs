// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// What happens when the buffer fills and the producer wants to deliver.
/// </summary>
public enum Overflow
{
    /// <summary>Producer's write blocks until the consumer makes space.</summary>
    Block,

    /// <summary>Incoming items are dropped (and disposed) when the buffer is full.</summary>
    DropIncoming,

    /// <summary>Oldest queued items are evicted (and disposed) to make room for new ones.</summary>
    DropOldest,
}

/// <summary>
/// Configuration of a single edge between two nodes. The two axes the
/// graph runner reads — buffer <see cref="Capacity"/> and the
/// <see cref="Overflow"/> policy — together specify the edge's channel
/// behaviour; backpressure is the emergent property of an
/// <see cref="Overflow.Block"/> edge rather than a declared axis.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backpressure</b> exists iff <c>Overflow == Block</c>: a full
/// buffer blocks the producer's write, so downstream slowness reaches
/// upstream. With a dropping overflow policy the drops absorb the rate
/// mismatch and upstream runs free.
/// </para>
/// <para>
/// The historic <c>Shape</c> (push/pull), <c>Cadence</c>
/// (producer/consumer-paced), and <c>Underflow</c> axes carried over
/// verbatim at the fork were never read by the runner; per
/// ADR-0049 §2 they were dropped once the consumer's actual needs were
/// observable.
/// </para>
/// </remarks>
public sealed record EdgeOptions(
    int Capacity = 1,
    Overflow Overflow = Overflow.Block
)
{
    public static EdgeOptions Default { get; } = new();

    /// <summary>
    /// Buffered edge: capacity-<paramref name="capacity"/>, blocks on overflow.
    /// The producer may run ahead of the consumer up to the buffer depth, then
    /// backpressures — lossless. The deep-buffer, blocking counterpart to
    /// <see cref="LatestWins(int)"/>.
    /// </summary>
    public static EdgeOptions Buffered(int capacity) =>
        new(Capacity: capacity, Overflow: Overflow.Block);

    /// <summary>
    /// Latest-wins style: consumer always reads the freshest item;
    /// older items in the buffer get evicted. Producer never blocks.
    /// </summary>
    public static EdgeOptions LatestWins(int capacity = 1) =>
        new(Capacity: capacity, Overflow: Overflow.DropOldest);
}
