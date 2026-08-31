using FrameFlow.Media;
using FrameFlow.Video;

namespace FrameFlow.Video.Tests;

/// <summary>
/// Unit tests for the pure swscale reconfigure predicate (<c>SwsPlan.Decide</c>) — §3.3.
/// </summary>
/// <remarks>
/// The whole point of lifting the reuse-vs-rebuild decision out of
/// <c>SwScaleVideoConverter.EnsureSwsContext</c> is that it can be exercised with
/// <b>no FFmpeg loaded</b>: no <c>SwsContext</c>, no <c>sws_getContext</c>, no media. These
/// tests carry zero native references and assert the full decision table — unchanged shape
/// reuses, any one field differing rebuilds (including target-only changes, which the old
/// FFmpeg-gated test that only checked output width could never isolate).
/// </remarks>
public sealed class SwsPlanTests
{
    // A representative baseline conversion: 1920x1080 NV12 → 1920x1080 BGRA32.
    private static SwsConfigKey Baseline() =>
        new(
            SrcWidth: 1920,
            SrcHeight: 1080,
            SrcFormat: PixelFormat.Nv12,
            DstWidth: 1920,
            DstHeight: 1080,
            DstFormat: PixelFormat.Bgra32
        );

    [Fact]
    public void Decide_NoCachedContext_Rebuilds()
    {
        // Null current == the shell holds no usable context (never built / disposed /
        // invalidated). Always a rebuild — the converter's first frame hits this.
        Assert.Equal(SwsPlanDecision.Rebuild, SwsPlan.Decide(current: null, Baseline()));
    }

    [Fact]
    public void Decide_IdenticalKey_Reuses()
    {
        // The steady state: every subsequent same-shape frame reuses the context.
        Assert.Equal(SwsPlanDecision.Reuse, SwsPlan.Decide(Baseline(), Baseline()));
    }

    [Fact]
    public void Decide_SourceWidthChanged_Rebuilds()
    {
        var requested = Baseline() with { SrcWidth = 1280 };
        Assert.Equal(SwsPlanDecision.Rebuild, SwsPlan.Decide(Baseline(), requested));
    }

    [Fact]
    public void Decide_SourceHeightChanged_Rebuilds()
    {
        var requested = Baseline() with { SrcHeight = 720 };
        Assert.Equal(SwsPlanDecision.Rebuild, SwsPlan.Decide(Baseline(), requested));
    }

    [Fact]
    public void Decide_SourceFormatChanged_Rebuilds()
    {
        // A mid-stream pixel-format flip — the unsafe-to-reuse case the cache exists for.
        var requested = Baseline() with { SrcFormat = PixelFormat.Yuv420P };
        Assert.Equal(SwsPlanDecision.Rebuild, SwsPlan.Decide(Baseline(), requested));
    }

    [Fact]
    public void Decide_TargetWidthChanged_Rebuilds()
    {
        // Target-only change: source is byte-for-byte identical, only the requested output
        // width differs. The old output-width-only assertion could not distinguish this from
        // a reuse; the predicate does.
        var requested = Baseline() with { DstWidth = 960 };
        Assert.Equal(SwsPlanDecision.Rebuild, SwsPlan.Decide(Baseline(), requested));
    }

    [Fact]
    public void Decide_TargetHeightChanged_Rebuilds()
    {
        var requested = Baseline() with { DstHeight = 540 };
        Assert.Equal(SwsPlanDecision.Rebuild, SwsPlan.Decide(Baseline(), requested));
    }

    [Fact]
    public void Decide_TargetFormatChanged_Rebuilds()
    {
        // Same geometry, only the output pixel format differs (Bgra32 → Rgba32).
        var requested = Baseline() with { DstFormat = PixelFormat.Rgba32 };
        Assert.Equal(SwsPlanDecision.Rebuild, SwsPlan.Decide(Baseline(), requested));
    }

    [Fact]
    public void Decide_IsSymmetricKeyEquality_AllSixFieldsMatter()
    {
        // Belt-and-suspenders: flipping each field in turn off a shared baseline must each
        // independently force a rebuild, proving no field was dropped from the key.
        var baseline = Baseline();
        SwsConfigKey[] mutations =
        [
            baseline with { SrcWidth = baseline.SrcWidth + 1 },
            baseline with { SrcHeight = baseline.SrcHeight + 1 },
            baseline with { SrcFormat = PixelFormat.Yuyv422 },
            baseline with { DstWidth = baseline.DstWidth + 1 },
            baseline with { DstHeight = baseline.DstHeight + 1 },
            baseline with { DstFormat = PixelFormat.Rgba32 },
        ];

        foreach (var mutated in mutations)
        {
            Assert.Equal(SwsPlanDecision.Rebuild, SwsPlan.Decide(baseline, mutated));
        }

        // And the unmutated baseline still reuses, so the rebuilds above are real signal.
        Assert.Equal(SwsPlanDecision.Reuse, SwsPlan.Decide(baseline, baseline));
    }
}
