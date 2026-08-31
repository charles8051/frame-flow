// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback.Diagnostics;

/// <summary>
/// Top-level diagnostics snapshot produced by
/// <see cref="IPlaybackController.GetDiagnostics"/> (ADR-0034). Composes the
/// pipeline-level snapshot with controller-owned state (state-machine
/// regions, position, A/V drift) so a single call returns a coherent view
/// of everything observable about the controller.
/// </summary>
/// <param name="State">Current primary playback state.</param>
/// <param name="SeekingState">Current seeking state.</param>
/// <param name="RepeatMode">Current repeat mode.</param>
/// <param name="Position">Position reported by the controller's clock.</param>
/// <param name="Duration">Loaded media duration, or <see cref="TimeSpan.Zero"/>.</param>
/// <param name="MediaInfo">
/// Metadata for the currently loaded media, or <see langword="null"/> when
/// nothing is loaded.
/// </param>
/// <param name="Pipeline">
/// Per-subsystem snapshots composed under the controller's lock so the
/// values are mutually coherent.
/// </param>
/// <param name="AvSyncDrift">
/// Audio-vs-video drift derived from the audio sink's
/// <c>PresentationTime</c> and the video sink's
/// <c>LastPresentedPresentationTime</c>. Positive means video is ahead of
/// audio; negative means video is behind. <see langword="null"/> when
/// either stream hasn't produced timed data yet.
/// </param>
/// <param name="LoopStalled">
/// <see langword="true"/> when a <c>RepeatMode.One</c> loop currently appears
/// stalled — the position has overrun the item duration without a restart
/// (frame delivery stopped while the clock kept advancing). The level-triggered
/// poll counterpart of the edge-triggered <c>IPlaybackController.LoopStalled</c>
/// observable.
/// </param>
/// <param name="LoopOverrun">
/// How long the position has been past the item duration with no restart while
/// <paramref name="LoopStalled"/> is set; <see langword="null"/> otherwise.
/// </param>
public sealed record PlaybackDiagnosticsSnapshot(
    PlaybackState State,
    SeekState SeekingState,
    RepeatMode RepeatMode,
    TimeSpan Position,
    TimeSpan Duration,
    MediaInfo? MediaInfo,
    PipelineDiagnosticsSnapshot Pipeline,
    TimeSpan? AvSyncDrift,
    bool LoopStalled = false,
    TimeSpan? LoopOverrun = null
)
{
    /// <summary>
    /// Empty snapshot used when the controller is unloaded.
    /// </summary>
    public static PlaybackDiagnosticsSnapshot Empty { get; } =
        new(
            State: PlaybackState.Unloaded,
            SeekingState: SeekState.NotSeeking,
            RepeatMode: RepeatMode.Off,
            Position: TimeSpan.Zero,
            Duration: TimeSpan.Zero,
            MediaInfo: null,
            Pipeline: PipelineDiagnosticsSnapshot.Empty,
            AvSyncDrift: null
        );
}
