using FrameFlow.Graph;

namespace FrameFlow.Media.Tests;

/// <summary>Each hardware decode backend's frames are in a GPU domain of their own (#566).</summary>
public sealed class HardwareFrameDomainsTests
{
    [Fact]
    public void EveryBackend_HasOneGpuDomain_AndNoTwoShareOne()
    {
        var domains = Enum.GetValues<HardwareDecodeBackendKind>().Select(HardwareFrameDomains.Of).ToArray();

        Assert.All(domains, d => Assert.True(
            (d & FrameMemoryDomains.Gpu) == d && System.Numerics.BitOperations.PopCount((uint)d) == 1,
            $"{d} is not a single GPU domain"));
        Assert.Equal(domains.Length, domains.Distinct().Count());
        Assert.Equal(FrameMemoryDomains.Gpu, domains.Aggregate(FrameMemoryDomains.None, (all, d) => all | d));
    }

    [Theory]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, FrameMemoryDomains.D3D11)]
    [InlineData(HardwareDecodeBackendKind.D3D12Va, FrameMemoryDomains.D3D12)]
    [InlineData(HardwareDecodeBackendKind.Cuda, FrameMemoryDomains.Cuda)]
    [InlineData(HardwareDecodeBackendKind.Vulkan, FrameMemoryDomains.Vulkan)]
    public void TheBackendsThatHaveReaders_MapToTheirApi(HardwareDecodeBackendKind backend, FrameMemoryDomains domain)
    {
        Assert.Equal(domain, HardwareFrameDomains.Of(backend));
    }

    [Fact]
    public void AnUndefinedBackend_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HardwareFrameDomains.Of((HardwareDecodeBackendKind)99));
    }
}
