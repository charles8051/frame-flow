using System.Collections.Concurrent;
using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// <see cref="TimelineId"/> (ADR-0084, decision 1): an identity, unique in the process, that is
/// never <see cref="TimelineId.None"/> once minted.
/// </summary>
public sealed class TimelineIdTests
{
    [Fact]
    public void None_IsTheDefault()
    {
        Assert.Equal(default, TimelineId.None);
        Assert.Equal("none", TimelineId.None.ToString());
    }

    [Fact]
    public void Next_IsNeverNone_AndNeverRepeats()
    {
        var seen = new HashSet<TimelineId>();
        for (int i = 0; i < 1000; i++)
        {
            var id = TimelineId.Next();
            Assert.NotEqual(TimelineId.None, id);
            Assert.True(seen.Add(id), $"{id} was handed out twice");
        }
    }

    [Fact]
    public void Next_IsUniqueAcrossThreads()
    {
        var minted = new ConcurrentBag<TimelineId>();
        Parallel.For(0, 10_000, _ => minted.Add(TimelineId.Next()));

        Assert.Equal(10_000, minted.Distinct().Count());
        Assert.DoesNotContain(TimelineId.None, minted);
    }

    [Fact]
    public void AnIdEqualsItselfAndNoOther()
    {
        var a = TimelineId.Next();
        var b = TimelineId.Next();
        var copy = a;

        Assert.True(a == copy);
        Assert.Equal(a.GetHashCode(), copy.GetHashCode());
        Assert.True(a != b);
        Assert.NotEqual(a.ToString(), b.ToString());
    }

    [Fact]
    public void AnIdHasNoOrder()
    {
        // Equality only (decision 1): an id minted later is not a later timeline, so the type
        // offers nothing that would let a consumer sort by it.
        Assert.False(typeof(IComparable).IsAssignableFrom(typeof(TimelineId)));
        Assert.False(typeof(IComparable<TimelineId>).IsAssignableFrom(typeof(TimelineId)));
        Assert.DoesNotContain(
            typeof(TimelineId).GetMethods(),
            m => m.Name is "op_LessThan" or "op_GreaterThan" or "op_LessThanOrEqual" or "op_GreaterThanOrEqual"
        );
    }
}
