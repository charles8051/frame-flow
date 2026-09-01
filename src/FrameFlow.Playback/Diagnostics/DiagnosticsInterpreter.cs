// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Diagnostics;

/// <summary>
/// Turns a pair of <see cref="PlaybackDiagnosticsSnapshot"/> polls into
/// <see cref="DiagnosticsObservation"/>s: which counters moved, and which stage of the pipeline
/// that blames.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is in the library.</b> Reading the ADR-0034 snapshot correctly means knowing that
/// <c>VideoDecoder.DecodeErrors</c> blames the file while <c>VideoSink.FramesDropped</c> blames
/// the render path, and that <c>PacketsDroppedForBackpressure</c> is a symptom of something
/// downstream rather than a decoder fault. Shipping the snapshot without shipping that knowledge
/// means every consumer rediscovers it, and they do.
/// </para>
/// <para>
/// <b>Pure and total.</b> No clock, no IO, no state. The same pair always yields the same result,
/// so it is directly unit-testable and safe to call from a poll loop.
/// </para>
/// <para>
/// <b>Positive deltas and rising edges only.</b> Every observation here is well-defined without
/// knowing how much time passed, because the snapshots do not carry a wallclock. "Nothing was
/// decoded" and "nothing reached the screen" are deliberately absent: whether that is a freeze or
/// a normal gap between two fast polls depends on the interval. Those questions have their own
/// answers already — <see cref="PlaybackDiagnosticsSnapshot.LoopStalled"/> on this snapshot, and
/// <c>PresenterStallWatchdog</c> for the presenter — both of which carry a timeout.
/// </para>
/// </remarks>
public static class DiagnosticsInterpreter
{
    /// <summary>
    /// Compares two polls of the same playback controller.
    /// </summary>
    /// <param name="before">The earlier snapshot.</param>
    /// <param name="after">The later snapshot.</param>
    /// <returns>
    /// <see cref="DiagnosticsDelta.Reset"/> when the two snapshots came from different sessions,
    /// otherwise the observations for the interval. An empty observation list means the interval
    /// was measured and nothing of note moved, which a reset does not.
    /// </returns>
    /// <remarks>
    /// Counters are monotonic within a session, so a negative delta cannot happen on a comparable
    /// pair. If one appears anyway it is ignored rather than reported: it means a counter was
    /// reset without the generation moving, and inventing an observation out of that would report
    /// a bug in the producer as an event in the media.
    /// </remarks>
    public static DiagnosticsDelta Compare(
        PlaybackDiagnosticsSnapshot before,
        PlaybackDiagnosticsSnapshot after
    )
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (before.SessionGeneration != after.SessionGeneration)
            return DiagnosticsDelta.Reset(before.SessionGeneration, after.SessionGeneration);

        var observations = new List<DiagnosticsObservation>();

        var beforeStream = before.Pipeline.Stream;
        var afterStream = after.Pipeline.Stream;

        // ── Source ───────────────────────────────────────────────────────────────────────
        Add(
            observations,
            DiagnosticsObservationKind.DemuxSeeks,
            DiagnosticsObservationSeverity.Info,
            Rise(beforeStream.Demux.SeeksPerformed, afterStream.Demux.SeeksPerformed),
            n => $"Demuxer seeked {Times(n)}."
        );

        if (!beforeStream.Demux.EndOfStreamReached && afterStream.Demux.EndOfStreamReached)
        {
            observations.Add(
                new DiagnosticsObservation(
                    DiagnosticsObservationKind.EndOfStream,
                    DiagnosticsObservationSeverity.Info,
                    1,
                    "Demuxer reached end of stream."
                )
            );
        }

        // ── Decode ───────────────────────────────────────────────────────────────────────
        Add(
            observations,
            DiagnosticsObservationKind.VideoDecodeErrors,
            DiagnosticsObservationSeverity.Error,
            Rise(beforeStream.VideoDecoder.DecodeErrors, afterStream.VideoDecoder.DecodeErrors),
            n =>
                $"Video decoder rejected {Packets(n)} — the source stream or the decoder, not the render path."
        );

        Add(
            observations,
            DiagnosticsObservationKind.AudioDecodeErrors,
            DiagnosticsObservationSeverity.Error,
            Rise(beforeStream.AudioDecoder.DecodeErrors, afterStream.AudioDecoder.DecodeErrors),
            n =>
                $"Audio decoder rejected {Packets(n)} — the source stream or the decoder, not the audio device."
        );

        // ── Pacing and backpressure ──────────────────────────────────────────────────────
        Add(
            observations,
            DiagnosticsObservationKind.VideoPacketsShed,
            DiagnosticsObservationSeverity.Warning,
            Rise(
                beforeStream.VideoDecoder.PacketsDroppedForBackpressure,
                afterStream.VideoDecoder.PacketsDroppedForBackpressure
            ),
            n =>
                $"Video decoder shed {Packets(n)} because the channel was full — something downstream is not keeping up, not a decode fault."
        );

        Add(
            observations,
            DiagnosticsObservationKind.FramesDroppedForSync,
            DiagnosticsObservationSeverity.Warning,
            Rise(
                before.Pipeline.VideoFramesDroppedForSync,
                after.Pipeline.VideoFramesDroppedForSync
            ),
            n =>
                $"Playback discarded {Frames(n)} as too late to present — pacing or A/V sync, upstream of the sink."
        );

        // ── Output ───────────────────────────────────────────────────────────────────────
        Add(
            observations,
            DiagnosticsObservationKind.SinkFramesDropped,
            DiagnosticsObservationSeverity.Warning,
            Rise(before.Pipeline.VideoSink.FramesDropped, after.Pipeline.VideoSink.FramesDropped),
            n =>
                $"Video sink superseded {Frames(n)} the render path had not consumed — the render path is the bottleneck."
        );

        Add(
            observations,
            DiagnosticsObservationKind.AudioUnderruns,
            DiagnosticsObservationSeverity.Warning,
            Rise(before.Pipeline.AudioSink.UnderrunCount, after.Pipeline.AudioSink.UnderrunCount),
            n =>
                $"Audio device underran {Times(n)} — it ran out of buffered samples, so the master clock stalled."
        );

        Add(
            observations,
            DiagnosticsObservationKind.AudioBackpressure,
            DiagnosticsObservationSeverity.Info,
            Rise(
                before.Pipeline.AudioSink.BackpressureEvents,
                after.Pipeline.AudioSink.BackpressureEvents
            ),
            n => $"Audio writes blocked {Times(n)} waiting for the device to drain."
        );

        // ── Loop ─────────────────────────────────────────────────────────────────────────
        if (!before.LoopStalled && after.LoopStalled)
        {
            var overrun = after.LoopOverrun;
            var forHowLong =
                overrun is { } o ? $" Position has been past the item duration for {o.TotalSeconds:F1}s." : string.Empty;
            observations.Add(
                new DiagnosticsObservation(
                    DiagnosticsObservationKind.LoopStalled,
                    DiagnosticsObservationSeverity.Error,
                    1,
                    $"Single-item loop stopped restarting.{forHowLong}"
                )
            );
        }

        return DiagnosticsDelta.Observed(after.SessionGeneration, observations);
    }

    /// <summary>
    /// The forward movement of a monotonic counter, or <c>0</c> if it did not move or went
    /// backwards. See the remarks on <see cref="Compare"/> for why backwards is swallowed.
    /// </summary>
    private static long Rise(long before, long after) => after > before ? after - before : 0;

    private static void Add(
        List<DiagnosticsObservation> into,
        DiagnosticsObservationKind kind,
        DiagnosticsObservationSeverity severity,
        long delta,
        Func<long, string> message
    )
    {
        if (delta > 0)
            into.Add(new DiagnosticsObservation(kind, severity, delta, message(delta)));
    }

    private static string Times(long n) => n == 1 ? "once" : $"{n} times";

    private static string Packets(long n) => n == 1 ? "1 packet" : $"{n} packets";

    private static string Frames(long n) => n == 1 ? "1 frame" : $"{n} frames";
}
