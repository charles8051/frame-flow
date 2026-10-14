// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Core;

/// <summary>What a pacer does about one call to its sink that threw (#703).</summary>
internal enum SinkFailureAction
{
    /// <summary>Drop the frame the call was for, and present the next one.</summary>
    Continue,

    /// <summary>
    /// Report the sink as faulted, and stop: more than
    /// <see cref="SinkFailureRun.MaxConsecutiveFailures"/> calls to it have failed in a row.
    /// </summary>
    Escalate,
}

/// <summary>The decision on one failed call to a video sink.</summary>
/// <param name="Action">What the pacer does about it.</param>
/// <param name="InARow">The calls to the sink that have failed in a row, this one included.</param>
/// <param name="Report">
/// Whether this failure is reported: logged at Warning and raised as an error playback carries on
/// from. At most one in each <see cref="SinkFailureRun.ReportInterval"/> is. The others are logged
/// at Debug. Always false for an escalation, which is reported as a fault instead.
/// </param>
/// <param name="Unreported">
/// The failures since the last one reported that were not, for the report to count.
/// </param>
internal readonly record struct SinkFailureVerdict(
    SinkFailureAction Action,
    int InARow,
    bool Report,
    int Unreported
);

/// <summary>
/// The calls to one video sink that have failed in a row, and what each new failure means (#703).
/// Pure: the history in, a decision out.
/// </summary>
/// <param name="InARow">Calls that have failed since the sink last took a frame.</param>
/// <param name="ReportedAt">When a failure was last reported, or null before the first.</param>
/// <param name="Unreported">Failures since then that were not reported, escalations aside.</param>
/// <remarks>
/// <para>
/// <b>What a failure costs.</b> A call to the sink's <c>OnFormatChangedAsync</c> or
/// <c>PresentAsync</c> that throws, including an <see cref="OperationCanceledException"/> the sink
/// raises while the pacer is not stopping, costs the frame it was for. The sink owns that frame
/// from the call (#701), so there is nothing to retry with, and the pacer presents the next frame.
/// </para>
/// <para>
/// <b>One run per sink.</b> The run belongs to the sink, not to a pacer: a player's items each have
/// a pacer of their own, and an item with fewer frames than the limit, a still for instance, would
/// otherwise never reach it. So the run goes on across items, and a frame the sink takes is the
/// only thing that ends it.
/// </para>
/// <para>
/// <b>When it escalates.</b> A failure that makes more than <see cref="MaxConsecutiveFailures"/> in
/// a row escalates, and the session treats it as a fault in the item it is playing. The sink is
/// <see cref="Faulted"/> from then until it takes a frame: every failure escalates, so an item
/// after the one that faulted faults at its first failure.
/// </para>
/// <para>
/// <b>Reporting.</b> At most one failure in each <see cref="ReportInterval"/> is reported, and the
/// report counts the failures since the one before it. Reporting each one would let a sink that
/// fails eight frames in nine, which never escalates, raise more than fifty errors a second at
/// 60 fps, on the controller's queue that also carries the end of an item and a fault, and a full
/// queue drops what is posted to it. An escalation is reported as the fault it is, whatever the
/// interval, and leaves the count alone.
/// </para>
/// </remarks>
internal readonly record struct SinkFailureRun(int InARow, TimeSpan? ReportedAt, int Unreported)
{
    /// <summary>Failed calls tolerated in a row. The next one escalates.</summary>
    /// <remarks>
    /// The same count, and the same shape, as <see cref="PlaylistFailureGuard.MaxConsecutiveFailures"/>,
    /// the one other run of failures playback tolerates before it gives up. It rides out a sink
    /// that fails a few calls while it rebuilds a surface, after a resize or a lost device, and it
    /// gives up on a sink that cannot present at all within a fraction of a second: nine frames
    /// at 30 fps is 0.3 s.
    /// </remarks>
    internal const int MaxConsecutiveFailures = 8;

    /// <summary>The shortest time between two failures reported.</summary>
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);

    /// <summary>Whether the sink's next failure escalates: it has not taken a frame since one did.</summary>
    public bool Faulted => InARow > MaxConsecutiveFailures;

    /// <summary>A call to the sink threw at <paramref name="now"/>.</summary>
    /// <param name="now">Elapsed time on any clock that does not go back. Only the reporting reads it.</param>
    public (SinkFailureRun Next, SinkFailureVerdict Verdict) Failed(TimeSpan now)
    {
        var inARow = InARow + 1;
        if (inARow > MaxConsecutiveFailures)
            return (
                this with { InARow = inARow },
                new SinkFailureVerdict(SinkFailureAction.Escalate, inARow, Report: false, Unreported)
            );

        var report = ReportedAt is not { } reportedAt || now - reportedAt >= ReportInterval;
        var next = new SinkFailureRun(
            inARow,
            ReportedAt: report ? now : ReportedAt,
            Unreported: report ? 0 : Unreported + 1
        );
        return (next, new SinkFailureVerdict(SinkFailureAction.Continue, inARow, report, Unreported));
    }

    /// <summary>The sink took a frame without throwing: the run of failures, if any, is over.</summary>
    /// <remarks>The report interval runs on, so a sink that fails every other frame is reported on it.</remarks>
    public SinkFailureRun Presented() => this with { InARow = 0 };
}
