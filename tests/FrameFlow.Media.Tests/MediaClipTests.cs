namespace FrameFlow.Media.Tests;

public sealed class MediaClipTests
{
    private static readonly IMediaSource Source = MediaSource.FromFile("clip.mp4");

    [Fact]
    public void AClip_CarriesItsSourceAndStart()
    {
        var clip = new MediaClip(Source, TimeSpan.FromSeconds(83.2));

        Assert.Same(Source, clip.Source);
        Assert.Equal(TimeSpan.FromSeconds(83.2), clip.Start);
    }

    [Fact]
    public void AClipWithNoStart_StartsAtZero() =>
        Assert.Equal(TimeSpan.Zero, new MediaClip(Source).Start);

    [Fact]
    public void ANullSource_IsRefused() =>
        Assert.Throws<ArgumentNullException>(() => new MediaClip(null!));

    [Fact]
    public void ANegativeStart_IsRefused_FromTheConstructorAndFromWith()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MediaClip(Source, TimeSpan.FromTicks(-1))
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MediaClip(Source) with { Start = TimeSpan.FromTicks(-1) }
        );
    }

    [Fact]
    public void ClipsOfOneSourceAndStart_AreEqual() =>
        Assert.Equal(
            new MediaClip(Source, TimeSpan.FromSeconds(2)),
            new MediaClip(Source, TimeSpan.FromSeconds(2))
        );
}
