// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// The most frames of one source's storage a graph can hold at once (ADR-0081, decision 3), or
/// the holder that leaves it unbounded.
/// </summary>
/// <remarks>
/// A fixed-pool source, a hardware decoder or a camera, sizes its pool from this, or copies
/// when the pool cannot be sized.
/// </remarks>
public sealed record FrameBudget
{
    private FrameBudget(int? frames, string? unboundedHolder)
    {
        Frames = frames;
        UnboundedHolder = unboundedHolder;
    }

    /// <summary>The most frames held at once, or <see langword="null"/> when unbounded.</summary>
    public int? Frames { get; }

    /// <summary>
    /// The id of the first node found on a path from the source that declares no bound, or
    /// <see langword="null"/> when the budget is bounded.
    /// </summary>
    public string? UnboundedHolder { get; }

    /// <summary>A budget of <paramref name="frames"/>.</summary>
    public static FrameBudget Of(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        return new FrameBudget(frames, null);
    }

    /// <summary>No budget, because <paramref name="holder"/> declares no bound.</summary>
    public static FrameBudget Unbounded(string holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return new FrameBudget(null, holder);
    }
}

/// <summary>
/// Computes a source's <see cref="FrameBudget"/> from the graph's wiring and its nodes'
/// declarations (ADR-0081, decision 3). A total function of its arguments.
/// </summary>
internal static class FrameBudgets
{
    /// <summary>
    /// The most frames the graph holds of what leaves <paramref name="source"/>: the item its
    /// pump is writing, every edge's capacity, and what each node on a path declares, up to the
    /// first storage boundary on each path. Each branch of a fan-out adds its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Edges and nodes count items. After a node that gathers frames into one item, such as a
    /// clip, each item counts the frames that node says it carries
    /// (<see cref="Holding.FramesPerOutputItem"/>).
    /// </para>
    /// <para>
    /// A node reached by two paths is counted once for each of its inputs, which is what a join
    /// fed from both sides of one fork holds.
    /// </para>
    /// </remarks>
    public static FrameBudget For(IPort source, IReadOnlyList<EdgeSpec> edges)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edges);

        long total = 1; // the item the source's pump holds while it writes
        var pending = new Queue<(IPort Output, int FramesPerItem)>();
        var outputsSeen = new HashSet<IPort> { source };
        var inputsCounted = new HashSet<IPort>();
        pending.Enqueue((source, 1));

        while (pending.Count > 0)
        {
            var (output, framesPerItem) = pending.Dequeue();
            foreach (var edge in edges)
            {
                if (edge.From != output)
                    continue;

                total += (long)edge.Capacity * framesPerItem;
                if (!inputsCounted.Add(edge.To))
                    continue;

                var node = edge.To.Owner;
                var holding = node is IDeclaresHolding declares
                    ? declares.HoldingAt(edge.To)
                    : Holding.Unbounded;
                if (holding.MaxHeld is not { } held)
                    return FrameBudget.Unbounded(node.Id);

                total += (long)held * framesPerItem;
                if (!holding.ForwardsStorage)
                    continue;

                int next = holding.FramesPerOutputItem ?? framesPerItem;
                foreach (var onward in edges)
                {
                    if (onward.From.Owner == node && outputsSeen.Add(onward.From))
                        pending.Enqueue((onward.From, next));
                }
            }
        }

        return FrameBudget.Of((int)Math.Min(total, int.MaxValue));
    }
}
