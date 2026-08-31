using FrameFlow.Decoding;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Pins <see cref="ShedRateAccounting"/>, which turns individual packet drops into a rate the
/// logs actually say out loud (issue #143).
/// </summary>
/// <remarks>
/// The count was always recorded and never surfaced, so a pipeline shedding a third of its
/// packets read as healthy. The two properties that make this worth having are that a healthy
/// pipeline stays silent, and that a sustained problem reports at a bounded cadence rather
/// than per drop.
/// </remarks>
public sealed class ShedRateAccountingTests
{
    private const long Freq = 1_000_000; // 1 tick = 1 microsecond
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    private static long Us(double seconds) => (long)(seconds * 1_000_000);

    private static (ShedWindow, ShedReport?) Observe(ShedWindow state, double atSeconds, long total = 1) =>
        ShedRateAccounting.Observe(state, Us(atSeconds), Freq, Every, total);

    [Fact]
    public void TheFirstDropOpensAWindowWithoutReporting()
    {
        var (state, report) = Observe(ShedWindow.None, atSeconds: 0);

        Assert.Null(report);
        Assert.True(state.Open);
        Assert.Equal(1, state.DroppedInWindow);
    }

    [Fact]
    public void NothingIsReportedBeforeTheIntervalElapses()
    {
        var state = ShedWindow.None;
        ShedReport? report = null;

        for (double t = 0; t < 10; t += 0.5)
            (state, report) = Observe(state, t);

        Assert.Null(report);
    }

    [Fact]
    public void TheDropThatClosesTheWindowCarriesTheRate()
    {
        var state = ShedWindow.None;
        ShedReport? report = null;

        // 20 drops over 10 s: one every half second.
        for (double t = 0; t <= 10; t += 0.5)
            (state, report) = Observe(state, t, total: 500);

        Assert.NotNull(report);
        Assert.Equal(21, report!.Value.Dropped);
        Assert.Equal(10, report.Value.Seconds, 1);
        Assert.Equal(2.1, report.Value.PerSecond, 1);
        Assert.Equal(500, report.Value.TotalDropped);
    }

    [Fact]
    public void AClosedWindowStartsTheNextOneFromScratch()
    {
        var state = ShedWindow.None;
        ShedReport? report = null;

        for (double t = 0; t <= 10; t += 0.5)
            (state, report) = Observe(state, t);
        Assert.NotNull(report);

        // Consecutive reports must not overlap, or the second one's rate is measured over a
        // span the first already covered.
        Assert.False(state.Open);

        (state, report) = Observe(state, 10.5);
        Assert.Null(report);
        Assert.Equal(1, state.DroppedInWindow);
        Assert.Equal(Us(10.5), state.WindowStartTicks);
    }

    [Fact]
    public void TwoDropsAnHourApartAreNotReported()
    {
        // A Warning saying the pump is outrunning the video chain would be a false alarm here,
        // and false alarms on this path are worse than silence — the whole reason it exists is
        // to point an investigation in the right direction.
        var (state, first) = Observe(ShedWindow.None, atSeconds: 0);
        Assert.Null(first);

        var (next, afterHour) = Observe(state, atSeconds: 3600);

        Assert.Null(afterHour);
        // The stale window is discarded, not extended: the new one starts at this drop.
        Assert.Equal(1, next.DroppedInWindow);
        Assert.Equal(Us(3600), next.WindowStartTicks);
    }

    [Fact]
    public void ATrickleBelowTheRateFloorIsNotReported()
    {
        // One drop every four seconds closes a window at 0.25/s. Real, but a straggler rather
        // than a chain failing to keep up, and the message would overstate it.
        var state = ShedWindow.None;
        ShedReport? report = null;

        for (double t = 0; t <= 40; t += 4)
            (state, report) = Observe(state, t);

        Assert.Null(report);
    }

    [Fact]
    public void SheddingThatStopsAndRestartsDoesNotFoldInTheIdleTime()
    {
        // 87 drops in a second, quiet, then shedding resumes. Reporting 88 over 12 s would
        // describe a quiet pipeline as a busy one.
        var state = ShedWindow.None;
        ShedReport? report = null;

        for (int i = 0; i < 87; i++)
            (state, report) = Observe(state, i * 0.01);
        Assert.Null(report);

        (state, report) = Observe(state, 12.0);

        Assert.Null(report);
        Assert.Equal(1, state.DroppedInWindow);
    }

    [Fact]
    public void AHealthyPipelineNeverReports()
    {
        // Observe is only called on a drop, so no drops means no state and no output. Stated
        // as a test because "silent when healthy" is the property that makes a Warning-level
        // log acceptable on this path at all.
        var state = ShedWindow.None;

        Assert.False(state.Open);
        Assert.Equal(0, state.DroppedInWindow);
    }

    [Fact]
    public void AnIsolatedBurstIsNeverReported()
    {
        // The documented cost of reporting only sustained shedding: a burst that starts and
        // stops inside one interval never produces a line. Deliberate — the failure worth
        // raising is a chain that cannot keep up, and the cumulative count is still in
        // PollDiagnostics for anyone who wants every drop.
        var state = ShedWindow.None;
        ShedReport? report = null;

        for (int i = 0; i < 87; i++)
            (state, report) = Observe(state, i * 0.01);

        Assert.Null(report);
        Assert.Equal(87, state.DroppedInWindow);
    }

    [Fact]
    public void SustainedSheddingReportsAtABoundedCadence()
    {
        // The #145 shape: ~14 packets/s for a minute. One line per interval, not per drop.
        var state = ShedWindow.None;
        var reports = 0;
        long total = 0;

        for (double t = 0; t <= 60; t += 1.0 / 14)
        {
            total++;
            var (next, report) = Observe(state, t, total);
            state = next;
            if (report is not null)
                reports++;
        }

        Assert.InRange(reports, 5, 6); // ~60 s at one per 10 s
        Assert.True(total > 800, $"expected a sustained stream of drops, saw {total}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnUnusableClockCountsWithoutReporting(long frequency)
    {
        var (state, _) = ShedRateAccounting.Observe(ShedWindow.None, 0, frequency, Every, 1);
        var (next, report) = ShedRateAccounting.Observe(state, Us(30), frequency, Every, 2);

        // Better to keep counting silently than to divide by a frequency that cannot be right.
        Assert.Null(report);
        Assert.Equal(2, next.DroppedInWindow);
    }

    [Fact]
    public void ABackwardsClockDoesNotReport()
    {
        var (state, _) = Observe(ShedWindow.None, atSeconds: 100);
        var (next, report) = Observe(state, atSeconds: 50);

        Assert.Null(report);
        Assert.Equal(2, next.DroppedInWindow);
    }
}
