using FrameFlow.Media.Core;
using Xunit;

namespace FrameFlow.Media.Tests;

/// <summary>
/// The display size and placement a presenter draws a frame at, from its coded size, pixel shape
/// and rotation (#542). Pure.
/// </summary>
public sealed class DisplayGeometryTests
{
    private static readonly SampleAspectRatio Ntsc169 = new(32, 27);

    [Theory]
    [InlineData(720, 480, 1, 1, VideoRotation.None, 720, 480)]
    [InlineData(720, 480, 32, 27, VideoRotation.None, 853, 480)] // DVD shown at 16:9
    [InlineData(720, 480, 8, 9, VideoRotation.None, 640, 480)] // DVD shown at 4:3
    [InlineData(640, 360, 1, 1, VideoRotation.Clockwise90, 360, 640)]
    [InlineData(640, 360, 1, 1, VideoRotation.Clockwise270, 360, 640)]
    [InlineData(640, 360, 1, 1, VideoRotation.Clockwise180, 640, 360)]
    [InlineData(720, 480, 32, 27, VideoRotation.Clockwise90, 480, 853)] // shape first, then the turn
    [InlineData(720, 480, 0, 0, VideoRotation.None, 720, 480)] // an unknown shape is square
    public void DisplaySize_StretchesTheWidth_ThenTurns(
        int width, int height, int num, int den, VideoRotation rotation, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(
            (expectedWidth, expectedHeight),
            DisplaySize.Of(width, height, new SampleAspectRatio(num, den), rotation));
    }

    [Fact]
    public void VideoFormatInfo_ReportsTheDisplaySize()
    {
        var format = new VideoFormatInfo(720, 480, PixelFormat.Bgra32)
        {
            SampleAspectRatio = Ntsc169,
            Rotation = VideoRotation.Clockwise90,
        };

        Assert.Equal((480, 853), (format.DisplayWidth, format.DisplayHeight));
    }

    [Fact]
    public void AFormatsGeometry_IsPartOfItsEquality()
    {
        // The announcer compares formats structurally: a change in shape or rotation alone has
        // to be a different format, or a sink is never told.
        var plain = new VideoFormatInfo(720, 480, PixelFormat.Bgra32);

        Assert.NotEqual(plain, plain with { SampleAspectRatio = Ntsc169 });
        Assert.NotEqual(plain, plain with { Rotation = VideoRotation.Clockwise90 });
        Assert.Equal(plain, new VideoFormatInfo(720, 480, PixelFormat.Bgra32));
    }

    [Theory]
    [InlineData(720, 480, 32, 27, 720, 480, 32, 27)] // same grid, same shape
    [InlineData(720, 540, 4, 3, 960, 540, 1, 1)] // stretched to its display size: square
    [InlineData(720, 480, 32, 27, 360, 240, 32, 27)] // halved both ways: unchanged
    [InlineData(1920, 1080, 1, 1, 960, 1080, 2, 1)] // squeezed: pixels twice as wide
    public void ScaledSampleAspectRatio_KeepsTheShapeThePictureShowsAt(
        int fromWidth, int fromHeight, int num, int den, int toWidth, int toHeight, int expectedNum, int expectedDen)
    {
        var scaled = DisplaySize.ScaledSampleAspectRatio(
            new SampleAspectRatio(num, den), fromWidth, fromHeight, toWidth, toHeight);

        Assert.Equal(new SampleAspectRatio(expectedNum, expectedDen), scaled);
    }

    [Fact]
    public void Placement_LetterboxesAtTheDisplayShape()
    {
        // 720x480 at 32:27 is 16:9: in a 1600x1000 view it fills the width, bars top and bottom.
        var p = VideoPlacement.Fit(1600, 1000, 720, 480, Ntsc169, VideoRotation.None);

        Assert.Equal(1600, p.DisplayWidth, 6);
        Assert.Equal(900, p.DisplayHeight, 6);
        Assert.Equal(0, p.DisplayX, 6);
        Assert.Equal(50, p.DisplayY, 6);

        // Upright, the frame's rectangle is the area.
        Assert.Equal(p.DisplayX, p.DrawX, 6);
        Assert.Equal(p.DisplayY, p.DrawY, 6);
        Assert.Equal(p.DisplayWidth, p.DrawWidth, 6);
        Assert.Equal(p.DisplayHeight, p.DrawHeight, 6);
        Assert.Equal(0, p.RotationRadians);
    }

    [Fact]
    public void Placement_OfAQuarterTurn_IsPortrait_AndTheDrawRectTurnsOntoIt()
    {
        // 640x360 turned a quarter is 360x640: in a 1000x1000 view, 562.5 wide and full height.
        var p = VideoPlacement.Fit(1000, 1000, 640, 360, SampleAspectRatio.Square, VideoRotation.Clockwise270);

        Assert.Equal(562.5, p.DisplayWidth, 6);
        Assert.Equal(1000, p.DisplayHeight, 6);
        Assert.Equal(218.75, p.DisplayX, 6);

        // The frame's own rectangle is landscape, centred on the portrait area, so a quarter turn
        // about its centre covers the area exactly.
        Assert.Equal((1000.0, 562.5), (p.DrawWidth, p.DrawHeight));
        Assert.Equal(p.DisplayX + p.DisplayWidth / 2, p.DrawX + p.DrawWidth / 2, 6);
        Assert.Equal(p.DisplayY + p.DisplayHeight / 2, p.DrawY + p.DrawHeight / 2, 6);
        Assert.Equal(270 * Math.PI / 180, p.RotationRadians, 9);
    }

    [Theory]
    [InlineData(0, 100, 640, 360)]
    [InlineData(100, 100, 0, 360)]
    public void Placement_WithNoBoundsOrNoFrame_IsEmpty(double width, double height, int codedWidth, int codedHeight)
    {
        Assert.True(VideoPlacement.Fit(width, height, codedWidth, codedHeight, SampleAspectRatio.Square, VideoRotation.None).IsEmpty);
    }

    [Fact]
    public void ACpuFrame_CarriesItsGeometry_AndAnUnknownShapeIsSquare()
    {
        using var shaped = CpuVideoFrame.Create(
            PixelFormat.Bgra32, 4, 2, TimeSpan.Zero, TimeSpan.Zero, 0, static (_, _) => { },
            sampleAspectRatio: Ntsc169, rotation: VideoRotation.Clockwise90);
        using var plain = CpuVideoFrame.Create(
            PixelFormat.Bgra32, 4, 2, TimeSpan.Zero, TimeSpan.Zero, 0, static (_, _) => { });

        Assert.Equal((Ntsc169, VideoRotation.Clockwise90), (shaped.SampleAspectRatio, shaped.Rotation));
        Assert.Equal((SampleAspectRatio.Square, VideoRotation.None), (plain.SampleAspectRatio, plain.Rotation));
        Assert.Equal((Ntsc169, VideoRotation.Clockwise90), (shaped.CloneCpu().SampleAspectRatio, shaped.CloneCpu().Rotation));
    }
}
