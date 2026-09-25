using FrameFlow.Camera.Tests.Fakes;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Camera.Tests;

/// <summary>
/// <see cref="CameraVideoFrame"/> under ADR-0080's counting rule: every holder shares one
/// lease, and the lease goes back to the camera's pool once, on the last release (#369).
/// </summary>
[Collection(RefCountingCollection.Name)]
public sealed class CameraVideoFrameRefCountTests
{
    [Fact]
    public void FollowsTheRule()
    {
        RefCountConformance.AssertFollowsTheRule<CameraVideoFrame>(
            () =>
            {
                var lease = new FakeCameraFrame();
                var frame = new CameraVideoFrame(lease);
                return (frame, () => lease.RefCount == 0 ? 1 : 0);
            },
            frame => frame.AddRef()
        );
    }

    [Fact]
    public void SharedFrame_StaysReadableUntilTheLastHolderReleasesIt()
    {
        var lease = new FakeCameraFrame();
        var frame = new CameraVideoFrame(lease);
        var second = frame.AddRef();

        frame.Dispose();

        Assert.Equal(1, second.Width);
        Assert.NotNull(second.AsCpu());
        Assert.Equal(1, lease.RefCount);

        second.Dispose();

        Assert.Equal(0, lease.RefCount);
        Assert.Throws<ObjectDisposedException>(() => second.Width);
    }
}
