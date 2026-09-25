using FrameFlow.Camera.Internal;
using Xunit;

namespace FrameFlow.Camera.Tests;

/// <summary>The camera source's guard policy (ADR-0081 decision 5, phase 1), as a table.</summary>
public sealed class CameraLeaseBudgetTests
{
    [Theory]
    [InlineData(3, 1, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 4, 0)]
    public void TheGraphsShare_IsBufferCountLessTheBridge(int bufferCount, int capacity, int limit)
    {
        Assert.Equal(limit, CameraLeaseBudget.LimitFor(bufferCount, capacity));
    }

    [Fact]
    public void UnderTheLimit_AFrameIsHandedOverAsItsLease()
    {
        var (state, handoff, report) = CameraLeaseBudget.Next(CameraLeaseState.Initial(2));

        Assert.Equal(CameraHandoff.Lease, handoff);
        Assert.False(report);
        Assert.Equal(1, state.Outstanding);
    }

    [Fact]
    public void AtTheLimit_AFrameIsCopied_AndOnlyTheFirstCopyIsReported()
    {
        var atLimit = new CameraLeaseState(Outstanding: 2, Limit: 2, CopyReported: false);

        var (first, firstHandoff, firstReport) = CameraLeaseBudget.Next(atLimit);
        var (second, secondHandoff, secondReport) = CameraLeaseBudget.Next(first);

        Assert.Equal(CameraHandoff.Copy, firstHandoff);
        Assert.True(firstReport);
        Assert.Equal(CameraHandoff.Copy, secondHandoff);
        Assert.False(secondReport);
        Assert.Equal(2, second.Outstanding);
    }

    [Fact]
    public void AReleaseMakesRoomForTheNextLease()
    {
        var atLimit = new CameraLeaseState(Outstanding: 2, Limit: 2, CopyReported: true);

        var (state, handoff, _) = CameraLeaseBudget.Next(CameraLeaseBudget.Released(atLimit));

        Assert.Equal(CameraHandoff.Lease, handoff);
        Assert.Equal(2, state.Outstanding);
    }

    [Fact]
    public void AReleaseAtZero_StaysAtZero()
    {
        Assert.Equal(0, CameraLeaseBudget.Released(CameraLeaseState.Initial(2)).Outstanding);
    }

    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(1, 2, false)]
    [InlineData(3, 2, true)]
    [InlineData(null, 2, true)]
    public void ABudgetOverTheLimit_OrUnbounded_Exceeds(int? budget, int limit, bool exceeds)
    {
        Assert.Equal(exceeds, CameraLeaseBudget.Exceeds(budget, limit));
    }

    [Fact]
    public void AZeroLimit_CopiesEveryFrame()
    {
        var (_, handoff, report) = CameraLeaseBudget.Next(CameraLeaseState.Initial(0));

        Assert.Equal(CameraHandoff.Copy, handoff);
        Assert.True(report);
    }
}
