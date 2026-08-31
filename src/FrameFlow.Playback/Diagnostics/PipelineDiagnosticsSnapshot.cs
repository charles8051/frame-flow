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
