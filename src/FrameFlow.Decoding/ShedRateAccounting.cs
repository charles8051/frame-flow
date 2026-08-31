// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>
/// Accumulated packet drops since the current reporting window opened. Threaded through
/// <see cref="ShedRateAccounting.Observe"/>; never mutated in place.
/// </summary>
/// <param name="WindowStartTicks">Timestamp the window opened, from the caller's clock.</param>
/// <param name="LastDropTicks">Timestamp of the most recent drop in the window.</param>
/// <param name="DroppedInWindow">Drops since it opened, including the one that opened it.</param>
/// <param name="Open">False before the first drop, and after a window closes.</param>
public readonly record struct ShedWindow(
    long WindowStartTicks,
    long LastDropTicks,
    long DroppedInWindow,
    bool Open
)
{
    /// <summary>No window yet: the next drop opens one.</summary>
    public static ShedWindow None => new(0, 0, 0, Open: false);
}

/// <summary>One closed window's shedding, ready to report.</summary>
/// <param name="Dropped">Packets shed in the window.</param>
/// <param name="Seconds">How long the window covered.</param>
/// <param name="PerSecond">Shed rate over that span.</param>
/// <param name="TotalDropped">Cumulative drops for the session.</param>
public readonly record struct ShedReport(
    long Dropped,
    double Seconds,
    double PerSecond,
    long TotalDropped
);

/// <summary>
/// Turns individual packet drops into a rate, reported at most once per window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The video decoder counts every packet it sheds to backpressure,
/// and has since long before #145 — but nothing surfaced it. The count was reachable only by
/// polling <c>PollDiagnostics</c>, which meant a pipeline shedding a third of its packets
/// looked healthy in the logs.
/// </para>
/// <para>
/// That silence cost three investigations. #128 diagnosed a presenter ceiling, #125 an audio
/// sink, #145 the audio sink again; the cause was the demux pump shedding video packets, and
/// <c>shed</c> climbing 213 → 640 was sitting in the very first counter dump on #125 the whole
/// time. A line in the log saying "shedding 14 packets/s" would have pointed at it directly.
/// </para>
/// <para>
/// <b>Why a rate and not each drop.</b> Shedding happens in bursts of hundreds; logging per
/// drop would bury the signal it exists to raise. A rate over a window says the thing that
/// matters — whether this is a momentary burst or a pipeline that cannot keep up.
/// </para>
/// <para>
/// <b>Windows are advanced by drops, not by time.</b> A healthy pipeline sheds nothing and so
/// logs nothing, ever.
/// </para>
/// <para>
/// <b>It reports sustained shedding, and only that.</b> A window whose drops are separated by
/// more than the report interval is discarded rather than reported: the earlier shedding has
/// stopped, and describing two drops an hour apart as a rate would be meaningless and, at
/// Warning level, a false alarm. A window closing below <see cref="MinReportableRate"/> is
/// discarded for the same reason.
/// </para>
/// <para>
/// The cost is that an isolated burst — shedding that starts and stops inside one interval — is
/// never reported. That is the right trade for this signal: the failure worth raising is a chain
/// that cannot keep up, and after #147 an isolated burst should not happen at all. The cumulative
/// count stays in <c>PollDiagnostics</c> for anyone who wants every drop.
/// </para>
/// </remarks>
public static class ShedRateAccounting
{
    /// <summary>
    /// Default reporting interval. Long enough that sustained shedding is one line every ten
    /// seconds rather than a flood, short enough to see it start.
    /// </summary>
    public static readonly TimeSpan DefaultReportEvery = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Below this, a closed window is discarded rather than reported. One packet a second is
    /// already a visible defect at any frame rate; anything under it is a straggler rather than
    /// a chain failing to keep up, and does not warrant a Warning that says otherwise.
    /// </summary>
    public const double MinReportableRate = 1.0;

    /// <summary>
    /// Records one shed packet. Returns a report when the window closes, otherwise null.
    /// </summary>
    /// <param name="state">The window carried from the previous drop.</param>
    /// <param name="nowTicks">Monotonic timestamp for this drop.</param>
    /// <param name="ticksPerSecond">Frequency of that timestamp.</param>
    /// <param name="reportEvery">Minimum span a window must cover before it reports.</param>
    /// <param name="totalDropped">Cumulative session drop count, for the report.</param>
    public static (ShedWindow Next, ShedReport? Report) Observe(
        ShedWindow state,
        long nowTicks,
        long ticksPerSecond,
        TimeSpan reportEvery,
        long totalDropped
    )
    {
        var opening = new ShedWindow(nowTicks, nowTicks, 1, Open: true);

        if (!state.Open)
            return (opening, null);

        if (ticksPerSecond <= 0)
            return (Extend(state, nowTicks), null);

        // Shedding stopped and started again: the gap since the last drop exceeds the report
        // interval, so whatever the old window held is over. Discard it and open here. Folding
        // that idle time into a rate would describe a quiet pipeline as a busy one.
        var idleTicks = nowTicks - state.LastDropTicks;
        if (idleTicks > 0 && idleTicks / (double)ticksPerSecond > reportEvery.TotalSeconds)
            return (opening, null);

        var elapsedTicks = nowTicks - state.WindowStartTicks;
        if (elapsedTicks <= 0)
            return (Extend(state, nowTicks), null);

        var seconds = elapsedTicks / (double)ticksPerSecond;
        if (seconds < reportEvery.TotalSeconds)
            return (Extend(state, nowTicks), null);

        var dropped = state.DroppedInWindow + 1;
        var perSecond = dropped / seconds;
        if (perSecond < MinReportableRate)
            return (opening, null);

        // Close it. The next drop opens a fresh window rather than this one continuing, so
        // consecutive reports never overlap and the rate is always over a span of its own.
        return (ShedWindow.None, new ShedReport(dropped, seconds, perSecond, totalDropped));
    }

    private static ShedWindow Extend(ShedWindow state, long nowTicks) =>
        state with { LastDropTicks = nowTicks, DroppedInWindow = state.DroppedInWindow + 1 };
}
