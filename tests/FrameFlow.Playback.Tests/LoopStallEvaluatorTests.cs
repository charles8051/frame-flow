using System.Diagnostics;
using FrameFlow.Playback;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Exhaustive, clock-free tests of the pure <see cref="LoopStallEvaluator"/> — the
/// loop-stall detector's functional core. The actual flaky-loop failure is hard to
/// reproduce on demand, so the detection logic is verified deterministically here
/// by folding hand-built sample sequences.
/// </summary>
public class LoopStallEvaluatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly long DurationTicks = TimeSpan.FromSeconds(10).Ticks;

    // Stopwatch-tick clock for the NowTicks field.
    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    // Position in TimeSpan ticks.
    private static long Pos(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    private static LoopStallSample Sample(
        double nowSec,
        double posSec,
        int loopCount,
        bool repeatOne = true,
        bool playing = true,
        long durationTicks = -1
    ) =>
        new(
            NowTicks: At(nowSec),
            PositionTicks: Pos(posSec),
            DurationTicks: durationTicks < 0 ? DurationTicks : durationTicks,
            RepeatOne: repeatOne,
            Playing: playing,
            LoopCount: loopCount
        );

    [Fact]
    public void WithinDuration_NeverStalls()
    {
        var ev = LoopStallEvaluator.Create(Timeout);
        var outcome = ev.Observe(Sample(nowSec: 100, posSec: 5, loopCount: 0));
        Assert.False(outcome.Stalled);
    }

    [Fact]
    public void JustPastEnd_BelowTimeout_NotYetStalled()
    {
        var ev = LoopStallEvaluator.Create(Timeout);

        // Episode opens at t=0 with position just past the 10s duration.
        var o0 = ev.Observe(Sample(nowSec: 0, posSec: 10.2, loopCount: 3));
        Assert.False(o0.Stalled);

        // 1.0s later, still overrun, loop count unchanged — under the 2s timeout.
        var o1 = o0.Next.Observe(Sample(nowSec: 1.0, posSec: 11.2, loopCount: 3));
        Assert.False(o1.Stalled);
    }

    [Fact]
    public void PersistentOverrun_PastTimeout_Stalls()
    {
        var ev = LoopStallEvaluator.Create(Timeout);

        var o0 = ev.Observe(Sample(nowSec: 0, posSec: 10.2, loopCount: 3));
        var o1 = o0.Next.Observe(Sample(nowSec: 1.0, posSec: 11.2, loopCount: 3));
        // 2.5s of continuous overrun with no loop restart → stalled.
        var o2 = o1.Next.Observe(Sample(nowSec: 2.5, posSec: 12.7, loopCount: 3));

        Assert.True(o2.Stalled);
        Assert.True(o2.OverrunTicks >= At(2.0));
    }

    [Fact]
    public void LoopCountAdvances_DuringOverrun_ResetsAndDoesNotStall()
    {
        var ev = LoopStallEvaluator.Create(Timeout);

        // Episode opens at loop count 3.
        var o0 = ev.Observe(Sample(nowSec: 0, posSec: 10.2, loopCount: 3));
        // Much later and still reading past-end, but the loop counter advanced
        // (a healthy wrap is in flight) → reset, not stalled.
        var o1 = o0.Next.Observe(Sample(nowSec: 5.0, posSec: 10.1, loopCount: 4));

        Assert.False(o1.Stalled);
    }

    [Fact]
    public void HealthyWrap_PositionResets_NeverStalls()
    {
        var ev = LoopStallEvaluator.Create(Timeout);

        // A healthy loop: position climbs toward the end, then wraps to ~0 each cycle.
        var s = ev;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var baseT = cycle * 10.0;
            var a = s.Observe(Sample(nowSec: baseT + 9.5, posSec: 9.5, loopCount: cycle));
            Assert.False(a.Stalled);
            var b = a.Next.Observe(Sample(nowSec: baseT + 10.0, posSec: 0.1, loopCount: cycle + 1));
            Assert.False(b.Stalled);
            s = b.Next;
        }
    }

    [Fact]
    public void NotRepeatOne_NeverStalls()
    {
        var ev = LoopStallEvaluator.Create(Timeout);
        var o0 = ev.Observe(Sample(nowSec: 0, posSec: 10.2, loopCount: 0, repeatOne: false));
        var o1 = o0.Next.Observe(Sample(nowSec: 10, posSec: 20, loopCount: 0, repeatOne: false));
        Assert.False(o1.Stalled);
    }

    [Fact]
    public void NotPlaying_NeverStalls()
    {
        var ev = LoopStallEvaluator.Create(Timeout);
        var o0 = ev.Observe(Sample(nowSec: 0, posSec: 10.2, loopCount: 0, playing: false));
        var o1 = o0.Next.Observe(Sample(nowSec: 10, posSec: 20, loopCount: 0, playing: false));
        Assert.False(o1.Stalled);
    }

    [Fact]
    public void UnknownDuration_NeverStalls()
    {
        var ev = LoopStallEvaluator.Create(Timeout);
        var o0 = ev.Observe(Sample(nowSec: 0, posSec: 10.2, loopCount: 0, durationTicks: 0));
        var o1 = o0.Next.Observe(Sample(nowSec: 10, posSec: 9999, loopCount: 0, durationTicks: 0));
        Assert.False(o1.Stalled);
    }

    [Fact]
    public void RecoveryThenRelapse_CanStallAgain()
    {
        var ev = LoopStallEvaluator.Create(Timeout);

        // First stall.
        var a = ev.Observe(Sample(nowSec: 0, posSec: 10.2, loopCount: 1));
        var b = a.Next.Observe(Sample(nowSec: 2.5, posSec: 12.7, loopCount: 1));
        Assert.True(b.Stalled);

        // Recovery (back within duration after a manual seek / unload-reload).
        var c = b.Next.Observe(Sample(nowSec: 3.0, posSec: 1.0, loopCount: 1));
        Assert.False(c.Stalled);

        // Relapse: overruns again and persists past the timeout → stalls once more.
        var d = c.Next.Observe(Sample(nowSec: 4.0, posSec: 10.2, loopCount: 1));
        var e = d.Next.Observe(Sample(nowSec: 6.5, posSec: 12.7, loopCount: 1));
        Assert.True(e.Stalled);
    }
}
