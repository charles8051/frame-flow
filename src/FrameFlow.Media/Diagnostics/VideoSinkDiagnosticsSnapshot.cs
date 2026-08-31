// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Diagnostics;

/// <summary>
/// Immutable snapshot of an <see cref="IVideoSink"/>'s observable state at a
/// single point in time (ADR-0034).
/// </summary>
/// <param name="FramesPresented">
/// Total frames the sink has handed off for rendering. For sinks that decouple
/// "received" from "drawn" (e.g. Avalonia's interlocked-exchange pattern),
/// this is the *rendered* count, not the *received* count.
/// </param>
/// <param name="FramesDropped">
/// Total frames the sink discarded — typically because the previous frame
/// hadn't been consumed by the render thread and the new one superseded it.
/// Dropped frames at the *sink* boundary mean the render path is the
/// bottleneck (CPU readback, GPU upload, vsync, etc.); dropped frames
/// upstream of the sink are reported separately by the pipeline controller.
/// </param>
/// <param name="LastPresentedPresentationTime">
/// PTS of the most recently presented frame, or <see langword="null"/> when
/// no frame has been presented yet. Pairs with
/// <paramref name="LastPresentedAtUtc"/> for A/V drift computation.
/// </param>
/// <param name="LastPresentedAtUtc">
/// Wallclock at which the most recently presented frame was handed to the
/// render path, or <see langword="null"/> when no frame has been presented.
/// </param>
/// <param name="FramesCommitted">
/// Total frames whose hand-off to the renderer actually <b>committed</b> — for the
/// zero-copy compositor presenter, the frames the compositor drained off its queue
/// (ADR-0064 §Observability). Distinct from <paramref name="FramesPresented"/>, which counts at
/// <i>enqueue</i>: a persistent gap (presented climbing, committed flat) means frames
/// are queued but not reaching the screen. Sinks that do not distinguish the two stages
/// (e.g. the CPU <c>WriteableBitmap</c> path) report 0 here.
/// </param>
/// <param name="LastCommittedAtUtc">
/// Wallclock of the most recently committed frame, or <see langword="null"/> when none
/// has committed (or the sink does not track commit separately from present).
/// </param>
public sealed record VideoSinkDiagnosticsSnapshot(
    long FramesPresented,
    long FramesDropped,
    TimeSpan? LastPresentedPresentationTime,
    DateTime? LastPresentedAtUtc,
    long FramesCommitted = 0,
    DateTime? LastCommittedAtUtc = null
)
{
    /// <summary>
    /// Zero-valued snapshot used by null-object sinks and as the seed value
    /// for rollups when no video sink is registered.
    /// </summary>
    public static VideoSinkDiagnosticsSnapshot Empty { get; } =
        new(
            FramesPresented: 0,
            FramesDropped: 0,
            LastPresentedPresentationTime: null,
            LastPresentedAtUtc: null
        );
}
