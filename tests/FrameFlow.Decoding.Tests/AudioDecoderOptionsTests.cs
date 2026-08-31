using FrameFlow.Decoding;

namespace FrameFlow.Decoding.Tests;

public sealed class AudioDecoderOptionsTests : IClassFixture<FfmpegBootstrapFixture>
{
    [Fact]
    public void DefaultTargetSampleRate_Is48000()
    {
        var opts = new AudioDecoderOptions();
        Assert.Equal(48_000, opts.TargetSampleRate);
    }

    [Fact]
    public void TargetSampleRate_InitSyntax_StoresValue()
    {
        var opts = new AudioDecoderOptions { TargetSampleRate = 22_050 };
        Assert.Equal(22_050, opts.TargetSampleRate);
    }
}
