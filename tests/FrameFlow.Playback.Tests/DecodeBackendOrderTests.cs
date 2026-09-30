using FrameFlow.Media;
using FrameFlow.Playback.Core;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>Which hardware decode backends a player's decoder tries first (#532).</summary>
public sealed class DecodeBackendOrderTests
{
    private static readonly HardwareDecodeBackendKind[] None = [];
    private static readonly HardwareDecodeBackendKind[] Vulkan = [HardwareDecodeBackendKind.Vulkan];
    private static readonly HardwareDecodeBackendKind[] Cuda = [HardwareDecodeBackendKind.Cuda];

    [Fact]
    public void NothingAsked_IsThePlatformDefault_AndNotLogged()
    {
        var decision = DecodeBackendOrder.Decide(None, None, framesStayOnGpu: true, borrowedDevice: null);

        Assert.Empty(decision.Preferred);
        Assert.True(decision.IsDefault);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARequest_WinsOverTheSink_WhereverFramesGo(bool framesStayOnGpu)
    {
        var decision = DecodeBackendOrder.Decide(Cuda, Vulkan, framesStayOnGpu, borrowedDevice: null);

        Assert.Equal(Cuda, decision.Preferred);
        Assert.False(decision.IsDefault);
    }

    [Fact]
    public void TheSinksPreference_Applies_WhenFramesStayOnTheGpu()
    {
        var decision = DecodeBackendOrder.Decide(None, Vulkan, framesStayOnGpu: true, borrowedDevice: null);

        Assert.Equal(Vulkan, decision.Preferred);
        Assert.Contains("Vulkan", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSinksPreference_IsSetAside_WhenFramesAreDownloaded()
    {
        var decision = DecodeBackendOrder.Decide(None, Vulkan, framesStayOnGpu: false, borrowedDevice: null);

        Assert.Empty(decision.Preferred);
        Assert.False(decision.IsDefault);
        Assert.Contains("downloaded", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ABorrowedDevice_WinsOverARequestAndTheSink_AndSaysSo()
    {
        var decision = DecodeBackendOrder.Decide(
            Cuda, Vulkan, framesStayOnGpu: true, borrowedDevice: HardwareDecodeBackendKind.VaApi);

        Assert.Empty(decision.Preferred);
        Assert.False(decision.IsDefault);
        Assert.Contains("VaApi", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ABorrowedDevice_WithNothingAsked_IsNotLogged()
    {
        var decision = DecodeBackendOrder.Decide(
            None, None, framesStayOnGpu: true, borrowedDevice: HardwareDecodeBackendKind.VaApi);

        Assert.True(decision.IsDefault);
    }
}
