using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>ADR-0080's counting and disposing rule, for the Graph assembly's items.</summary>
[Collection(RefCountingCollection.Name)]
public sealed class RefCountingRuleTests
{
    [Fact]
    public void RefBox_FollowsTheRule()
    {
        RefCountConformance.AssertFollowsTheRule<RefBox<string>>(
            () =>
            {
                int frees = 0;
                var box = new RefBox<string>("value", _ => frees++);
                return (box, () => frees);
            },
            box => box.AddRef()
        );
    }

    [Fact]
    public void Release_ReturnsTrueOnlyForTheCallThatDropsTheLastReference()
    {
        int count = 1;
        var owner = new object();
        RefCounting.AddRef(ref count, owner);

        Assert.False(RefCounting.Release(ref count, owner));
        Assert.True(RefCounting.Release(ref count, owner));
    }

    [Fact]
    public void Release_PastZero_LeavesTheCountAtZero()
    {
        int count = 1;
        var owner = new object();
        Assert.True(RefCounting.Release(ref count, owner));

        Assert.False(RefCounting.Release(ref count, owner));
        Assert.Equal(0, count);
    }
}
