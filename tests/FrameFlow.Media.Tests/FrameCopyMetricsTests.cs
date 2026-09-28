using FrameFlow.Media.Diagnostics;
using Xunit;

namespace FrameFlow.Media.Tests;

/// <summary>
/// The per-site copy counts (#435). Nothing else in this assembly copies a frame, so a difference
/// between two snapshots is this test's alone.
/// </summary>
public sealed class FrameCopyMetricsTests
{
    [Fact]
    public void ARecord_CountsAtItsSiteOnly()
    {
        var before = FrameCopyMetrics.Snapshot();

        FrameCopyMetrics.Record(FrameCopySite.PresenterGpuCopy);
        FrameCopyMetrics.Record(FrameCopySite.PresenterGpuCopy);
        FrameCopyMetrics.Record(FrameCopySite.DecoderDownload);

        var made = FrameCopyMetrics.Snapshot().Since(before);
        Assert.Equal(2, made[FrameCopySite.PresenterGpuCopy]);
        Assert.Equal(1, made[FrameCopySite.DecoderDownload]);
        Assert.Equal(3, made.Total);
        Assert.Equal(FrameCopyMetrics.Count(FrameCopySite.DecoderDownload), FrameCopyMetrics.Snapshot()[FrameCopySite.DecoderDownload]);
    }

    [Fact]
    public void ASnapshot_DoesNotMoveAfterItIsTaken()
    {
        var snapshot = FrameCopyMetrics.Snapshot();
        long before = snapshot[FrameCopySite.EncoderConvert];

        FrameCopyMetrics.Record(FrameCopySite.EncoderConvert);

        Assert.Equal(before, snapshot[FrameCopySite.EncoderConvert]);
    }

    [Fact]
    public void ASnapshot_NamesEverySite()
    {
        var text = FrameCopyMetrics.Snapshot().ToString();

        Assert.All(Enum.GetValues<FrameCopySite>(), site => Assert.Contains($"{site}=", text, StringComparison.Ordinal));
    }
}
