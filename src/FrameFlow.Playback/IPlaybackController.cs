// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback.Diagnostics;

namespace FrameFlow.Playback;

/// <summary>
/// Public contract for controlling media playback. Commands are serialized through
/// an internal channel and processed sequentially against the state machines.
/// All async methods return <see cref="Result"/> rather than throwing on expected failures.
/// </summary>
/// <remarks>
/// The controller manages three orthogonal state machines:
/// <list type="bullet">
///   <item><description>Primary playback region (8 public states; see <see cref="PlaybackState"/>)</description></item>
///   <item><description>Seeking region (3 states)</description></item>
///   <item><description>Repeat/loop region (2 modes)</description></item>
/// </list>
/// </remarks>
public interface IPlaybackController : IAsyncDisposable
{
    // ── Commands ──────────────────────────────────────────────────────────

    /// <summary>Load a media source and prepare the pipeline for playback.</summary>
    Task<Result> LoadAsync(IMediaSource source, CancellationToken cancellationToken = default);

    /// <summary>Unload the media source and tear down the pipeline.</summary>
    Task<Result> UnloadAsync(CancellationToken cancellationToken = default);

    /// <summary>Start or resume forward playback.</summary>
    Task<Result> PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>Pause playback at the current position.</summary>
    Task<Result> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Seek to <paramref name="position"/> in the media timeline.</summary>
    Task<Result> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>Change the repeat/loop mode.</summary>
    Task<Result> SetRepeatModeAsync(RepeatMode mode, CancellationToken cancellationToken = default);

    // ── State ────────────────────────────────────────────────────────────

    /// <summary>Current primary playback state.</summary>
    PlaybackState State { get; }

    /// <summary>Current seeking region state.</summary>
    SeekState SeekingState { get; }

    /// <summary>Current repeat/loop mode.</summary>
    RepeatMode RepeatMode { get; }

    /// <summary>
    /// Returns <see langword="true"/> when media frames are actively being
    /// rendered — the primary state is <see cref="PlaybackState.Playing"/>
    /// and no seek operation is in progress. Use this instead of comparing
    /// <see cref="State"/> directly when you need to know whether frames
    /// are flowing, since <see cref="State"/> remains
    /// <see cref="PlaybackState.Playing"/> during an in-progress seek.
    /// </summary>
    bool IsActivelyPresenting { get; }

    /// <summary>Current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>Total duration of the loaded media, or <see cref="TimeSpan.Zero"/> if unknown.</summary>
    TimeSpan Duration { get; }

    /// <summary>Metadata for the loaded media, or <see langword="null"/> if nothing is loaded.</summary>
    MediaInfo? MediaInfo { get; }

    // ── Observable events ────────────────────────────────────────────────

    /// <summary>Fires on every primary playback state transition.</summary>
    IObservable<StateTransition<PlaybackState>> PlaybackStateChanged { get; }

    /// <summary>Fires on every seeking state transition.</summary>
    IObservable<StateTransition<SeekState>> SeekStateChanged { get; }

    /// <summary>Fires on every repeat mode change.</summary>
    IObservable<StateTransition<RepeatMode>> RepeatModeChanged { get; }

    /// <summary>Fires when a loop restart occurs.</summary>
    IObservable<LoopRestarted> LoopRestarted { get; }

    /// <summary>
    /// Fires when a single-item loop (<c>RepeatMode.One</c>) appears to have
    /// stalled — the position overran the item duration without a restart, i.e.
    /// frame delivery stopped while the clock kept advancing. Hosts can surface
    /// this to health/telemetry. See <see cref="FrameFlow.Media.LoopStalled"/>.
    /// </summary>
    IObservable<LoopStalled> LoopStalled { get; }

    /// <summary>Fires when an error occurs during playback.</summary>
    IObservable<PlaybackError> ErrorOccurred { get; }

    /// <summary>Periodic position updates during playback.</summary>
    IObservable<TimeSpan> PositionTick { get; }

    // ── Diagnostics (ADR-0034) ────────────────────────────────────────────

    /// <summary>
    /// Returns a coherent snapshot of the controller's observable state plus
    /// pipeline-level rollup (ADR-0034). This is the sanctioned entry point
    /// for player UIs, integration tests, and any consumer that needs
    /// thread-safe access to "what's the playback doing right now?" without
    /// reaching into subsystem internals.
    /// </summary>
    /// <remarks>
    /// Cheap enough to call from a UI timer at ~2 Hz; not a hot-path call.
    /// Returns <see cref="PlaybackDiagnosticsSnapshot.Empty"/> when no media
    /// is loaded.
    /// </remarks>
    PlaybackDiagnosticsSnapshot GetDiagnostics();
}
