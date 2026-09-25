using FrameFlow.Tests.Shared;
using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type
// (FrameFlow.Graph.Graph). Alias the type so the tests can sit in the
// conventional FrameFlow.Graph.Tests namespace without the clash.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// Ownership / refcount tests for the fan-out path (<c>NodePumps.ForwardAsync</c>). Every branch
/// shares the one item by <c>AddRef</c> (ADR-0080), so each shape must balance the count to zero,
/// including the path where an <c>AddRef</c> throws.
/// </summary>
/// <remarks>
/// The over-release counter is process-wide, so this runs in the non-parallel ref-counting
/// collection. Each pump terminates before <see cref="GraphRunner.RunAsync"/> returns
/// (or throws), so asserting <see cref="RefBox{T}.RefCount"/> immediately
/// afterward observes the fully-settled state — no polling needed.
/// </remarks>
[Collection(RefCountingCollection.Name)]
public sealed class ForwardAsyncFanOutTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// A source that hands the substrate <paramref name="items"/> in order,
    /// one per pull, then EOS. The source pump is single-threaded, so the
    /// plain index needs no synchronization.
    /// </summary>
    private static SourceNode<T> Emit<T>(params T[] items)
        where T : class, IRefCounted
    {
        int i = 0;
        return new SourceNode<T>(
            "src",
            _ => ValueTask.FromResult<T?>(i < items.Length ? items[i++] : null)
        );
    }

    /// <summary>A do-nothing sink; the substrate disposes each item it consumes.</summary>
    private static SinkNode<T> NullSink<T>(string id)
        where T : class, IRefCounted =>
        new(id, (_, _) => ValueTask.CompletedTask);

    // ── Success paths ───────────────────────────────────────────────────

    [Fact]
    public async Task NoConsumers_DisposesIncomingRef()
    {
        var box = RefBox.Of(1);
        var src = Emit(box);

        var graph = new GraphRunner();
        graph.Add(src); // no Connect → Output.Writers is empty

        await graph.RunAsync();

        Assert.Equal(0, box.RefCount);
    }

    [Fact]
    public async Task SingleConsumer_TakesTheIncomingRef()
    {
        var box = RefBox.Of(1);
        var src = Emit(box);
        var sink = NullSink<RefBox<int>>("sink");

        var graph = new GraphRunner();
        graph.Connect(src.Output, sink.Input);

        await graph.RunAsync();

        Assert.Equal(0, box.RefCount);
    }

    [Fact]
    public async Task ThreeConsumers_ShareTheItem_AllReleased()
    {
        long overReleasesBefore = RefCounting.OverReleases;
        var box = RefBox.Of(1);
        var src = Emit(box);
        var seen = new List<RefBox<int>>();
        SinkNode<RefBox<int>> Recording(string id) =>
            new(
                id,
                (item, _) =>
                {
                    lock (seen)
                        seen.Add(item);
                    return ValueTask.CompletedTask;
                }
            );

        var graph = new GraphRunner();
        graph.Connect(src.Output, Recording("a").Input);
        graph.Connect(src.Output, Recording("b").Input);
        graph.Connect(src.Output, Recording("c").Input);

        await graph.RunAsync();

        Assert.Equal(3, seen.Count);
        Assert.All(seen, item => Assert.Same(box, item));
        Assert.Equal(0, box.RefCount);
        Assert.Equal(overReleasesBefore, RefCounting.OverReleases);
    }

    // ── Error path ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnAddRefThatThrows_ReleasesTheRefsTaken_AndTheIncomingOne()
    {
        // The second AddRef throws, so one extra ref has been taken and nothing written. The
        // pump releases that ref and the incoming one before the fault propagates.
        var item = new FailingSecondAddRef();
        var src = Emit(item);

        var graph = new GraphRunner();
        graph.Connect(src.Output, NullSink<FailingSecondAddRef>("a").Input);
        graph.Connect(src.Output, NullSink<FailingSecondAddRef>("b").Input);
        graph.Connect(src.Output, NullSink<FailingSecondAddRef>("c").Input);

        await Assert.ThrowsAsync<InvalidOperationException>(() => graph.RunAsync());

        Assert.Equal(0, item.RefCount);
    }

    /// <summary>An item whose second <c>AddRef</c> throws, after counting its first.</summary>
    private sealed class FailingSecondAddRef : IRefCounted
    {
        private int _refCount = 1;
        private int _addRefs;

        public int RefCount => Volatile.Read(ref _refCount);

        public IRefCounted AddRef()
        {
            if (++_addRefs == 2)
                throw new InvalidOperationException("second AddRef fails");
            Interlocked.Increment(ref _refCount);
            return this;
        }

        public void Dispose() => Interlocked.Decrement(ref _refCount);
    }
}
