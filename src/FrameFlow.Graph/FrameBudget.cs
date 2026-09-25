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
    /// (<see cref="FrameHolding.FramesPerOutputItem"/>). A node reached by paths that carry different
    /// numbers of frames per item, such as a join, is taken to emit the largest, whatever order
    /// the paths were wired in.
    /// </para>
    /// <para>
    /// Each input port is counted once, which is what a join fed from both sides of one fork
    /// holds: its primary's and its secondary's.
    /// </para>
    /// </remarks>
    public static FrameBudget For(IPort source, IReadOnlyList<EdgeSpec> edges)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edges);

        // First the frames each reached port carries per item, raised to the largest over every
        // path into it. An input port has one edge, so it carries what that edge's output does.
        var outputs = new Dictionary<IPort, int> { [source] = 1 };
        var inputs = new Dictionary<IPort, int>();
        var pending = new Queue<IPort>();
        pending.Enqueue(source);

        while (pending.Count > 0)
        {
            var output = pending.Dequeue();
            int framesPerItem = outputs[output];
            foreach (var edge in edges)
            {
                if (edge.From != output)
                    continue;
                if (inputs.TryGetValue(edge.To, out int seen) && seen >= framesPerItem)
                    continue;
                inputs[edge.To] = framesPerItem;

                var holding = HoldingAt(edge.To);
                if (holding.MaxHeld is null)
                    return FrameBudget.Unbounded(edge.To.Owner.Id);
                if (!holding.ForwardsStorage)
                    continue;

                int next = holding.FramesPerOutputItem ?? framesPerItem;
                foreach (var onward in edges)
                {
                    if (onward.From.Owner != edge.To.Owner)
                        continue;
                    if (outputs.TryGetValue(onward.From, out int current) && current >= next)
                        continue;
                    outputs[onward.From] = next;
                    pending.Enqueue(onward.From);
                }
            }
        }

        // Then the sum: the item the source's pump holds while it writes, each reached edge, and
        // each reached input.
        long total = 1;
        foreach (var edge in edges)
        {
            if (outputs.TryGetValue(edge.From, out int framesPerItem))
                total += (long)edge.Capacity * framesPerItem;
        }
        foreach (var (input, framesPerItem) in inputs)
            total += (long)HoldingAt(input).MaxHeld!.Value * framesPerItem;

        return FrameBudget.Of((int)Math.Min(total, int.MaxValue));
    }

    private static FrameHolding HoldingAt(IPort input) =>
        input.Owner is IDeclaresHolding declares ? declares.HoldingAt(input) : FrameHolding.Unbounded;
}
