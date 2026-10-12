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
    public void AClip_CarriesItsEnd_AndHasNoneByDefault()
    {
        Assert.Null(new MediaClip(Source, TimeSpan.FromSeconds(2)).End);
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            new MediaClip(Source, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)).End
        );
    }

    [Fact]
    public void AnEndThatIsNotAfterTheStart_IsRefused_WhicheverIsSetLast()
    {
        var two = TimeSpan.FromSeconds(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaClip(Source, two, two));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MediaClip(Source, two) with { End = TimeSpan.FromSeconds(1) }
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MediaClip(Source, TimeSpan.Zero, two) with { Start = two }
        );
    }

    [Fact]
    public void ClipsOfOneSourceAndStart_AreEqual() =>
        Assert.Equal(
            new MediaClip(Source, TimeSpan.FromSeconds(2)),
            new MediaClip(Source, TimeSpan.FromSeconds(2))
        );
}
