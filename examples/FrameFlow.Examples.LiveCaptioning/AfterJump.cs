using FrameFlow.Graph;

namespace FrameFlow.Examples.LiveCaptioning;

/// <summary>
/// An item of the caption graph with the jump its audio was played after. The window counts a
/// jump as each run of the player's graph with a picture starts, which is once for each load,
/// seek, loop and file, so a caption whose jump is not the one on screen is for audio from
/// before a jump.
/// </summary>
/// <remarks>
/// It counts no references of its own: adding or releasing one adds or releases one on
/// <see cref="Item"/>.
/// </remarks>
internal sealed class AfterJump<T>(int jump, T item) : IRefCounted
    where T : class, IRefCounted
{
    public int Jump { get; } = jump;

    public T Item { get; } = item;

    public IRefCounted AddRef()
    {
        Item.AddRef();
        return this;
    }

    public void Dispose() => Item.Dispose();
}

/// <summary>
/// Runs the library's nodes on <see cref="AfterJump{T}"/> items, so each caption keeps the jump
/// of the audio it was transcribed from.
/// </summary>
/// <remarks>
/// A node's memory-domain rule is not carried over, which suits the caption graph: its nodes
/// declare none.
/// </remarks>
internal static class AfterJump
{
    /// <summary>Runs <paramref name="node"/> on each item; its output keeps the item's jump.</summary>
    public static OperatorNode<AfterJump<TIn>, AfterJump<TOut>> KeepJump<TIn, TOut>(
        OperatorNode<TIn, TOut> node
    )
        where TIn : class, IRefCounted
        where TOut : class, IRefCounted =>
        new(
            node.Id,
            async (input, ct) =>
            {
                var output = await node.Body(input.Item, ct);
                if (output is null)
                    return null;

                // An operator that returns its input hands on the reference the graph holds, and
                // the graph still releases that one with the item, so the output takes its own.
                if (ReferenceEquals(output, input.Item))
                    output.AddRef();
                return new AfterJump<TOut>(input.Jump, output);
            },
            node.OnError,
            node.Holding,
            cleanup: node.Cleanup
        );

    /// <summary>Runs <paramref name="node"/> on each item; every output keeps the item's jump.</summary>
    public static MultiOperatorNode<AfterJump<TIn>, AfterJump<TOut>> KeepJump<TIn, TOut>(
        MultiOperatorNode<TIn, TOut> node
    )
        where TIn : class, IRefCounted
        where TOut : class, IRefCounted =>
        new(
            node.Id,
            (input, ct) => WithJump(node.Body(input.Item, ct), input.Jump),
            node.OnError,
            node.Holding,
            cleanup: node.Cleanup
        );

    private static async IAsyncEnumerable<AfterJump<T>> WithJump<T>(IAsyncEnumerable<T> outputs, int jump)
        where T : class, IRefCounted
    {
        await foreach (var output in outputs)
            yield return new AfterJump<T>(jump, output);
    }
}
