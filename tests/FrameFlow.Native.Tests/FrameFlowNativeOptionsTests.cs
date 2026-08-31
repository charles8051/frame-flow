using FrameFlow.Native;

namespace FrameFlow.Native.Tests;

public sealed class FrameFlowNativeOptionsTests
{
    [Fact]
    public void Defaults_UseBundledBinaries_IsTrue()
    {
        var options = new FrameFlowNativeOptions();
        Assert.True(options.UseBundledBinaries);
    }

    [Fact]
    public void Defaults_ProbeSystemLibraries_IsTrue()
    {
        var options = new FrameFlowNativeOptions();
        Assert.True(options.ProbeSystemLibraries);
    }

    [Fact]
    public void Defaults_CustomFfmpegPath_IsNull()
    {
        var options = new FrameFlowNativeOptions();
        Assert.Null(options.CustomFfmpegPath);
    }

    [Fact]
    public void SetCustomFfmpegPath_RetainsValue()
    {
        var options = new FrameFlowNativeOptions { CustomFfmpegPath = @"C:\ffmpeg\bin" };
        Assert.Equal(@"C:\ffmpeg\bin", options.CustomFfmpegPath);
    }

    [Fact]
    public void AllProperties_AreIndependentlyMutable()
    {
        var options = new FrameFlowNativeOptions
        {
            UseBundledBinaries = false,
            ProbeSystemLibraries = false,
            CustomFfmpegPath = "/usr/local/lib",
        };

        Assert.False(options.UseBundledBinaries);
        Assert.False(options.ProbeSystemLibraries);
        Assert.Equal("/usr/local/lib", options.CustomFfmpegPath);
    }
}
