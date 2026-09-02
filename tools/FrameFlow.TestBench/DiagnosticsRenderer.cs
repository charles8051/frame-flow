using System.Text;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;

namespace FrameFlow.TestBench;

/// <summary>
/// Formats controller state and diagnostics for the console.
/// </summary>
/// <remarks>
/// Static and snapshot-driven, so every method here is testable from a constructed
/// <see cref="PlaybackDiagnosticsSnapshot"/> without a pipeline behind it.
/// </remarks>
internal static class DiagnosticsRenderer
{
    /// <summary>One line: where the session is.</summary>
    internal static string Status(IPlaybackController controller) =>
        $"{controller.State} {Time(controller.Position)}/{Time(controller.Duration)}"
        + (controller.SeekingState == SeekState.NotSeeking ? "" : $" [{controller.SeekingState}]")
        + (controller.RepeatMode == RepeatMode.Off ? "" : $" repeat={controller.RepeatMode}");

    /// <summary>
    /// The counters worth reading at a glance, grouped by where in the pipeline they sit.
    /// </summary>
    /// <remarks>
    /// Ordered demux, decode, sink, sync — the direction a frame travels, so a reader
    /// scanning down finds the first stage where the numbers stop making sense.
    /// </remarks>
    internal static string Summary(
        PlaybackDiagnosticsSnapshot snapshot,
        PresenterSelection presenter,
        HeadlessVideoSink? headless = null
    )
    {
        var stream = snapshot.Pipeline.Stream;
        var sink = snapshot.Pipeline.VideoSink;
        var text = new StringBuilder();

        text.AppendLine(
            $"  state     {snapshot.State} {Time(snapshot.Position)}/{Time(snapshot.Duration)}"
                + $"  gen={snapshot.SessionGeneration}"
        );
        text.AppendLine(
            $"  demux     packets={stream.Demux.PacketsRead} bytes={stream.Demux.BytesRead} "
                + $"seeks={stream.Demux.SeeksPerformed} eof={stream.Demux.EndOfStreamReached}"
        );
        text.AppendLine(
            $"  video     decoded={stream.VideoDecoder.FramesDecoded} "
                + $"errors={stream.VideoDecoder.DecodeErrors} "
                + $"shed={stream.VideoDecoder.PacketsDroppedForBackpressure} "
                + $"backend={stream.VideoDecoder.HardwareBackend?.ToString() ?? "software"}"
        );
        text.AppendLine(
            $"  audio     decoded={stream.AudioDecoder.BuffersDecoded} "
                + $"errors={stream.AudioDecoder.DecodeErrors} "
                + $"active={snapshot.Pipeline.AudioSink.IsActive}"
        );
        text.AppendLine(
            $"  sink      presented={sink.FramesPresented} dropped={sink.FramesDropped} "
                + $"sync-dropped={snapshot.Pipeline.VideoFramesDroppedForSync}"
        );

        // FramesCommitted is populated only by the zero-copy compositor presenter, and
        // every other sink leaves it at 0 — which cannot be told apart from a compositor
        // that committed nothing. The ADR carries that as an open question against the
        // snapshot. The bench cannot fix the field, but it does know which surface it
        // built, so it prints the number where the number means something and says
        // nothing where it would only mislead.
        text.AppendLine(
            presenter.Resolved == PresenterKind.Gpu
                ? $"  committed {sink.FramesCommitted}"
                    + (
                        sink.LastCommittedAtUtc is { } at
                            ? $"  last={at:HH:mm:ss.fff}"
                            : "  (nothing committed yet)"
                    )
                : $"  committed n/a — only the gpu presenter populates it"
                    + $" (this run is {presenter.Resolved.ToString().ToLowerInvariant()})"
        );

        // The headless sink's abandoned count is deliberately not FramesDropped: dropped
        // means the render path was the bottleneck, and there is no render tick here to
        // fall behind. Printing it beside them keeps that distinction visible rather
        // than letting a reader assume zero drops means nothing was lost.
        if (headless is not null)
            text.AppendLine($"  headless  abandoned={headless.AbandonedCount}");

        if (snapshot.AvSyncDrift is { } drift)
            text.AppendLine($"  drift     {Time(drift)}");

        return text.ToString().TrimEnd();
    }

    /// <summary>The whole snapshot, for when the summary has left something out.</summary>
    internal static string Full(
        PlaybackDiagnosticsSnapshot snapshot,
        PresenterSelection presenter,
        HeadlessVideoSink? headless = null
    ) =>
        Summary(snapshot, presenter, headless)
        + Environment.NewLine
        + Environment.NewLine
        + "  "
        + snapshot;

    /// <summary>The presenter line printed at startup and by <c>status</c>.</summary>
    internal static string Presenter(PresenterSelection presenter) =>
        presenter.FellBack
            ? $"presenter {presenter.Resolved.ToString().ToLowerInvariant()} "
                + $"— requested {presenter.Requested.ToString().ToLowerInvariant()}, "
                + $"{presenter.Reason}"
            : $"presenter {presenter.Resolved.ToString().ToLowerInvariant()}";

    /// <summary>
    /// What moved between two <c>diag</c> polls, in sentences rather than numbers.
    /// </summary>
    /// <remarks>
    /// <see cref="DiagnosticsInterpreter"/> is the counter-delta knowledge Decision 5
    /// moved into the library. A reset is a value here rather than an empty list, and is
    /// reported as one: the pair straddles a <c>load</c> and half its counters restarted
    /// at zero, so "nothing moved" would be a lie in exactly the interval most likely to
    /// be interesting.
    /// </remarks>
    internal static string Interval(
        PlaybackDiagnosticsSnapshot before,
        PlaybackDiagnosticsSnapshot after
    )
    {
        var delta = DiagnosticsInterpreter.Compare(before, after);

        if (delta.IsReset)
            return $"  since last diag: {delta.ResetMessage}";

        if (delta.Observations.Count == 0)
            return "  since last diag: nothing of note.";

        var text = new StringBuilder("  since last diag:");
        foreach (var observation in delta.Observations)
        {
            text.AppendLine();
            text.Append($"    [{observation.Severity}] {observation.Message}");
        }
        return text.ToString();
    }

    private static string Time(TimeSpan value) =>
        value < TimeSpan.Zero
            ? "-" + (-value).ToString(@"mm\:ss\.fff")
            : value.ToString(@"mm\:ss\.fff");
}
