using FrameFlow.Media.Core;
using Xunit;

namespace FrameFlow.Media.Tests;

/// <summary>
/// The display size and placement a presenter draws a frame at, from its coded size, pixel shape
/// and rotation (#542), fitted or filling the bounds (#766). Pure.
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
    public void Fill_CoversTheBoundsAtTheDisplayShape_AndCropsTheSidesEvenly()
    {
        // 720x480 at 32:27 is 16:9: in a 1600x1000 view it fills the height, and 1777.8 wide
        // overflows by 88.9 each side.
        var p = VideoPlacement.Fill(1600, 1000, 720, 480, Ntsc169, VideoRotation.None);

        Assert.Equal(16000.0 / 9, p.DisplayWidth, 6);
        Assert.Equal(1000, p.DisplayHeight, 6);
        Assert.Equal(-800.0 / 9, p.DisplayX, 6);
        Assert.Equal(0, p.DisplayY, 6);
        Assert.Equal((p.DisplayX, p.DisplayY, p.DisplayWidth, p.DisplayHeight), (p.DrawX, p.DrawY, p.DrawWidth, p.DrawHeight));
        Assert.Equal(0, p.RotationRadians);

        // 5% of the width is cut each side: coded columns 36 to 684 show, across the whole view.
        var v = p.Visible(1600, 1000, 720, 480);
        AssertRect((36, 0, 648, 480), (v.SourceX, v.SourceY, v.SourceWidth, v.SourceHeight));
        AssertRect((0, 0, 1600, 1000), (v.DrawX, v.DrawY, v.DrawWidth, v.DrawHeight));
    }

    [Fact]
    public void Fill_OfAQuarterTurn_CoversTheBoundsAfterTheTurn()
    {
        // 640x360 turned a quarter is 360x640: in a 1000x1000 view, full width and 1777.8 high.
        var p = VideoPlacement.Fill(1000, 1000, 640, 360, SampleAspectRatio.Square, VideoRotation.Clockwise270);

        Assert.Equal(1000, p.DisplayWidth, 6);
        Assert.Equal(16000.0 / 9, p.DisplayHeight, 6);
        Assert.Equal(-3500.0 / 9, p.DisplayY, 6);

        // The frame's own rectangle is landscape and turns onto the portrait area.
        Assert.Equal(16000.0 / 9, p.DrawWidth, 6);
        Assert.Equal(1000, p.DrawHeight, 6);
        Assert.Equal(500, p.DrawX + p.DrawWidth / 2, 6);
        Assert.Equal(500, p.DrawY + p.DrawHeight / 2, 6);
        Assert.Equal(270 * Math.PI / 180, p.RotationRadians, 9);

        // The area's top and bottom are cut, which are the frame's own sides: coded columns 140 to
        // 500 show, every row, drawn as the square view.
        var v = p.Visible(1000, 1000, 640, 360);
        AssertRect((140, 0, 360, 360), (v.SourceX, v.SourceY, v.SourceWidth, v.SourceHeight));
        AssertRect((0, 0, 1000, 1000), (v.DrawX, v.DrawY, v.DrawWidth, v.DrawHeight));
        Assert.Equal(p.RotationRadians, v.RotationRadians);
    }

    [Theory]
    [InlineData(VideoRotation.None)]
    [InlineData(VideoRotation.Clockwise90)]
    [InlineData(VideoRotation.Clockwise180)]
    [InlineData(VideoRotation.Clockwise270)]
    public void Fill_OfAPictureTheShapeOfTheBounds_IsTheFit(VideoRotation rotation)
    {
        bool quarter = DisplaySize.IsQuarterTurn(rotation);
        double width = quarter ? 900 : 1600, height = quarter ? 1600 : 900;

        var fit = VideoPlacement.Fit(width, height, 1920, 1080, SampleAspectRatio.Square, rotation);
        var fill = VideoPlacement.Fill(width, height, 1920, 1080, SampleAspectRatio.Square, rotation);

        AssertRect((0, 0, width, height), (fill.DisplayX, fill.DisplayY, fill.DisplayWidth, fill.DisplayHeight));
        AssertRect((fit.DrawX, fit.DrawY, fit.DrawWidth, fit.DrawHeight), (fill.DrawX, fill.DrawY, fill.DrawWidth, fill.DrawHeight));
        Assert.Equal(fit.RotationRadians, fill.RotationRadians);
    }

    [Theory]
    [InlineData(0, 100, 640, 360)]
    [InlineData(100, 100, 0, 360)]
    public void Fill_WithNoBoundsOrNoFrame_IsEmpty(double width, double height, int codedWidth, int codedHeight)
    {
        var p = VideoPlacement.Fill(width, height, codedWidth, codedHeight, SampleAspectRatio.Square, VideoRotation.None);

        Assert.True(p.IsEmpty);
        Assert.True(p.Visible(width, height, codedWidth, codedHeight).IsEmpty);
    }

    [Theory]
    [InlineData(VideoPlacementMode.Fit)]
    [InlineData(VideoPlacementMode.Fill)]
    public void Of_PlacesByTheMode(VideoPlacementMode mode)
    {
        var expected = mode == VideoPlacementMode.Fill
            ? VideoPlacement.Fill(1600, 1000, 640, 360, Ntsc169, VideoRotation.Clockwise90)
            : VideoPlacement.Fit(1600, 1000, 640, 360, Ntsc169, VideoRotation.Clockwise90);

        Assert.Equal(expected, VideoPlacement.Of(mode, 1600, 1000, 640, 360, Ntsc169, VideoRotation.Clockwise90));
    }

    public static TheoryData<VideoPlacementMode, VideoRotation, double, double, int, int, int, int> Placements()
    {
        var data = new TheoryData<VideoPlacementMode, VideoRotation, double, double, int, int, int, int>();
        (double W, double H)[] bounds = [(1600, 1000), (1000, 1000), (300, 900)];
        (int W, int H, int Num, int Den)[] frames = [(720, 480, 32, 27), (640, 360, 1, 1), (1080, 1920, 1, 1)];
        foreach (var mode in new[] { VideoPlacementMode.Fit, VideoPlacementMode.Fill })
            foreach (var rotation in new[] { VideoRotation.None, VideoRotation.Clockwise90, VideoRotation.Clockwise180, VideoRotation.Clockwise270 })
                foreach (var b in bounds)
                    foreach (var f in frames)
                        data.Add(mode, rotation, b.W, b.H, f.W, f.H, f.Num, f.Den);
        return data;
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void Visible_DrawsEachPointFromTheSameCodedPixel_AsTheWholePlacement(
        VideoPlacementMode mode, VideoRotation rotation, double boundsWidth, double boundsHeight,
        int codedWidth, int codedHeight, int num, int den)
    {
        var p = VideoPlacement.Of(mode, boundsWidth, boundsHeight, codedWidth, codedHeight, new SampleAspectRatio(num, den), rotation);
        var v = p.Visible(boundsWidth, boundsHeight, codedWidth, codedHeight);

        // The visible part is the area cut to the bounds, and the draw rectangle turns onto it.
        double left = Math.Max(p.DisplayX, 0), top = Math.Max(p.DisplayY, 0);
        double right = Math.Min(p.DisplayX + p.DisplayWidth, boundsWidth);
        double bottom = Math.Min(p.DisplayY + p.DisplayHeight, boundsHeight);
        AssertRect((left, top, right - left, bottom - top), Turned(v.DrawX, v.DrawY, v.DrawWidth, v.DrawHeight, v.RotationRadians));
        if (mode == VideoPlacementMode.Fill)
            AssertRect((0, 0, boundsWidth, boundsHeight), (left, top, right - left, bottom - top));
        else
            AssertRect((0, 0, codedWidth, codedHeight), (v.SourceX, v.SourceY, v.SourceWidth, v.SourceHeight));

        // Any point of the view the picture covers shows the same coded pixel either way: drawn
        // whole and clipped, or drawn from the crop.
        foreach (var (fx, fy) in new[] { (0.1, 0.2), (0.9, 0.3), (0.25, 0.85), (0.5, 0.5) })
        {
            double x = left + fx * (right - left), y = top + fy * (bottom - top);
            var whole = SourceAt(x, y, (0, 0, codedWidth, codedHeight), (p.DrawX, p.DrawY, p.DrawWidth, p.DrawHeight), p.RotationRadians);
            var crop = SourceAt(x, y, (v.SourceX, v.SourceY, v.SourceWidth, v.SourceHeight), (v.DrawX, v.DrawY, v.DrawWidth, v.DrawHeight), v.RotationRadians);
            Assert.Equal(whole.X, crop.X, 6);
            Assert.Equal(whole.Y, crop.Y, 6);
        }
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void Shown_OfTheVisibleSource_IsTheVisiblePart(
        VideoPlacementMode mode, VideoRotation rotation, double boundsWidth, double boundsHeight,
        int codedWidth, int codedHeight, int num, int den)
    {
        var p = VideoPlacement.Of(mode, boundsWidth, boundsHeight, codedWidth, codedHeight, new SampleAspectRatio(num, den), rotation);
        var v = p.Visible(boundsWidth, boundsHeight, codedWidth, codedHeight);

        var shown = p.Shown(v.SourceX, v.SourceY, v.SourceWidth, v.SourceHeight, codedWidth, codedHeight);

        AssertRect((v.DrawX, v.DrawY, v.DrawWidth, v.DrawHeight), (shown.DrawX, shown.DrawY, shown.DrawWidth, shown.DrawHeight));
        Assert.Equal(v.RotationRadians, shown.RotationRadians);
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void Shown_DrawsAnyPartFromTheSameCodedPixels_AsTheWholePlacement(
        VideoPlacementMode mode, VideoRotation rotation, double boundsWidth, double boundsHeight,
        int codedWidth, int codedHeight, int num, int den)
    {
        var p = VideoPlacement.Of(mode, boundsWidth, boundsHeight, codedWidth, codedHeight, new SampleAspectRatio(num, den), rotation);
        var whole = p.Shown(0, 0, codedWidth, codedHeight, codedWidth, codedHeight);
        AssertRect((p.DrawX, p.DrawY, p.DrawWidth, p.DrawHeight), (whole.DrawX, whole.DrawY, whole.DrawWidth, whole.DrawHeight));

        // An off-centre part, as a ring left holding an earlier crop has.
        (double X, double Y, double W, double H) part = (codedWidth * 0.1, codedHeight * 0.3, codedWidth * 0.5, codedHeight * 0.6);
        var s = p.Shown(part.X, part.Y, part.W, part.H, codedWidth, codedHeight);
        var (cx, cy) = (s.DrawX + s.DrawWidth / 2, s.DrawY + s.DrawHeight / 2);
        foreach (var (fx, fy) in new[] { (0.1, 0.2), (0.9, 0.3), (0.25, 0.85), (0.5, 0.5) })
        {
            // A point of the part's surface, turned with it onto the view.
            var (x, y) = Turn(s.DrawX + fx * s.DrawWidth, s.DrawY + fy * s.DrawHeight, cx, cy, s.RotationRadians);
            var fromWhole = SourceAt(x, y, (0, 0, codedWidth, codedHeight), (p.DrawX, p.DrawY, p.DrawWidth, p.DrawHeight), p.RotationRadians);
            var fromPart = SourceAt(x, y, part, (s.DrawX, s.DrawY, s.DrawWidth, s.DrawHeight), s.RotationRadians);
            Assert.Equal(fromWhole.X, fromPart.X, 6);
            Assert.Equal(fromWhole.Y, fromPart.Y, 6);
        }

        Assert.True(p.Shown(0, 0, 0, codedHeight, codedWidth, codedHeight).IsEmpty);
    }

    [Theory]
    [InlineData(1.0, 1600, 1000)]
    [InlineData(1.5, 2400, 1500)]
    [InlineData(1.25, 2000, 1250)]
    public void Visible_PhysicalSize_IsTheDrawRectangleInDevicePixels(double scaling, int width, int height)
    {
        var v = VideoPlacement.Fill(1600, 1000, 720, 480, Ntsc169, VideoRotation.None).Visible(1600, 1000, 720, 480);

        Assert.Equal((width, height), v.PhysicalSize(scaling));
    }

    [Fact]
    public void Visible_PhysicalSize_OfAQuarterTurn_IsBeforeTheTurn()
    {
        // A 300x900 portrait view filled by a turned landscape frame: the frame's own rectangle is
        // landscape, which is what a surface holding the frame as coded is sized to.
        var v = VideoPlacement.Fill(300, 900, 1920, 1080, SampleAspectRatio.Square, VideoRotation.Clockwise90)
            .Visible(300, 900, 1920, 1080);

        Assert.Equal((900, 300), v.PhysicalSize(1.0));
        Assert.Equal((0, 0), default(VisibleVideo).PhysicalSize(1.0));
        Assert.Equal((0, 0), v.PhysicalSize(0));
    }

    private static (double X, double Y) Turn(double x, double y, double centreX, double centreY, double radians)
    {
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return (centreX + (x - centreX) * cos - (y - centreY) * sin, centreY + (x - centreX) * sin + (y - centreY) * cos);
    }

    // The axis-aligned rectangle a rectangle covers once turned about its centre.
    private static (double X, double Y, double Width, double Height) Turned(double x, double y, double width, double height, double radians)
    {
        double cx = x + width / 2, cy = y + height / 2;
        var corners = new[] { Turn(x, y, cx, cy, radians), Turn(x + width, y + height, cx, cy, radians) };
        double minX = Math.Min(corners[0].X, corners[1].X), minY = Math.Min(corners[0].Y, corners[1].Y);
        return (minX, minY, Math.Abs(corners[0].X - corners[1].X), Math.Abs(corners[0].Y - corners[1].Y));
    }

    // The coded pixel a view point shows when a source rectangle is drawn into a draw rectangle
    // turned about its centre.
    private static (double X, double Y) SourceAt(
        double x, double y,
        (double X, double Y, double Width, double Height) source,
        (double X, double Y, double Width, double Height) draw,
        double radians)
    {
        var (ux, uy) = Turn(x, y, draw.X + draw.Width / 2, draw.Y + draw.Height / 2, -radians);
        return (source.X + (ux - draw.X) / draw.Width * source.Width, source.Y + (uy - draw.Y) / draw.Height * source.Height);
    }

    private static void AssertRect(
        (double X, double Y, double Width, double Height) expected,
        (double X, double Y, double Width, double Height) actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
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
