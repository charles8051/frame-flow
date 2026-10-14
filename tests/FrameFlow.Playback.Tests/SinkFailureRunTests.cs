using FrameFlow.Playback.Core;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// What a run of failed calls to a video sink means (#703): more than
/// <see cref="SinkFailureRun.MaxConsecutiveFailures"/> in a row escalate, every failure after that
/// escalates until the sink takes a frame, and at most one failure in each
/// <see cref="SinkFailureRun.ReportInterval"/> is reported.
/// </summary>
public sealed class SinkFailureRunTests
{
    private const int Max = SinkFailureRun.MaxConsecutiveFailures;

    /// <summary>
    /// Every sequence of failed and successful calls long enough for a run of failures, a success
    /// and a second run that escalates, checked against a model that reads the whole sequence
    /// rather than folding it: a failure escalates when it ends more than <see cref="Max"/>
    /// failures in a row, and otherwise the frame is dropped and delivery goes on.
    /// </summary>
    [Fact]
    public void EverySequence_EscalatesExactlyTheFailuresThatEndARunPastTheLimit()
    {
        const int length = 2 * (Max + 1) + 1;
        for (int bits = 0; bits < 1 << length; bits++)
        {
            var failed = new bool[length];
            for (int i = 0; i < length; i++)
                failed[i] = (bits >> i & 1) == 1;

            var run = default(SinkFailureRun);
            for (int i = 0; i < length; i++)
            {
                if (!failed[i])
                {
                    run = run.Presented();
                    continue;
                }

                (run, var verdict) = run.Failed(TimeSpan.Zero);
                int inARow = TrailingFailures(failed, i);
                var expected = inARow > Max ? SinkFailureAction.Escalate : SinkFailureAction.Continue;
                if (verdict.Action != expected || verdict.InARow != inARow)
                    Assert.Fail(
                        $"{Describe(failed)}: call {i + 1} was {verdict.Action} at {verdict.InARow} in a row, "
                            + $"expected {expected} at {inARow}."
                    );
                if (run.Faulted != inARow > Max)
                    Assert.Fail($"{Describe(failed)}: after call {i + 1}, Faulted was {run.Faulted}.");
            }
        }
    }

    [Fact]
    public void TheLimit_IsThePlaylistsOwn() =>
        Assert.Equal(PlaylistFailureGuard.MaxConsecutiveFailures, SinkFailureRun.MaxConsecutiveFailures);

    [Fact]
    public void AFaultedSink_EscalatesEveryFailure_UntilItTakesAFrame()
    {
        // The run is the sink's, so the item after the one that faulted faults at its first
        // failure rather than at the limit again.
        var run = default(SinkFailureRun);
        for (int i = 0; i <= Max; i++)
            (run, _) = run.Failed(TimeSpan.Zero);
        Assert.True(run.Faulted);

        (run, var again) = run.Failed(TimeSpan.Zero);
        Assert.Equal(SinkFailureAction.Escalate, again.Action);

        run = run.Presented();
        var (_, afterAFrame) = run.Failed(TimeSpan.Zero);
        Assert.False(run.Faulted);
        Assert.Equal(SinkFailureAction.Continue, afterAFrame.Action);
        Assert.Equal(1, afterAFrame.InARow);
    }

    // ── Reporting ───────────────────────────────────────────────────────

    [Fact]
    public void TheFirstFailure_IsReported()
    {
        var (_, verdict) = default(SinkFailureRun).Failed(TimeSpan.FromSeconds(42));

        Assert.True(verdict.Report);
        Assert.Equal(0, verdict.Unreported);
    }

    [Fact]
    public void FailuresInsideTheInterval_AreNotReported_AndTheNextReportCountsThem()
    {
        // A frame between each, so no run reaches the limit: a sink that fails every other frame
        // never escalates, and reporting each failure would raise an error for half its frames.
        var interval = SinkFailureRun.ReportInterval;
        var (run, first) = default(SinkFailureRun).Failed(TimeSpan.Zero);
        var reported = new List<bool> { first.Report };
        for (int i = 1; i <= 3; i++)
        {
            run = run.Presented();
            (run, var verdict) = run.Failed(interval * i / 4);
            reported.Add(verdict.Report);
        }

        run = run.Presented();
        var (_, atTheInterval) = run.Failed(interval);

        Assert.Equal([true, false, false, false], reported);
        Assert.True(atTheInterval.Report);
        Assert.Equal(3, atTheInterval.Unreported);
    }

    [Fact]
    public void AnEscalation_IsNotTheIntervalsReport_AndIsNotCountedAsUnreported()
    {
        // The escalation is reported as the fault it is, so it neither spends the interval's
        // report nor shows up in the next one's count.
        var run = default(SinkFailureRun);
        for (int i = 0; i < Max; i++)
            (run, _) = run.Failed(TimeSpan.Zero);
        int unreportedBefore = run.Unreported;

        (run, var escalation) = run.Failed(SinkFailureRun.ReportInterval);
        run = run.Presented();
        var (_, next) = run.Failed(SinkFailureRun.ReportInterval);

        Assert.Equal(SinkFailureAction.Escalate, escalation.Action);
        Assert.False(escalation.Report);
        Assert.True(next.Report);
        Assert.Equal(unreportedBefore, next.Unreported);
    }

    // ── The model ───────────────────────────────────────────────────────

    private static int TrailingFailures(bool[] failed, int at)
    {
        int count = 0;
        for (int i = at; i >= 0 && failed[i]; i--)
            count++;
        return count;
    }

    private static string Describe(bool[] failed) => string.Concat(failed.Select(f => f ? 'F' : 'S'));
}
