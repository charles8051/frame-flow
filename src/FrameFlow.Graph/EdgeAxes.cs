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
/// verbatim from the Crossbar fork were never read by the runner; per
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

/// <summary>
/// Typed pairing of <see cref="EdgeOptions"/> (channel/back-pressure
/// configuration) with an optional per-edge cloner used at fan-out
/// (ADR-0054). Built via <see cref="EdgeOptionsExtensions.WithCloner{T}"/>;
/// consumed by the typed <see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeConfig{T})"/>
/// overload.
/// </summary>
/// <remarks>
/// <para>
/// The cloner exists so that a multi-consumer fan-out from an output
/// whose item type doesn't support <see cref="IRefCounted.AddRef"/>
/// (most notably converter outputs producing one-shot
/// <c>Media.CpuVideoFrame</c>) can still be expressed as ordinary
/// multi-<see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeOptions?)"/>
/// calls. The substrate's fan-out path uses the cloner instead of
/// <c>AddRef</c> for that specific branch; branches without a cloner
/// keep the existing AddRef semantics. See ADR-0054 for the rationale
/// and ADR-0049 §2 for the broader fan-out direction this extends.
/// </para>
/// </remarks>
/// <typeparam name="T">The item type flowing across the edge.</typeparam>
/// <param name="Options">Underlying channel/back-pressure configuration.</param>
/// <param name="Cloner">
/// Optional per-branch cloner. When supplied, fan-out invokes
/// <c>Cloner(item)</c> to produce the per-branch item instead of
/// calling <c>item.AddRef()</c>.
/// </param>
public readonly record struct EdgeConfig<T>(EdgeOptions Options, Func<T, T>? Cloner)
    where T : class, IRefCounted;

/// <summary>
/// Fluent helpers that pair an <see cref="EdgeOptions"/> with a typed
/// cloner via <see cref="EdgeConfig{T}"/>. Lets callers write
/// <c>EdgeOptions.LatestWins().WithCloner(input =&gt; ...)</c> at the
/// wireup site (ADR-0054).
/// </summary>
public static class EdgeOptionsExtensions
{
    /// <summary>
    /// Pairs <paramref name="options"/> with an explicit per-branch
    /// <paramref name="cloner"/>. The resulting <see cref="EdgeConfig{T}"/>
    /// is consumed by the typed
    /// <see cref="Graph.Connect{T}(OutputPort{T}, InputPort{T}, EdgeConfig{T})"/>
    /// overload; fan-out uses <paramref name="cloner"/> instead of
    /// <c>AddRef</c> for that branch.
    /// </summary>
    /// <remarks>
    /// Use when the upstream output flows items whose
    /// <see cref="IRefCounted.AddRef"/> throws (one-shot frame types,
    /// notably converter outputs). The cloner produces an independent
    /// item for the branch — typically a deep CPU copy. The substrate
    /// applies the cloner only on this specific branch; siblings
    /// without a cloner continue to use <c>AddRef</c>.
    /// </remarks>
    public static EdgeConfig<T> WithCloner<T>(
        this EdgeOptions options,
        Func<T, T> cloner
    )
        where T : class, IRefCounted
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cloner);
        return new EdgeConfig<T>(options, cloner);
    }
}
