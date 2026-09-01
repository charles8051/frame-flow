// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Diagnostics;

/// <summary>
/// What one <see cref="DiagnosticsObservation"/> is about. The kind, not the message, is what a
/// consumer should branch on — the wording is free to change.
/// </summary>
public enum DiagnosticsObservationKind
{
    /// <summary>The demuxer performed one or more seeks.</summary>
    DemuxSeeks,

    /// <summary>The demuxer reached the end of the stream during this interval.</summary>
    EndOfStream,

    /// <summary>The video decoder rejected packets.</summary>
    VideoDecodeErrors,

    /// <summary>The audio decoder rejected packets.</summary>
    AudioDecodeErrors,

    /// <summary>The video decoder discarded packets because the channel was full.</summary>
    VideoPacketsShed,

    /// <summary>The playback layer discarded frames the sync strategy ruled too late.</summary>
    FramesDroppedForSync,

    /// <summary>The video sink superseded frames the render path had not consumed.</summary>
    SinkFramesDropped,

    /// <summary>The audio device ran out of buffered samples.</summary>
    AudioUnderruns,

    /// <summary>Audio writes blocked waiting for the device to drain.</summary>
    AudioBackpressure,

    /// <summary>A single-item loop stopped restarting.</summary>
    LoopStalled,
}

/// <summary>
/// How much a consumer should care. Assigned by <see cref="DiagnosticsInterpreter"/> so that
/// every consumer does not re-derive it, which is the rediscovery this type exists to stop.
/// </summary>
public enum DiagnosticsObservationSeverity
{
    /// <summary>Normal operation. Reported because it explains a counter moving, not because it is wrong.</summary>
    Info,

    /// <summary>Output was degraded but playback continued. Frames or samples were lost.</summary>
    Warning,

    /// <summary>Data was rejected, or playback is not progressing.</summary>
    Error,
}

/// <summary>
/// One thing that happened between two <see cref="PlaybackDiagnosticsSnapshot"/> polls.
/// </summary>
/// <param name="Kind">What the observation is about. Branch on this, not on <paramref name="Message"/>.</param>
/// <param name="Severity">How much to care.</param>
/// <param name="Delta">
/// How much the underlying counter moved. <c>1</c> for the edge-triggered observations
/// (<see cref="DiagnosticsObservationKind.EndOfStream"/>,
/// <see cref="DiagnosticsObservationKind.LoopStalled"/>), which have no counter.
/// </param>
/// <param name="Message">
/// A sentence naming what moved and which stage to look at. Presentation only — the wording is
/// not a contract.
/// </param>
public readonly record struct DiagnosticsObservation(
    DiagnosticsObservationKind Kind,
    DiagnosticsObservationSeverity Severity,
    long Delta,
    string Message
);

/// <summary>
/// The result of comparing two <see cref="PlaybackDiagnosticsSnapshot"/> polls: either a list of
/// <see cref="DiagnosticsObservation"/>, or a reset saying the pair is not comparable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reset is a value, not an empty list.</b> A pair whose
/// <see cref="PlaybackDiagnosticsSnapshot.SessionGeneration"/> differs straddles a <c>load</c>,
/// and half its counters restarted at zero. The caller has to handle that case to get past this
/// type, which is the point: the two available shortcuts are both wrong.
/// </para>
/// <list type="bullet">
/// <item>Subtracting anyway yields negative deltas, and reports a session restart as an error or
/// drop burst.</item>
/// <item>Reporting only increases avoids that false alarm by accident and buys a false negative:
/// the new session's counters climb from zero back toward the old session's values, so every
/// genuine error in that first interval is swallowed until the count passes the previous
/// high-water mark.</item>
/// </list>
/// </remarks>
public sealed record DiagnosticsDelta
{
    private static readonly IReadOnlyList<DiagnosticsObservation> NoObservations = [];

    private DiagnosticsDelta(
        bool isReset,
        int fromGeneration,
        int toGeneration,
        IReadOnlyList<DiagnosticsObservation> observations
    )
    {
        IsReset = isReset;
        FromGeneration = fromGeneration;
        ToGeneration = toGeneration;
        Observations = observations;
    }

    /// <summary>
    /// Whether the two snapshots came from different sessions. When set,
    /// <see cref="Observations"/> is empty and carries no meaning — the interval cannot be
    /// measured, which is different from having measured it and found nothing.
    /// </summary>
    public bool IsReset { get; }

    /// <summary>The generation of the earlier snapshot.</summary>
    public int FromGeneration { get; }

    /// <summary>The generation of the later snapshot.</summary>
    public int ToGeneration { get; }

    /// <summary>
    /// What happened over the interval, in the order
    /// <see cref="DiagnosticsInterpreter.Compare"/> evaluates them: source, then decode, then
    /// pacing, then output. Empty when nothing moved. Always empty when <see cref="IsReset"/>.
    /// </summary>
    public IReadOnlyList<DiagnosticsObservation> Observations { get; }

    /// <summary>
    /// A sentence explaining the reset, for a consumer that reports one to a human.
    /// <see langword="null"/> when <see cref="IsReset"/> is false.
    /// </summary>
    public string? ResetMessage =>
        IsReset
            ? $"Playback session changed between polls (generation {FromGeneration} → {ToGeneration}); "
                + "demux and decoder counters restarted at zero, so the interval cannot be measured."
            : null;

    /// <summary>Builds the not-comparable result for a pair that straddles a session change.</summary>
    public static DiagnosticsDelta Reset(int fromGeneration, int toGeneration) =>
        new(
            isReset: true,
            fromGeneration: fromGeneration,
            toGeneration: toGeneration,
            observations: NoObservations
        );

    /// <summary>Builds the measured result for a comparable pair.</summary>
    public static DiagnosticsDelta Observed(
        int generation,
        IReadOnlyList<DiagnosticsObservation> observations
    ) =>
        new(
            isReset: false,
            fromGeneration: generation,
            toGeneration: generation,
            observations: observations ?? NoObservations
        );
}
