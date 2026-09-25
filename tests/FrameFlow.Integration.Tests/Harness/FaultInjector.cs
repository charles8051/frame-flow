using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Integration.Tests.Harness;

/// <summary>
/// Supplies a player's video configurator. Chains are numbered in the order they are built, and the
/// chains <c>breaks</c> selects throw on their <see cref="FaultFrame"/>th frame.
/// </summary>
/// <remarks>
/// An item gets a new chain each time it is built, and a faulted item is always rebuilt rather than
/// rewound, so breaking every chain breaks every pass.
/// </remarks>
internal sealed class FaultInjector(Func<int, bool> breaks)
{
    /// <summary>The frame each broken chain throws on.</summary>
    public const int FaultFrame = 21;

    private int _chains;

    /// <summary>How many chains have been built.</summary>
    public int Chains => Volatile.Read(ref _chains);

    public GraphChain<IVideoFrame> Configure(GraphChain<IVideoFrame> chain)
    {
        var index = Interlocked.Increment(ref _chains) - 1;
        if (!breaks(index))
            return chain;

        var frames = 0;
        return chain.Then(
            new OperatorNode<IVideoFrame, IVideoFrame>(
                "inject-fault",
                (frame, _) =>
                    ++frames == FaultFrame
                        ? throw new InjectedFault(index)
                        : ValueTask.FromResult<IVideoFrame?>(frame)
            )
        );
    }
}

/// <summary>The fault <see cref="FaultInjector"/> throws.</summary>
internal sealed class InjectedFault(int chain) : Exception($"Injected fault in chain {chain}.")
{
    /// <summary>Whether the error's exception chain contains an injected fault.</summary>
    public static bool Caused(PlaybackError error)
    {
        var pending = new Stack<Exception>();
        if (error.Inner is { } inner)
            pending.Push(inner);
        while (pending.TryPop(out var ex))
        {
            if (ex is InjectedFault)
                return true;
            if (ex is AggregateException aggregate)
            {
                foreach (var child in aggregate.InnerExceptions)
                    pending.Push(child);
            }
            else if (ex.InnerException is { } next)
            {
                pending.Push(next);
            }
        }
        return false;
    }
}
