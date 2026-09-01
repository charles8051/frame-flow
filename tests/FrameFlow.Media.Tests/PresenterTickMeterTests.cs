using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins <see cref="PresenterTickMeter"/>, the diagnostic that separates the two explanations
/// for the 1080p60 ceiling in issue #128.
/// </summary>
/// <remarks>
/// The meter is only worth having if the two causes produce visibly different reports at the
/// same frame rate, so the load-bearing tests are the two scenarios themselves — both tuned
/// to ~38 ticks/s, one starved by the scheduler and one by its own work.
/// </remarks>
public sealed class PresenterTickMeterTests
{
    // 1 tick = 1 microsecond, so timestamps in these tests read as microseconds.
    private const long Freq = 1_000_000;

    private static long Us(double ms) => (long)(ms * 1000);

    [Fact]
    public void ReportsNothingUntilTheWindowCloses()
    {
        var meter = new PresenterTickMeter(Freq, reportEvery: 3);

        Assert.Null(meter.Record(Us(0), Us(1), hadFrame: true));
        Assert.Null(meter.Record(Us(16), Us(17), hadFrame: true));
        Assert.NotNull(meter.Record(Us(32), Us(33), hadFrame: true));
    }

    [Fact]
    public void SlowSchedulingWithCheapWork_ShowsUpAsGap()
    {
        // Explanation 1: the handler is cheap and simply is not being run often enough.
        var report = RunSteady(gapMs: 25.7, workMs: 0.3);

        Assert.Equal(38.5, report.TicksPerSecond, 0);
        Assert.Equal(25.7, report.GapMeanMs, 1);
        Assert.Equal(0.3, report.WorkMeanMs, 1);
    }

    [Fact]
    public void FastSchedulingWithExpensiveWork_ShowsUpAsWork()
    {
        // Explanation 2: the scheduler hands the tick back immediately and the work itself
        // fills the frame budget. Same ~38 ticks/s on screen as the case above.
        var report = RunSteady(gapMs: 1, workMs: 25);

        Assert.Equal(38.5, report.TicksPerSecond, 0);
        Assert.Equal(1, report.GapMeanMs, 1);
        Assert.Equal(25, report.WorkMeanMs, 1);
    }

    [Fact]
    public void TheTwoCausesAreDistinguishableAtTheSameFrameRate()
    {
        // The justification for the whole class: identical rate, opposite reports.
        var scheduler = RunSteady(gapMs: 25.7, workMs: 0.3);
        var work = RunSteady(gapMs: 1, workMs: 25);

        Assert.Equal(scheduler.TicksPerSecond, work.TicksPerSecond, 0);
        Assert.True(scheduler.GapMeanMs > scheduler.WorkMeanMs * 10);
        Assert.True(work.WorkMeanMs > work.GapMeanMs * 10);
    }

    [Fact]
    public void AHealthyPresenter_ShowsBothNumbersSmall()
    {
        var report = RunSteady(gapMs: 14.7, workMs: 2);

        Assert.Equal(60, report.TicksPerSecond, 0);
        Assert.Equal(14.7, report.GapMeanMs, 1);
        Assert.Equal(2, report.WorkMeanMs, 1);
    }

    [Fact]
    public void IdleTicksDoNotDiluteTheWorkFigures()
    {
        // Nineteen idle ticks beside one expensive present. Averaging the idle ones in would
        // report ~1.3ms and hide the expensive case exactly when delivery is sparse.
        var meter = new PresenterTickMeter(Freq, reportEvery: 20);
        PresenterTickReport? report = null;
        double t = 0;

        for (int i = 0; i < 20; i++)
        {
            var work = i == 0 ? 25.0 : 0.1;
            report = meter.Record(Us(t), Us(t + work), hadFrame: i == 0);
            t += work + 1;
        }

        Assert.Equal(20, report!.Value.Ticks);
        Assert.Equal(1, report.Value.TicksWithFrame);
        Assert.Equal(25, report.Value.WorkMeanMs, 1);
        Assert.Equal(25, report.Value.WorkMaxMs, 1);
    }

    [Fact]
    public void AWindowWithNoFramesReportsZeroWorkRatherThanDividingByZero()
    {
        var meter = new PresenterTickMeter(Freq, reportEvery: 3);
        long t = 0;
        PresenterTickReport? report = null;

        for (int i = 0; i < 3; i++)
        {
            report = meter.Record(Us(t), Us(t + 1), hadFrame: false);
            t += 16;
        }

        Assert.Equal(0, report!.Value.TicksWithFrame);
        Assert.Equal(0, report.Value.WorkMeanMs);
        Assert.Equal(0, report.Value.WorkMaxMs);
    }

    [Fact]
    public void MaximaSurviveAveraging()
    {
        var meter = new PresenterTickMeter(Freq, reportEvery: 4);
        long t = 0;

        meter.Record(Us(t), Us(t + 2), true);
        t += 16;
        meter.Record(Us(t), Us(t + 2), true);
        t += 16;
        meter.Record(Us(t), Us(t + 40), true); // one expensive tick
        t += 160; // and one long scheduler stall
        var report = meter.Record(Us(t), Us(t + 2), true);

        Assert.NotNull(report);
        Assert.Equal(40, report!.Value.WorkMaxMs, 1);
        Assert.True(report.Value.GapMaxMs > 100);
        // A single outlier must not dominate the mean, or the report reads as a stall.
        Assert.True(report.Value.GapMeanMs < 70);
    }

    [Fact]
    public void EachWindowDescribesOnlyItself()
    {
        // The caller spends time emitting a report, and that time is not scheduler delay.
        // The first tick of a new window contributes work but no gap.
        var meter = new PresenterTickMeter(Freq, reportEvery: 2);
        long t = 0;

        meter.Record(Us(t), Us(t + 1), true);
        t += 16;
        meter.Record(Us(t), Us(t + 1), true); // closes window 1

        t += 5000; // a long report/flush, then the next window
        meter.Record(Us(t), Us(t + 1), true);
        t += 16;
        var second = meter.Record(Us(t), Us(t + 1), true);

        Assert.NotNull(second);
        Assert.Equal(15, second!.Value.GapMeanMs, 1); // the 5 s straddle is not counted
        Assert.True(second.Value.GapMaxMs < 20);
    }

    [Fact]
    public void ResetDiscardsAPartialWindow()
    {
        var meter = new PresenterTickMeter(Freq, reportEvery: 3);

        meter.Record(Us(0), Us(1), true);
        meter.Record(Us(16), Us(17), true);

        // A detach: what follows is a new session, and the gap across it is idle time rather
        // than scheduler delay.
        meter.Reset();

        Assert.Null(meter.Record(Us(600_000), Us(600_001), true));
        Assert.Null(meter.Record(Us(600_016), Us(600_017), true));
        var report = meter.Record(Us(600_032), Us(600_033), true);

        Assert.NotNull(report);
        Assert.Equal(3, report!.Value.Ticks);
        Assert.True(report.Value.GapMaxMs < 20); // no 10-minute straddle
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsAnInvalidFrequency(long frequency) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenterTickMeter(frequency));

    [Fact]
    public void RejectsAnInvalidWindow() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenterTickMeter(Freq, 0));

    /// <summary>Ticks at a fixed scheduler gap and a fixed per-tick cost.</summary>
    private static PresenterTickReport RunSteady(double gapMs, double workMs)
    {
        var meter = new PresenterTickMeter(Freq, reportEvery: 20);
        PresenterTickReport? report = null;
        double t = 0;

        for (int i = 0; i < 20; i++)
        {
            report = meter.Record(Us(t), Us(t + workMs), hadFrame: true);
            t += workMs + gapMs;
        }

        Assert.NotNull(report);
        return report!.Value;
    }
}
