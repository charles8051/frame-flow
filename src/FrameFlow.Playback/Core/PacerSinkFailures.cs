// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Core;

/// <summary>What the pacer does about one call to its sink that threw (#703).</summary>
internal enum SinkFailureAction
{
    /// <summary>Nothing: the sink has already been reported as faulted, and delivery has stopped.</summary>
    None,

    /// <summary>Report the failure as one playback carries on from, and present the next frame.</summary>
    Report,

    /// <summary>
    /// Report the sink as faulted: it has failed more than
    /// <see cref="PacerSinkFailures.MaxConsecutiveFailures"/> calls in a row.
    /// </summary>
    Escalate,
}

/// <summary>The decision on one failed call to the pacer's sink.</summary>
/// <param name="Action">What the pacer does about it.</param>
/// <param name="InARow">The calls that have failed in a row, this one included.</param>
/// <param name="Warn">
/// Whether this failure is the one logged at Warning. The others are logged at Debug, so a sink
/// that fails on every frame does not write a Warning for each.
/// </param>
/// <param name="NotWarned">
/// The failures since the last Warning that were logged only at Debug, for the Warning to count.
/// </param>
internal readonly record struct SinkFailureVerdict(
    SinkFailureAction Action,
    int InARow,
    bool Warn,
    int NotWarned
);

/// <summary>
/// The calls to a video pacer's sink that have failed in a row, and what each new failure means
/// (#703). Pure: the pacer's history in, a decision out.
/// </summary>
/// <param name="InARow">Calls that have failed since the sink last took a frame.</param>
/// <param name="Faulted">True once a failure has been escalated. Nothing undoes it.</param>
/// <param name="WarnedAt">When a failure was last logged at Warning, or null before the first.</param>
/// <param name="NotWarned">Failures since then that were logged only at Debug.</param>
/// <remarks>
/// <para>
/// <b>What a failure costs.</b> A call to the sink's <c>OnFormatChangedAsync</c> or
/// <c>PresentAsync</c> that throws, including an <see cref="OperationCanceledException"/> the sink
/// raises while the pacer is not stopping, costs the frame it was for. The sink owns that frame
/// from the call (#701), so there is nothing to retry with. The failure is reported, and the pacer
/// presents the next frame.
/// </para>
/// <para>
/// <b>When it escalates.</b> A sink that fails more than <see cref="MaxConsecutiveFailures"/>
/// calls in a row is not going to take the next one either, and reporting each frame it drops
/// would leave the picture frozen behind a stream of errors. The next failure is escalated as a
/// fault, which the session treats like any of its workers faulting. A frame the sink takes
/// without throwing ends the run, so a sink that fails now and then never escalates.
/// </para>
/// <para>
/// <b>Logging.</b> At most one failure in each <see cref="WarningInterval"/> is logged at Warning,
/// and it counts the failures logged at Debug since the one before it. That bounds the Warnings
/// whatever the pattern, a sink that fails on every other frame included, where collapsing each
/// run of failures into one line would still write one for every other frame.
/// </para>
/// </remarks>
internal readonly record struct PacerSinkFailures(
    int InARow,
    bool Faulted,
    TimeSpan? WarnedAt,
    int NotWarned
)
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

    /// <summary>The shortest time between two failures logged at Warning.</summary>
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(5);

    /// <summary>A call to the sink threw at <paramref name="now"/>.</summary>
    /// <param name="now">Elapsed time on any clock that does not go back. Only the logging reads it.</param>
    public (PacerSinkFailures Next, SinkFailureVerdict Verdict) Failed(TimeSpan now)
    {
        if (Faulted)
            return (this, new SinkFailureVerdict(SinkFailureAction.None, InARow, Warn: false, NotWarned));

        var inARow = InARow + 1;
        var escalate = inARow > MaxConsecutiveFailures;
        // The escalation has a Warning of its own, so it does not spend this one.
        var warn = !escalate && (WarnedAt is not { } warnedAt || now - warnedAt >= WarningInterval);
        var next = new PacerSinkFailures(
            inARow,
            Faulted: escalate,
            WarnedAt: warn ? now : WarnedAt,
            NotWarned: warn ? 0 : NotWarned + 1
        );
        return (
            next,
            new SinkFailureVerdict(
                escalate ? SinkFailureAction.Escalate : SinkFailureAction.Report,
                inARow,
                warn,
                NotWarned
            )
        );
    }

    /// <summary>The sink took a frame without throwing: the run of failures, if any, is over.</summary>
    /// <remarks>Once faulted, the count stands: a fault is not taken back.</remarks>
    public PacerSinkFailures Presented() => Faulted ? this : this with { InARow = 0 };
}
