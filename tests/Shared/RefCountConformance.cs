using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Tests.Shared;

/// <summary>
/// Runs the ref-counting rule tests alone. They release past zero on purpose, and
/// <see cref="RefCounting.OverReleases"/> is process-wide, so nothing else may run beside them
/// while they read it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RefCountingCollection
{
    public const string Name = "Ref-counting rule (ADR-0080)";
}

/// <summary>
/// Checks one item type against ADR-0080's counting and disposing rule (decisions 1 and 2).
/// Linked into every test project that owns a ref-counted type. Callers run it from a test in
/// <see cref="RefCountingCollection"/>.
/// </summary>
internal static class RefCountConformance
{
    /// <param name="create">
    /// Builds a fresh item holding one reference, with a probe that reports how many times it
    /// has freed what it owns. The probe is <see langword="null"/> for an item that owns
    /// nothing to free.
    /// </param>
    /// <param name="addRef">Calls the item's own <c>AddRef</c> and returns what it returned.</param>
    public static void AssertFollowsTheRule<T>(
        Func<(T Item, Func<int>? Frees)> create,
        Func<T, object> addRef
    )
        where T : class, IDisposable
    {
        // AddRef returns the same instance.
        {
            var (item, _) = create();
            Assert.Same(item, addRef(item));
            item.Dispose();
            item.Dispose();
        }

        // N AddRefs and N + 1 Disposes free once, on the last.
        var (shared, frees) = create();
        const int extra = 3;
        for (int i = 0; i < extra; i++)
            addRef(shared);
        for (int i = 0; i < extra; i++)
        {
            shared.Dispose();
            if (frees is not null)
                Assert.Equal(0, frees());
        }
        shared.Dispose();
        if (frees is not null)
            Assert.Equal(1, frees());

        // AddRef after the final release throws, and does not revive the item: a second
        // attempt throws too.
        Assert.Throws<ObjectDisposedException>(() => addRef(shared));
        Assert.Throws<ObjectDisposedException>(() => addRef(shared));

        // An extra Dispose frees nothing, does not throw, and is counted.
        long before = RefCounting.OverReleases;
        shared.Dispose();
        Assert.Equal(before + 1, RefCounting.OverReleases);
        if (frees is not null)
            Assert.Equal(1, frees());
        Assert.Throws<ObjectDisposedException>(() => addRef(shared));
    }
}
