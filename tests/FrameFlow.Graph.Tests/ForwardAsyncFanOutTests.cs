using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type
// (FrameFlow.Graph.Graph). Alias the type so the tests can sit in the
// conventional FrameFlow.Graph.Tests namespace without the clash.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// Ownership / refcount tests for the fan-out path
/// (<c>NodePumps.ForwardAsync</c>) extended by ADR-0054's per-edge cloner.
/// Every flowing item is a <see cref="RefBox{T}"/> so the test can assert
/// the substrate balances refcounts to zero on every distribution shape —
/// the inherit branch, AddRef siblings, cloner branches, the all-cloner
/// case, and the cloner-throws error path.
/// </summary>
/// <remarks>
/// Each pump terminates before <see cref="GraphRunner.RunAsync"/> returns
/// (or throws), so asserting <see cref="RefBox{T}.RefCount"/> immediately
/// afterward observes the fully-settled state — no polling needed.
/// </remarks>
public sealed class ForwardAsyncFanOutTests
{
    /// <summary>Sentinel thrown by a misbehaving cloner; keeps the propagated fault unambiguous.</summary>
    private sealed class ClonerBoomException : Exception { }

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

    /// <summary>
    /// A cloner edge that deep-copies the boxed value into a fresh
    /// <see cref="RefBox{T}"/> and records it, so the test can assert the
    /// clone is also disposed. Mirrors the MotionClip preview wireup shape
    /// (<c>LatestWins().WithCloner(...)</c>).
    /// </summary>
    private static EdgeConfig<RefBox<int>> CloneInto(List<RefBox<int>> recorded) =>
        EdgeOptions
            .LatestWins()
            .WithCloner<RefBox<int>>(src =>
            {
                var clone = RefBox.Of(src.Value);
                recorded.Add(clone);
                return clone;
            });

    /// <summary>A cloner edge whose cloner always throws.</summary>
    private static EdgeConfig<RefBox<int>> ThrowingCloner() =>
        EdgeOptions
            .LatestWins()
            .WithCloner<RefBox<int>>(_ => throw new ClonerBoomException());

    // ── Success-path matrix ─────────────────────────────────────────────

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
    public async Task SingleConsumer_NoCloner_InheritsAndDisposes()
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
    public async Task SingleConsumer_WithCloner_DisposesOriginalAndClone()
    {
        var box = RefBox.Of(7);
        var clones = new List<RefBox<int>>();
        var src = Emit(box);
        var sink = NullSink<RefBox<int>>("sink");

        var graph = new GraphRunner();
        graph.Connect(src.Output, sink.Input, CloneInto(clones));

        await graph.RunAsync();

        Assert.Equal(0, box.RefCount); // incoming ref had no inheritor → released
        var clone = Assert.Single(clones);
        Assert.Equal(0, clone.RefCount); // clone consumed + disposed by the sink
    }

    [Fact]
    public async Task TwoConsumers_NoCloner_AddRefFanOut_AllDisposed()
    {
        var box = RefBox.Of(1);
        var src = Emit(box);
        var sinkA = NullSink<RefBox<int>>("a");
        var sinkB = NullSink<RefBox<int>>("b");

        var graph = new GraphRunner();
        graph.Connect(src.Output, sinkA.Input); // inherits
        graph.Connect(src.Output, sinkB.Input); // AddRef

        await graph.RunAsync();

        Assert.Equal(0, box.RefCount);
    }

    [Fact]
    public async Task MixedInheritAndCloner_AllDisposed()
    {
        var box = RefBox.Of(3);
        var clones = new List<RefBox<int>>();
        var src = Emit(box);
        var gate = NullSink<RefBox<int>>("gate");
        var preview = NullSink<RefBox<int>>("preview");

        var graph = new GraphRunner();
        graph.Connect(src.Output, gate.Input); // cloner-less → inherits
        graph.Connect(src.Output, preview.Input, CloneInto(clones)); // cloned sibling

        await graph.RunAsync();

        Assert.Equal(0, box.RefCount);
        Assert.Equal(0, Assert.Single(clones).RefCount);
    }

    [Fact]
    public async Task AllCloner_TwoBranches_DisposesIncomingAndAllClones()
    {
        var box = RefBox.Of(9);
        var clones = new List<RefBox<int>>();
        var src = Emit(box);
        var sinkA = NullSink<RefBox<int>>("a");
        var sinkB = NullSink<RefBox<int>>("b");

        var graph = new GraphRunner();
        graph.Connect(src.Output, sinkA.Input, CloneInto(clones));
        graph.Connect(src.Output, sinkB.Input, CloneInto(clones));

        await graph.RunAsync();

        Assert.Equal(0, box.RefCount); // nobody inherited → released
        Assert.Equal(2, clones.Count);
        Assert.All(clones, c => Assert.Equal(0, c.RefCount));
    }

    // ── Error path: cloner throws (regression for the ADR-0054 leak) ─────

    [Fact]
    public async Task ThrowingCloner_WithInheritingBranch_DisposesIncomingRef()
    {
        // gate inherits (branch 0), preview's cloner throws (branch 1).
        // Pre-fix: the incoming ref was skipped in the catch and leaked.
        var box = RefBox.Of(1);
        var src = Emit(box);
        var gate = NullSink<RefBox<int>>("gate");
        var preview = NullSink<RefBox<int>>("preview");

        var graph = new GraphRunner();
        graph.Connect(src.Output, gate.Input);
        graph.Connect(src.Output, preview.Input, ThrowingCloner());

        await Assert.ThrowsAsync<ClonerBoomException>(() => graph.RunAsync());

        Assert.Equal(0, box.RefCount);
    }

    [Fact]
    public async Task ThrowingCloner_WithAddRefSibling_DisposesAllRefs()
    {
        // inherit (0) + AddRef sibling (1) + throwing cloner (2). Because
        // RefBox.AddRef() returns `this`, the AddRef'd slot is reference-
        // equal to the incoming ref; pre-fix the catch skipped both via
        // !ReferenceEquals and leaked two refs. The box must still reach 0.
        var box = RefBox.Of(1);
        var src = Emit(box);
        var a = NullSink<RefBox<int>>("a");
        var b = NullSink<RefBox<int>>("b");
        var c = NullSink<RefBox<int>>("c");

        var graph = new GraphRunner();
        graph.Connect(src.Output, a.Input); // inherits
        graph.Connect(src.Output, b.Input); // AddRef (returns `this`)
        graph.Connect(src.Output, c.Input, ThrowingCloner());

        await Assert.ThrowsAsync<ClonerBoomException>(() => graph.RunAsync());

        Assert.Equal(0, box.RefCount);
    }

    [Fact]
    public async Task ThrowingCloner_AllCloner_DisposesIncomingRef()
    {
        // Every branch has a cloner; the first one throws. firstNoCloner < 0,
        // so the incoming ref must be disposed in the catch.
        var box = RefBox.Of(1);
        var src = Emit(box);
        var a = NullSink<RefBox<int>>("a");
        var b = NullSink<RefBox<int>>("b");

        var graph = new GraphRunner();
        graph.Connect(src.Output, a.Input, ThrowingCloner());
        graph.Connect(src.Output, b.Input, CloneInto(new List<RefBox<int>>()));

        await Assert.ThrowsAsync<ClonerBoomException>(() => graph.RunAsync());

        Assert.Equal(0, box.RefCount);
    }
}
