namespace FrameFlow.Media.Tests;

public sealed class PixelFormatTests
{
    /// <summary>
    /// Pins the exact member set. Adding or removing a pixel format is a
    /// contract change — every <c>switch</c> over <see cref="PixelFormat"/>
    /// (conversion, stride calc, sink upload) must be revisited — so this
    /// test trips deliberately to force that review. Asserting the named
    /// set rather than a bare count makes the expected contract explicit
    /// and self-documenting when it fails.
    /// </summary>
    [Fact]
    public void PixelFormat_HasExpectedMembers()
    {
        var values = Enum.GetValues<PixelFormat>();
        Assert.Equal(
            new[]
            {
                PixelFormat.Bgra32,
                PixelFormat.Rgba32,
                PixelFormat.Yuv420P,
                PixelFormat.Nv12,
                PixelFormat.Yuyv422,
                PixelFormat.Uyvy422,
            },
            values
        );
    }
}
