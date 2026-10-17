using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The size of the shader-mode ring for a view, when a change of it is applied, and how the shader
/// samples the frame into it (#767). Pure: the clock is a number the test advances.
/// </summary>
public sealed class ViewSizedRingTests
{
    private const long Hold = 500;
    private static readonly RingSize Frame1080 = new(1920, 1080);

    [Fact]
    public void A1080pFrameIn384x216_WantsA384x216Ring()
    {
        var cap = ViewSizedRingPolicy.Cap(SourceRect.Whole(1920, 1080), 1920, 1080);

        Assert.Equal(Frame1080, cap);
        Assert.Equal(new RingSize(384, 216), ViewSizedRingPolicy.Wanted(384, 216, cap));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void AtOrAboveTheFramesSize_TheRingIsTheFrame(int drawWidth, int drawHeight)
    {
        var cap = ViewSizedRingPolicy.Cap(SourceRect.Whole(1920, 1080), 1920, 1080);

        Assert.Equal(Frame1080, ViewSizedRingPolicy.Wanted(drawWidth, drawHeight, cap));
    }

    [Fact]
    public void EachAxis_IsHeldToTheSourcesSizeOnItsOwn()
    {
        // An anamorphic 1440x1080 frame shown 1920 wide keeps its 1440 columns; a crop of a filled
        // view caps the ring at the crop.
        var anamorphic = ViewSizedRingPolicy.Cap(SourceRect.Whole(1440, 1080), 1440, 1080);
        Assert.Equal(new RingSize(1440, 540), ViewSizedRingPolicy.Wanted(1920, 540, anamorphic));

        var crop = ViewSizedRingPolicy.Cap(new SourceRect(420, 0, 1079.6, 1080), 1920, 1080);
        Assert.Equal(new RingSize(1080, 1080), crop);
        Assert.Equal(new RingSize(1080, 700), ViewSizedRingPolicy.Wanted(1500, 700, crop));
    }

    [Theory]
    [InlineData(0, 216)]
    [InlineData(384, 0)]
    [InlineData(-1, -1)]
    public void NothingDrawn_WantsNoChange(int drawWidth, int drawHeight) =>
        Assert.Null(ViewSizedRingPolicy.Wanted(drawWidth, drawHeight, Frame1080));

    [Fact]
    public void ACapNeverDropsBelowOnePixel_NorExceedsTheFrame()
    {
        Assert.Equal(new RingSize(1, 1), ViewSizedRingPolicy.Cap(new SourceRect(0, 0, 0.2, 0.4), 1920, 1080));
        Assert.Equal(Frame1080, ViewSizedRingPolicy.Cap(new SourceRect(0, 0, 4000, 4000), 1920, 1080));
        Assert.Equal(Frame1080, ViewSizedRingPolicy.Cap(default, 1920, 1080));
    }

    [Fact]
    public void Grown_RoundsAGrowingAxisUpToTheStep_WithinTheCap()
    {
        var small = new RingSize(384, 216);
        Assert.Equal(new RingSize(448, 256), ViewSizedRingPolicy.Grown(new RingSize(385, 217), small, Frame1080));
        Assert.Equal(new RingSize(1920, 1080), ViewSizedRingPolicy.Grown(new RingSize(1900, 1070), small, Frame1080));
    }

    [Fact]
    public void Grown_KeepsAnAxisThatShrinks_ForTheHold()
    {
        // A 1000x1000 ring wanting 1100x500 grows across at once and keeps its height.
        var square = new RingSize(1000, 1000);
        Assert.Equal(new RingSize(1152, 1000), ViewSizedRingPolicy.Grown(new RingSize(1100, 500), square, new RingSize(1920, 1920)));

        // Past a cap that has since come down, as a narrower crop brings, the axis drops to the cap.
        Assert.Equal(new RingSize(1152, 800), ViewSizedRingPolicy.Grown(new RingSize(1100, 500), square, new RingSize(1920, 800)));
    }

    // ── The view's plan ──

    [Theory]
    [InlineData(1.0, 384, 216)]
    [InlineData(1.5, 576, 324)]
    [InlineData(10.0, 1920, 1080)]
    public void AFittedView_WantsItsDrawnSizeInPhysicalPixels_UpToTheFrame(double scaling, int width, int height)
    {
        var plan = CompositionInteropVideoView.PlanRing(
            VideoPlacementMode.Fit, 384, 216, scaling, 1920, 1080, SampleAspectRatio.Square, VideoRotation.None);

        Assert.Equal(SourceRect.Whole(1920, 1080), plan.Source);
        Assert.Equal(new RingSize(width, height), plan.Wanted);
        Assert.Equal(Frame1080, plan.Cap);
    }

    [Fact]
    public void AFilledView_ReadsItsCrop_AndCapsTheRingAtIt()
    {
        var small = CompositionInteropVideoView.PlanRing(
            VideoPlacementMode.Fill, 400, 400, 1.0, 1920, 1080, SampleAspectRatio.Square, VideoRotation.None);
        Assert.Equal(new SourceRect(420, 0, 1080, 1080), small.Source);
        Assert.Equal(new RingSize(400, 400), small.Wanted);
        Assert.Equal(new RingSize(1080, 1080), small.Cap);

        var large = CompositionInteropVideoView.PlanRing(
            VideoPlacementMode.Fill, 2000, 2000, 1.0, 1920, 1080, SampleAspectRatio.Square, VideoRotation.None);
        Assert.Equal(new RingSize(1080, 1080), large.Wanted);
    }

    [Fact]
    public void AQuarterTurn_SizesTheRingAsCoded()
    {
        var plan = CompositionInteropVideoView.PlanRing(
            VideoPlacementMode.Fit, 216, 384, 1.0, 1920, 1080, SampleAspectRatio.Square, VideoRotation.Clockwise90);

        Assert.Equal(new RingSize(384, 216), plan.Wanted);
    }

    [Theory]
    [InlineData(0, 216)]
    [InlineData(384, 0)]
    public void AViewWithNoBounds_WantsNothing_AndAFirstBuildTakesTheFrame(double width, double height)
    {
        var plan = CompositionInteropVideoView.PlanRing(
            VideoPlacementMode.Fit, width, height, 1.0, 1920, 1080, SampleAspectRatio.Square, VideoRotation.None);

        Assert.Null(plan.Wanted);
        Assert.Equal(Frame1080, plan.Immediate);
        Assert.Equal(SourceRect.Whole(1920, 1080), plan.Source);
    }

    // ── RingSettle ──

    private static readonly int[] Sizes = [1, 63, 64, 65, 216, 384, 1000, 1080, 1919, 1920];

    public static IEnumerable<object[]> Pairs()
    {
        foreach (int cw in Sizes)
            foreach (int ch in new[] { 64, 216, 1080 })
                foreach (int ww in Sizes)
                    foreach (int wh in new[] { 1, 64, 215, 216, 1080 })
                        yield return [cw, ch, ww, wh];
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void EveryResize_HoldsTheWantedSize_WithinTheCap_AndALargerWantGrowsAtOnce(int cw, int ch, int ww, int wh)
    {
        var current = new RingSize(cw, ch);
        var wanted = ViewSizedRingPolicy.Wanted(ww, wh, Frame1080)!.Value;
        bool grows = wanted.Width > current.Width || wanted.Height > current.Height;

        var (next, resize) = default(RingSettle).Advance(current, wanted, Frame1080, now: 1000, Hold);

        if (wanted == current)
        {
            Assert.Null(resize);
            Assert.Equal(default, next);
        }
        else if (grows)
        {
            // The ring is never left too small to draw 1:1: the compositor would scale it up.
            var r = Assert.NotNull(resize);
            Assert.True(r.Width >= wanted.Width && r.Height >= wanted.Height, $"{r} for {wanted}");
            Assert.True(r.Width <= Frame1080.Width && r.Height <= Frame1080.Height, $"{r} past the cap");

            // A growing axis is rounded up by less than a step; any other axis keeps its size, so
            // its shrink still waits for the hold.
            Assert.Equal(GrownAxis(wanted.Width, current.Width, Frame1080.Width), r.Width);
            Assert.Equal(GrownAxis(wanted.Height, current.Height, Frame1080.Height), r.Height);
            Assert.Equal(default, next);
        }
        else
        {
            Assert.Null(resize);
            Assert.Equal(new RingSettle(wanted, 1000), next);
        }
    }

    private static int GrownAxis(int wanted, int current, int cap)
    {
        if (wanted <= current)
            return Math.Min(current, cap);
        int r = Math.Min((wanted + ViewSizedRingPolicy.Step - 1) / ViewSizedRingPolicy.Step * ViewSizedRingPolicy.Step, cap);
        Assert.InRange(r - wanted, 0, ViewSizedRingPolicy.Step - 1);
        return r;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void ASmallerSize_IsTakenExactly_OnceHeldForTheHoldTime_AndNotATickBefore(int cw, int ch, int ww, int wh)
    {
        var current = new RingSize(cw, ch);
        var wanted = ViewSizedRingPolicy.Wanted(ww, wh, Frame1080)!.Value;
        if (wanted == current || wanted.Width > current.Width || wanted.Height > current.Height)
            return; // the case above

        var settle = default(RingSettle);
        RingSize? resize;
        for (long now = 1000; now < 1000 + Hold; now += 37)
        {
            (settle, resize) = settle.Advance(current, wanted, Frame1080, now, Hold);
            Assert.Null(resize);
        }

        (settle, resize) = settle.Advance(current, wanted, Frame1080, 1000 + Hold - 1, Hold);
        Assert.Null(resize);
        (settle, resize) = settle.Advance(current, wanted, Frame1080, 1000 + Hold, Hold);
        Assert.Equal(wanted, resize);
        Assert.Equal(default, settle);
    }

    [Fact]
    public void AChangeOfTheWantedSize_RestartsTheHold()
    {
        var current = new RingSize(1920, 1080);
        var settle = default(RingSettle);

        (settle, _) = settle.Advance(current, new RingSize(800, 450), Frame1080, 0, Hold);
        (settle, var resize) = settle.Advance(current, new RingSize(790, 444), Frame1080, 400, Hold);
        Assert.Null(resize);
        (settle, resize) = settle.Advance(current, new RingSize(790, 444), Frame1080, 600, Hold);
        Assert.Null(resize);
        (_, resize) = settle.Advance(current, new RingSize(790, 444), Frame1080, 900, Hold);
        Assert.Equal(new RingSize(790, 444), resize);
    }

    [Fact]
    public void WantingTheRingsOwnSizeAgain_DropsAPendingShrink()
    {
        var current = new RingSize(1920, 1080);

        var (settle, _) = default(RingSettle).Advance(current, new RingSize(800, 450), Frame1080, 0, Hold);
        (settle, var resize) = settle.Advance(current, current, Frame1080, 100, Hold);
        Assert.Null(resize);
        Assert.Equal(default, settle);

        // The shrink starts over from its next want.
        (settle, resize) = settle.Advance(current, new RingSize(800, 450), Frame1080, 2000, Hold);
        Assert.Null(resize);
        Assert.Equal(new RingSettle(new RingSize(800, 450), 2000), settle);
    }

    [Fact]
    public void NoWantedSize_KeepsTheRing_AndDropsAPendingShrink()
    {
        var current = new RingSize(1920, 1080);
        var (settle, _) = default(RingSettle).Advance(current, new RingSize(800, 450), Frame1080, 0, Hold);

        var (next, resize) = settle.Advance(current, null, Frame1080, 10_000, Hold);

        Assert.Null(resize);
        Assert.Equal(default, next);
    }

    [Fact]
    public void ADragThatGrows_RebuildsOncePerStep_ThenSettlesOnTheDrawnSize()
    {
        // A view dragged from 384x216 to 1000x562 by 4 px a frame, a frame every 16 ticks.
        var ring = new RingSize(384, 216);
        var settle = default(RingSettle);
        int resizes = 0;
        long now = 0;
        for (int w = 388; w <= 1000; w += 4, now += 16)
        {
            int h = (int)Math.Round(w * 9 / 16.0);
            (settle, var resize) = settle.Advance(ring, ViewSizedRingPolicy.Wanted(w, h, Frame1080), Frame1080, now, Hold);
            if (resize is { } r)
            {
                Assert.True(r.Width >= w && r.Height >= h);
                ring = r;
                resizes++;
            }
        }

        // At most one rebuild per step crossed on either axis: ten widths from 448 to 1024 and six
        // heights from 256 to 576, some of them crossed together. A rebuild per frame would be 153.
        Assert.InRange(resizes, 10, 16);
        Assert.Equal(new RingSize(1024, 576), ring);

        // Held still, the ring takes the drawn size once, after the hold.
        var still = new RingSize(1000, 563);
        for (long t = now; t < now + Hold; t += 16)
        {
            (settle, var early) = settle.Advance(ring, still, Frame1080, t, Hold);
            Assert.Null(early);
        }

        var (_, settled) = settle.Advance(ring, still, Frame1080, now + Hold, Hold);
        Assert.Equal(still, settled);
    }

    [Fact]
    public void ADragThatShrinks_RebuildsOnlyOnceItStops()
    {
        var ring = Frame1080;
        var settle = default(RingSettle);
        long now = 0;
        for (int w = 1916; w >= 384; w -= 4, now += 16)
        {
            int h = (int)Math.Round(w * 9 / 16.0);
            (settle, var resize) = settle.Advance(ring, ViewSizedRingPolicy.Wanted(w, h, Frame1080), Frame1080, now, Hold);
            Assert.Null(resize);
        }

        var stopped = new RingSize(384, 216);
        (settle, var none) = settle.Advance(ring, stopped, Frame1080, now + Hold - 17, Hold);
        Assert.Null(none);
        var (_, applied) = settle.Advance(ring, stopped, Frame1080, now + Hold, Hold);
        Assert.Equal(stopped, applied);
    }

    // ── ShaderSampling ──

    [Fact]
    public void TheWholeFrameAtItsOwnSize_CoversATexelARingPixel()
    {
        var s = ShaderSampling.For(SourceRect.Whole(1920, 1080), 1920, 1080, Frame1080);

        Assert.Equal(new ShaderSampling(0, 0, 1, 1, 1f / 1920, 1f / 1080), s);
    }

    [Theory]
    [InlineData(384, 216)]
    [InlineData(640, 360)]
    [InlineData(1000, 563)]
    [InlineData(64, 36)]
    public void EachRingPixel_CoversItsShareOfTheFrame(int width, int height)
    {
        var s = ShaderSampling.For(SourceRect.Whole(1920, 1080), 1920, 1080, new RingSize(width, height));

        // In texels of the luma plane: the ratio the area filter averages over.
        Assert.Equal(1920.0 / width, s.FootprintU * 1920, 3);
        Assert.Equal(1080.0 / height, s.FootprintV * 1080, 3);
    }

    [Fact]
    public void ACrop_IsReadAsTextureCoordinates()
    {
        var s = ShaderSampling.For(new SourceRect(480, 0, 960, 1080), 1920, 1080, new RingSize(480, 540));

        Assert.Equal(new ShaderSampling(0.25f, 0f, 0.5f, 1f, 0.5f / 480, 1f / 540), s);
    }

    [Fact]
    public void AnEmptySource_ReadsTheWholeFrame() =>
        Assert.Equal(
            ShaderSampling.For(SourceRect.Whole(1920, 1080), 1920, 1080, new RingSize(384, 216)),
            ShaderSampling.For(default, 1920, 1080, new RingSize(384, 216)));
}
