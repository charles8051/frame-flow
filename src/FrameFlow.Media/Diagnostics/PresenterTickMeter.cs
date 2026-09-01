// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Diagnostics;

/// <summary>
/// Accumulates render-tick timings and emits a summary every N ticks. Pure arithmetic over
/// caller-supplied timestamps — no clock of its own, so it is deterministic under test.
/// </summary>
/// <remarks>
/// <para>
/// Exists to answer one question about the 1080p60 ceiling in issue #128, where both
/// presenters sustain ~38 fps against a 60 fps source. Two explanations fit that number and
/// they need different fixes:
/// </para>
/// <list type="number">
///   <item>
///     The tick is not scheduled at its requested 16 ms, so presented fps is capped however
///     cheap the per-frame work is. The fix is to stop driving presents from a timer and
///     drive them from frame arrival, as <c>FrameFlowVideoView</c> now does.
///   </item>
///   <item>
///     The tick is scheduled on time but the work inside it takes ~26 ms — the Blt, the image
///     import, and the keyed-mutex hand-off. The fix is in that work, not the cadence.
///   </item>
/// </list>
/// <para>
/// <b>Why the gap and not the period.</b> The obvious metric — entry to entry — cannot tell
/// these apart. The handler is serial on the UI thread, so a tick that spends 25 ms working
/// pushes the next entry out by 25 ms whatever the timer wanted; period is delay and cost
/// added together and there is no way back to the two. So the gap measured here is from the
/// <i>previous tick's exit</i> to this tick's entry, which is time the handler was not
/// running and therefore scheduler delay alone. Work is measured separately. A large gap with
/// small work is (1); a small gap with large work is (2). Their sum is the effective period.
/// </para>
/// <para>
/// <b>Work excludes idle ticks.</b> A tick that finds no frame does almost nothing, and
/// averaging those in drags the mean toward zero — nineteen idle ticks at 0.1 ms beside one
/// 25 ms present reads as 1.3 ms and hides the expensive case exactly when delivery is
/// sparse. Work statistics cover frame-bearing ticks only; the idle count is reported beside
/// them, since a tick that finds nothing is evidence about upstream delivery, not about this
/// presenter.
/// </para>
/// <para>
/// <b>Windows do not overlap.</b> Emitting a report costs the caller something (a log write),
/// and counting that as scheduler delay would inflate the next window with the cost of
/// measuring. Each window therefore starts fresh: the first tick after a report contributes
/// its work but no gap. One sample in <c>reportEvery</c> is dropped, and every figure in a
/// report describes that window alone.
/// </para>
/// </remarks>
public sealed class PresenterTickMeter
{
    private readonly double _ticksPerMs;
    private readonly int _reportEvery;

    // Exit timestamp of the previous tick, or -1 at the start of a window. The gap from it to
    // the next entry is the scheduler delay.
    private long _previousExitedTicks = -1;

    private long _windowStartTicks;
    private long _lastEnteredTicks;
    private int _ticks;
    private int _ticksWithFrame;
    private int _gaps;
    private long _gapSumTicks;
    private long _gapMaxTicks;
    private long _workSumTicks;
    private long _workMaxTicks;

    /// <param name="timestampFrequency">
    /// Ticks per second of the caller's clock (<c>Stopwatch.Frequency</c> in production).
    /// </param>
    /// <param name="reportEvery">Ticks per reporting window.</param>
    public PresenterTickMeter(long timestampFrequency, int reportEvery = 120)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timestampFrequency, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reportEvery, 0);

        _ticksPerMs = timestampFrequency / 1000.0;
        _reportEvery = reportEvery;
    }

    /// <summary>
    /// Discards any part-accumulated window. Call when the presenter starts a new session, so
    /// a report cannot span a detach — the gap across it is idle time, not scheduler delay,
    /// and would read as a stall that never happened.
    /// </summary>
    public void Reset()
    {
        _previousExitedTicks = -1;
        ResetWindow();
    }

    /// <summary>
    /// Records one render tick. Returns a report when the window closes, otherwise
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="enteredTicks">Timestamp on entry to the tick handler.</param>
    /// <param name="exitedTicks">Timestamp once the tick's work is done.</param>
    /// <param name="hadFrame">Whether the tick found a frame to present.</param>
    public PresenterTickReport? Record(long enteredTicks, long exitedTicks, bool hadFrame)
    {
        if (_ticks == 0)
            _windowStartTicks = enteredTicks;
        _lastEnteredTicks = enteredTicks;

        if (_previousExitedTicks >= 0)
        {
            var gap = enteredTicks - _previousExitedTicks;
            _gaps++;
            _gapSumTicks += gap;
            if (gap > _gapMaxTicks)
                _gapMaxTicks = gap;
        }

        _ticks++;
        if (hadFrame)
        {
            _ticksWithFrame++;

            var work = exitedTicks - enteredTicks;
            _workSumTicks += work;
            if (work > _workMaxTicks)
                _workMaxTicks = work;
        }

        if (_ticks < _reportEvery)
        {
            _previousExitedTicks = exitedTicks;
            return null;
        }

        // Entry to entry, so the span covers exactly _ticks - 1 whole periods. Measuring to
        // the last exit instead would add one tick's work to the span and report a rate
        // faster than the presenter is actually running.
        var windowMs = (_lastEnteredTicks - _windowStartTicks) / _ticksPerMs;
        var report = new PresenterTickReport(
            Ticks: _ticks,
            TicksWithFrame: _ticksWithFrame,
            WindowMs: windowMs,
            TicksPerSecond: windowMs > 0 ? (_ticks - 1) * 1000.0 / windowMs : 0,
            GapMeanMs: _gaps > 0 ? _gapSumTicks / _ticksPerMs / _gaps : 0,
            GapMaxMs: _gapMaxTicks / _ticksPerMs,
            WorkMeanMs: _ticksWithFrame > 0 ? _workSumTicks / _ticksPerMs / _ticksWithFrame : 0,
            WorkMaxMs: _workMaxTicks / _ticksPerMs
        );

        // Start the next window clean: the caller is about to spend time emitting this
        // report, and that time is not scheduler delay.
        _previousExitedTicks = -1;
        ResetWindow();

        return report;
    }

    private void ResetWindow()
    {
        _ticks = 0;
        _ticksWithFrame = 0;
        _gaps = 0;
        _gapSumTicks = 0;
        _gapMaxTicks = 0;
        _workSumTicks = 0;
        _workMaxTicks = 0;
    }
}

/// <summary>One reporting window's render-tick timings. All durations in milliseconds.</summary>
/// <param name="Ticks">Ticks in the window.</param>
/// <param name="TicksWithFrame">Of those, how many found a frame to present.</param>
/// <param name="WindowMs">Span from the window's first tick entry to its last.</param>
/// <param name="TicksPerSecond">Actual tick cadence — compare against the requested rate.</param>
/// <param name="GapMeanMs">
/// Mean time between one tick finishing and the next starting: scheduler delay, with the
/// handler's own cost excluded.
/// </param>
/// <param name="GapMaxMs">Worst scheduler delay in the window.</param>
/// <param name="WorkMeanMs">Mean time inside the handler, over frame-bearing ticks only.</param>
/// <param name="WorkMaxMs">Worst time inside the handler, over frame-bearing ticks only.</param>
public readonly record struct PresenterTickReport(
    int Ticks,
    int TicksWithFrame,
    double WindowMs,
    double TicksPerSecond,
    double GapMeanMs,
    double GapMaxMs,
    double WorkMeanMs,
    double WorkMaxMs
);
