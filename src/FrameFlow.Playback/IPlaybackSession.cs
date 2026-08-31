// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Playback.Diagnostics;

namespace FrameFlow.Playback;

/// <summary>
/// Internal lifecycle interface for a playback session. The controller calls these
/// methods from its entry actions — the session does not own state transitions.
/// </summary>
/// <remarks>
/// <para>
/// Per R018 and D004, this interface is internal. The public API surface is
/// <see cref="IPlaybackController"/>. Transport-control methods have been replaced
/// with lifecycle-oriented operations that the controller orchestrates.
/// </para>
/// <para>
/// The session owns runtime resources such as demux, decoders, worker orchestration,
/// and clock coordination. It may activate, pause, resume, or deactivate caller-supplied
/// sinks as part of those lifecycle operations, but it does not own the sink object
/// lifetime and must not dispose externally supplied sink instances.
/// </para>
/// </remarks>
internal interface IPlaybackSession : IAsyncDisposable
{
    // ── Read-only state for controller delegation ───────────────────────

    /// <summary>Media information available after <see cref="InitializeAsync"/>.</summary>
    MediaInfo? MediaInfo { get; }

    /// <summary>Total duration of the loaded media.</summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Returns the pipeline-level diagnostics snapshot (ADR-0034). Default
    /// returns <see cref="PipelineDiagnosticsSnapshot.Empty"/>; concrete
    /// sessions override to delegate to their underlying runtime.
    /// </summary>
    PipelineDiagnosticsSnapshot GetPipelineDiagnostics() => PipelineDiagnosticsSnapshot.Empty;

    // ── Lifecycle methods (called by PlaybackController entry actions) ──

    /// <summary>
    /// Opens the media source, creates demux session, decoders, pipeline controller,
    /// and decoding pipeline. Does NOT start workers or transition states.
    /// </summary>
    ValueTask InitializeAsync(IMediaSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pre-warms the pipeline by starting the decoder graph with sink-side
    /// gates closed and awaiting at least one decoded video frame before
    /// returning. Absorbs hardware-decoder cold-start latency during the
    /// controller's <c>InitialBuffering</c> state so the first
    /// <see cref="PlayAsync"/> can open the gates with a frame already ready —
    /// preventing audio from running ahead of video on a fresh start.
    /// </summary>
    /// <remarks>
    /// Idempotent. Audio-only sources return immediately. Implementations that
    /// do no warmup work may return synchronously.
    /// </remarks>
    ValueTask WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts or resumes playback. The session tracks its own activation state
    /// internally: the first call after <see cref="InitializeAsync"/> activates
    /// renderers, starts the clock, and launches pipeline workers; subsequent
    /// calls (after a pause) resume the existing runtime without reactivating.
    /// </summary>
    /// <remarks>
    /// Per ADR-0028 §3, the controller no longer distinguishes first-play from
    /// resume — that state lives inside the session. After a terminal teardown
    /// (stop, error, natural end) a new session instance is required before
    /// calling <see cref="PlayAsync"/> again.
    /// </remarks>
    ValueTask PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses workers, clock, and audio sink without tearing down the pipeline or changing
    /// sink ownership.
    /// </summary>
    ValueTask PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeks to <paramref name="position"/> by flushing the pipeline, repositioning
    /// the demux session, resetting the clock, and transiently quiescing audio output.
    /// Does not check or transition state.
    /// </summary>
    ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewinds a single-source loop back to its start (<see cref="TimeSpan.Zero"/>) for a
    /// <c>RepeatMode.One</c> loop boundary, using the cheapest path that produces a clean
    /// A/V restart from frame 0 with the master clock re-seated to the loop epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it differs from <see cref="SeekAsync"/>(0).</b> A <c>RepeatMode.One</c> loop is
    /// driven by natural end-of-stream: when the controller fires this, the graph and demux
    /// pump have already run dry and completed cleanly, so there is nothing in flight to
    /// cancel. The full seek path, by contrast, is built for a seek that can land at any time
    /// during active playback: it cancels the session CTS to stop a running graph mid-flight
    /// (the cancel-mid-GPU-op implicated in the native seek-cancel wedge), tears the graph
    /// down, then rebuilds a brand-new graph topology on restart. The rewind keeps the
    /// long-lived graph object and merely re-runs it after repositioning the demuxer and
    /// resetting the decoders — skipping the CTS-cancel of in-flight work and the graph
    /// teardown/rebuild, which is the recurring per-loop CPU/GPU spike on a 24/7 attract loop.
    /// </para>
    /// <para>
    /// <b>Correctness parity with the seek path.</b> The rewind performs the same
    /// position-discontinuity resets the seek path does — demuxer reposition to the first
    /// packet, the uniform <c>ResetForSeek</c> pass over decoders + pipeline (codec flush +
    /// packet-queue replacement + pending-packet drop), and the master-clock epoch reseat via
    /// <c>ISeekableClock.SeekBaseline(0)</c> — so the loop epoch is established exactly as a
    /// user seek to 0 would, with no gapless-loop clock drift and no pre-loop frame leaking
    /// across the boundary.
    /// </para>
    /// <para>
    /// Does not check or transition controller state; the controller routes this through the
    /// same seek state machine as a user seek so observers still see the loop as a seek and a
    /// concurrent user seek can cancel it.
    /// </para>
    /// </remarks>
    ValueTask RewindToStartAsync(CancellationToken cancellationToken = default) =>
        SeekAsync(TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// Performs terminal teardown for the active runtime. The session must leave any
    /// caller-supplied sinks quiescent and detach runtime resources, but it must not
    /// dispose externally owned sink objects.
    /// </summary>
    new ValueTask DisposeAsync();
}
