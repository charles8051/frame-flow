namespace FrameFlow.Decoding.Tests;

public sealed class VideoDecoderOptionsTests
{
    [Fact]
    public void HeldHardwareFrames_DefaultsToZero()
    {
        Assert.Equal(0, new VideoDecoderOptions().HeldHardwareFrames);
    }

    [Fact]
    public void HeldHardwareFrames_RejectsANegativeCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoDecoderOptions { HeldHardwareFrames = -1 });
    }
}
