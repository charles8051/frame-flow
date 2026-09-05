using FrameFlow.Decoding;
using Xunit;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for the rule that separates a bound hardware backend from an engaged one.
/// </summary>
/// <remarks>
/// <para>
/// The rule is one comparison; the reason it exists is not. Binding succeeds at
/// <c>avcodec_open2</c>, which only proves the device opened. FFmpeg refuses the
/// hwaccel per stream later, in <c>get_format</c> on the first decoded frame, and
/// falls back by returning a software pixel format. Before this check the
/// diagnostics reported the bound backend either way, so a run decoding in software
/// was attributed to hardware.
/// </para>
/// <para>
/// Observed on a Vulkan device with no <c>VK_KHR_video_decode_queue</c>: FFmpeg
/// logged <c>Failed setup for format vulkan</c> and the snapshot still said
/// <c>backend=Vulkan</c>. See issue #74.
/// </para>
/// <para>
/// Pure, so the rule is covered without FFmpeg, a GPU, or a media file. The wiring
/// that calls it is covered by the decode-path tests.
/// </para>
/// </remarks>
public sealed class HwAccelEngagementTests
{
    // AV_PIX_FMT_VULKAN and AV_PIX_FMT_YUV420P as FFmpeg numbers them. The values do
    // not matter to the rule; using real ones keeps the test readable next to a log.
    private const int Vulkan = 191;
    private const int Yuv420p = 0;
    private const int Nv12 = 23;

    [Fact]
    public void AFrameInTheBackendsFormatIsAHardwareFrame() =>
        Assert.True(HwAccelEngagement.IsHardwareFrame(Vulkan, Vulkan));

    [Fact]
    public void AFrameInASoftwareFormatIsNot()
    {
        // The reported case: the Vulkan device opened, get_format refused it, and the
        // decoder produced ordinary YUV420P.
        Assert.False(HwAccelEngagement.IsHardwareFrame(Yuv420p, Vulkan));
    }

    [Fact]
    public void ADifferentHardwareFormatIsNotTheBoundBackendsFrame() =>
        Assert.False(HwAccelEngagement.IsHardwareFrame(Nv12, Vulkan));

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NoBoundBackendMeansNoHardwareFrame(int hwPixelFormat)
    {
        // A software-only decoder leaves _hwPixelFormat negative. Without this guard a
        // frame whose format FFmpeg also left unset (-1) would compare equal and report
        // hardware on a decoder that never bound one.
        Assert.False(HwAccelEngagement.IsHardwareFrame(Yuv420p, hwPixelFormat));
        Assert.False(HwAccelEngagement.IsHardwareFrame(-1, hwPixelFormat));
    }

    [Fact]
    public void AnUnsetFrameFormatIsNotHardwareEvenWithABoundBackend()
    {
        // -1 is AV_PIX_FMT_NONE. It is not the backend's format, so it is not evidence
        // the backend produced the frame.
        Assert.False(HwAccelEngagement.IsHardwareFrame(-1, Vulkan));
    }
}
