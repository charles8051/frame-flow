using FrameFlow.Playback.Core;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// What the pacer does about a sink that throws (#703): each failure is reported, more than
/// <see cref="PacerSinkFailures.MaxConsecutiveFailures"/> in a row escalate, and a frame the sink
/// takes ends the run.
/// </summary>
public sealed class PacerSinkFailuresTests
{
    private const int Max = PacerSinkFailures.MaxConsecutiveFailures;

    /// <summary>
    /// Every sequence of failed and successful calls, long enough for a run of failures, a
    /// success and a second run that escalates. The expected verdicts come from a model that
    /// reads the whole sequence rather than folding it: a failure escalates when it is the first
    /// call to end <see cref="Max"/> + 1 failures in a row, every failure before that is
    /// reported, and nothing after it is.
    /// </summary>
    [Fact]
    public void EverySequence_ReportsEachFailure_EscalatesTheFirstRunPastTheLimit_AndThenNothing()
    {
        const int length = 2 * (Max + 1);
        for (int bits = 0; bits < 1 << length; bits++)
        {
            var failed = new bool[length];
            for (int i = 0; i < length; i++)
                failed[i] = (bits >> i & 1) == 1;

            int escalatesAt = FirstRunPastTheLimit(failed);
            var state = default(PacerSinkFailures);
            for (int i = 0; i < length; i++)
            {
                if (!failed[i])
                {
                    state = state.Presented();
                    continue;
                }

                (state, var verdict) = state.Failed(TimeSpan.Zero);
                var expected =
                    i < escalatesAt ? SinkFailureAction.Report
                    : i == escalatesAt ? SinkFailureAction.Escalate
                    : SinkFailureAction.None;
                if (verdict.Action != expected)
                    Assert.Fail($"{Describe(failed)}: call {i + 1} was {verdict.Action}, expected {expected}.");
                if (expected != SinkFailureAction.None && verdict.InARow != TrailingFailures(failed, i))
                    Assert.Fail($"{Describe(failed)}: call {i + 1} counted {verdict.InARow} in a row.");
            }
        }
    }

    [Fact]
    public void TheLimit_IsThePlaylistsOwn() =>
        Assert.Equal(PlaylistFailureGuard.MaxConsecutiveFailures, PacerSinkFailures.MaxConsecutiveFailures);

    [Fact]
    public void ASuccess_EndsTheRun()
    {
        var state = default(PacerSinkFailures);
        for (int i = 0; i < Max; i++)
            (state, _) = state.Failed(TimeSpan.Zero);

        state = state.Presented();
        var (_, verdict) = state.Failed(TimeSpan.Zero);

        Assert.Equal(SinkFailureAction.Report, verdict.Action);
        Assert.Equal(1, verdict.InARow);
    }

    [Fact]
    public void AFault_IsNotTakenBack()
    {
        var state = default(PacerSinkFailures);
        for (int i = 0; i <= Max; i++)
            (state, _) = state.Failed(TimeSpan.Zero);

        Assert.True(state.Faulted);
        var after = state.Presented();
        var (_, verdict) = after.Failed(TimeSpan.Zero);

        Assert.Equal(state, after);
        Assert.Equal(SinkFailureAction.None, verdict.Action);
        Assert.False(verdict.Warn);
    }

    // ── Logging ─────────────────────────────────────────────────────────

    [Fact]
    public void TheFirstFailure_IsLoggedAtWarning()
    {
        var (_, verdict) = default(PacerSinkFailures).Failed(TimeSpan.FromSeconds(42));

        Assert.True(verdict.Warn);
        Assert.Equal(0, verdict.NotWarned);
    }

    [Fact]
    public void FailuresInsideTheInterval_AreNotLoggedAtWarning_AndTheNextWarningCountsThem()
    {
        var interval = PacerSinkFailures.WarningInterval;
        var (state, first) = default(PacerSinkFailures).Failed(TimeSpan.Zero);
        // A frame between each, so no run reaches the limit: a sink that fails on every other
        // frame is the pattern a Warning per run of failures would still write one for each.
        var warned = new List<bool> { first.Warn };
        for (int i = 1; i <= 3; i++)
        {
            state = state.Presented();
            (state, var verdict) = state.Failed(interval * i / 4);
            warned.Add(verdict.Warn);
        }

        state = state.Presented();
        var (_, atTheInterval) = state.Failed(interval);

        Assert.Equal([true, false, false, false], warned);
        Assert.True(atTheInterval.Warn);
        Assert.Equal(3, atTheInterval.NotWarned);
    }

    [Fact]
    public void TheEscalation_DoesNotSpendTheWarning()
    {
        // The escalation is logged on its own line, so the failure that escalates is not the
        // interval's Warning.
        var state = default(PacerSinkFailures);
        for (int i = 0; i < Max; i++)
            (state, _) = state.Failed(TimeSpan.Zero);

        var (_, verdict) = state.Failed(PacerSinkFailures.WarningInterval);

        Assert.Equal(SinkFailureAction.Escalate, verdict.Action);
        Assert.False(verdict.Warn);
    }

    // ── The model ───────────────────────────────────────────────────────

    // The first index that ends Max + 1 failures in a row, or past the end when none does.
    private static int FirstRunPastTheLimit(bool[] failed)
    {
        for (int i = Max; i < failed.Length; i++)
        {
            bool all = true;
            for (int j = i - Max; j <= i && all; j++)
                all = failed[j];
            if (all)
                return i;
        }
        return failed.Length;
    }

    private static int TrailingFailures(bool[] failed, int at)
    {
        int count = 0;
        for (int i = at; i >= 0 && failed[i]; i--)
            count++;
        return count;
    }

    private static string Describe(bool[] failed) => string.Concat(failed.Select(f => f ? 'F' : 'S'));
}
