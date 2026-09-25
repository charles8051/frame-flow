using FrameFlow.Media;
using FrameFlow.Native.Interop;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Decoding.Tests;

/// <summary>ADR-0080's counting and disposing rule, for the Decoding assembly's items.</summary>
[Collection(RefCountingCollection.Name)]
public sealed class RefCountingRuleTests : IClassFixture<FfmpegBootstrapFixture>
{
    public RefCountingRuleTests(FfmpegBootstrapFixture _) { }

    [RequiresFfmpegFact]
    public void GpuVideoFrame_FollowsTheRule()
    {
        // The frame's final release frees its AVFrame; nothing observable tells a test that
        // happened, so the probe is null. GpuVideoFrameRefCountTests covers the release itself.
        RefCountConformance.AssertFollowsTheRule(
            () =>
            {
                nint src = FFAvUtil.av_frame_alloc();
                Assert.NotEqual(nint.Zero, src);
                var frame = GpuVideoFrame.FromOwnedAvFrame(
                    src,
                    width: 64,
                    height: 64,
                    softwareFormat: PixelFormat.Nv12,
                    pts: TimeSpan.Zero,
                    duration: TimeSpan.FromMilliseconds(33),
                    backend: HardwareDecodeBackendKind.D3D11Va
                );
                return (frame, (Func<int>?)null);
            },
            frame => frame.AddRef()
        );
    }
}
