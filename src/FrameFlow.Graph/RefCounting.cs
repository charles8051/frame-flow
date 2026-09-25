// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FrameFlow.Graph;

/// <summary>
/// The counting and releasing rule every <see cref="IRefCounted"/> item follows
/// (ADR-0080, decisions 1 and 2). An item keeps its count in an <see cref="int"/> field that
/// starts at 1 and routes its <c>AddRef</c> and <c>Dispose</c> through here, so every item in
/// the graph behaves the same way when it is shared, released, or released too often.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counting.</b> <see cref="AddRef"/> takes one more reference, atomically, and never
/// revives an item whose count has reached zero: that item has freed what it owns, and a
/// caller still holding it is using it after release.
/// </para>
/// <para>
/// <b>Releasing.</b> Each <see cref="Release"/> gives back exactly one reference, and
/// exactly one call, the one that drops the last reference, returns <see langword="true"/>.
/// A release past zero is an over-release. It frees nothing, is counted in
/// <see cref="OverReleases"/> and on the <c>frameflow.graph.over_releases</c> metric, and
/// breaks into an attached debugger in a debug build. It never throws: the graph's pumps
/// dispose items inside <c>catch</c> blocks and in best-effort drains, and a throw there
/// would replace the real failure and abandon the rest of the drain.
/// </para>
/// <para>
/// ADR-0080 asks for <c>Debug.Fail</c>. That terminates the process when no debugger is
/// attached, including a test host exercising the over-release path on purpose, so the
/// debug-build check breaks into an attached debugger instead and relies on the counter
/// everywhere else.
/// </para>
/// </remarks>
public static class RefCounting
{
    private static readonly Meter Meter = new("FrameFlow.Graph", "1.0.0");

    private static readonly Counter<long> OverReleaseCounter = Meter.CreateCounter<long>(
        "frameflow.graph.over_releases",
        unit: "{release}",
        description: "Releases of a ref-counted item past zero. Each one is a caller bug.");

    private static long _overReleases;

    /// <summary>Over-releases observed in this process since it started.</summary>
    public static long OverReleases => Interlocked.Read(ref _overReleases);

    /// <summary>Takes one more reference on an item whose count lives in <paramref name="count"/>.</summary>
    /// <param name="count">The item's count field.</param>
    /// <param name="owner">The item, named in the exception and the metric.</param>
    /// <exception cref="ObjectDisposedException">The item has already been released.</exception>
    public static void AddRef(ref int count, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        while (true)
        {
            int current = Volatile.Read(ref count);
            if (current <= 0)
            {
                throw new ObjectDisposedException(
                    owner.GetType().Name,
                    "AddRef after the final release. A reference held past its Dispose is a "
                        + "use-after-release bug."
                );
            }

            if (Interlocked.CompareExchange(ref count, current + 1, current) == current)
                return;
        }
    }

    /// <summary>Gives back one reference on an item whose count lives in <paramref name="count"/>.</summary>
    /// <param name="count">The item's count field.</param>
    /// <param name="owner">The item, named in the metric.</param>
    /// <returns>
    /// <see langword="true"/> for the one call that drops the last reference. The caller then
    /// frees what the item owns. <see langword="false"/> for every other call, including an
    /// over-release.
    /// </returns>
    public static bool Release(ref int count, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        int remaining = Interlocked.Decrement(ref count);
        if (remaining > 0)
            return false;
        if (remaining == 0)
            return true;

        // Past zero: undo the decrement so the count reads zero again, and report it.
        Interlocked.Increment(ref count);
        Interlocked.Increment(ref _overReleases);
        OverReleaseCounter.Add(1, new KeyValuePair<string, object?>("type", owner.GetType().Name));
        BreakIfDebugging();
        return false;
    }

    [Conditional("DEBUG")]
    private static void BreakIfDebugging()
    {
        if (Debugger.IsAttached)
            Debugger.Break();
    }
}
