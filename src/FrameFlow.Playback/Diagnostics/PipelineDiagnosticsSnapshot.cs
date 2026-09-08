// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Playback.Diagnostics;

/// <summary>
/// Aggregate snapshot of the playback pipeline state at a single
/// point in time (ADR-0034 + ADR-0036). Composes the decoded-media-
/// stream snapshot (demux + decoders + channel depths) with the
/// playback-layer snapshots (sinks, sync-drop counter) the
/// controller owns directly.
/// </summary>
/// <param name="Stream">
/// Decoded media stream snapshot — demux, decoders, and the
/// pull-shape channel depths. Owned by
/// the active stream; this field
/// folds the decode half of the diagnostics surface under one
/// composite, matching the architectural seam introduced by
/// ADR-0036.
/// </param>
/// <param name="VideoSink">Video sink snapshot, or <see cref="VideoSinkDiagnosticsSnapshot.Empty"/> when running in pull mode (no sink registered).</param>
/// <param name="AudioSink">Audio sink snapshot, or <see cref="AudioSinkDiagnosticsSnapshot.Empty"/> when running in pull mode.</param>
/// <param name="VideoFramesDroppedForSync">
/// Frames the playback layer dropped <i>upstream</i> of the sink
/// because the sync strategy ruled the frame too late to present.
/// Distinct from <see cref="VideoSinkDiagnosticsSnapshot.FramesDropped"/>,
/// which counts frames the sink itself superseded.
/// </param>
public sealed record PipelineDiagnosticsSnapshot(
    DecodedMediaStreamDiagnosticsSnapshot Stream,
    VideoSinkDiagnosticsSnapshot VideoSink,
    AudioSinkDiagnosticsSnapshot AudioSink,
    long VideoFramesDroppedForSync
)
{
    /// <summary>
    /// How far the master clock has run past the end of the frame most recently
    /// presented, or <see langword="null"/> when the run has presented none (and on
    /// sessions with no clock-selecting pacer).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero on a pipeline that is keeping up. A sustained non-zero value means the
    /// reported <c>Position</c> describes a point in the media that the pipeline has
    /// not reached, because the clock advances on its own schedule and nothing is
    /// discarding the backlog to catch up.
    /// </para>
    /// <para>
    /// Read it alongside the two counters that hold it down:
    /// <see cref="VideoFramesDroppedForSync"/> and the decoder's shed count. Lag
    /// climbing while both stay flat is the signature of a pipeline that is falling
    /// behind with no mechanism to compensate, rather than one paying to keep up.
    /// </para>
    /// <para>
    /// Deliberately not a positional parameter. This record is public and shipped,
    /// and a fifth positional value — optional or not — replaces the generated
    /// four-argument constructor and four-output <c>Deconstruct</c> rather than
    /// adding to them, so a binary compiled against the old shape would fail at
    /// runtime and source using four-value deconstruction would stop compiling. An
    /// init-only property is additive on both counts.
    /// </para>
    /// </remarks>
    public TimeSpan? VideoPresentationLag { get; init; }

    /// <summary>
    /// Empty pipeline snapshot used as the rollup seed when the
    /// controller has no live session.
    /// </summary>
    public static PipelineDiagnosticsSnapshot Empty { get; } =
        new(
            Stream: DecodedMediaStreamDiagnosticsSnapshot.Empty,
            VideoSink: VideoSinkDiagnosticsSnapshot.Empty,
            AudioSink: AudioSinkDiagnosticsSnapshot.Empty,
            VideoFramesDroppedForSync: 0
        );
}
