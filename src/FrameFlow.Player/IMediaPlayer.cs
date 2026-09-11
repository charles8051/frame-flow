// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;

namespace FrameFlow.Player;

/// <summary>
/// The player surface: a small, task-based API over
/// <see cref="IPlaybackController"/>, which it wraps and projects to a
/// simpler shape. Built by <see cref="MediaPlayer.CreateAsync"/>.
/// <para>
/// This is an interface rather than a concrete type because the
/// <c>FrameFlow.Avalonia</c> chrome controls and the
/// <c>FrameFlow.Audio.OpenAL</c> fluent extension both take it as a
/// polymorphic dependency.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Error model (ADR-0069).</b> Transport commands return
/// <see cref="Result"/> rather than throwing, matching
/// <see cref="IPlaybackController"/>. A command that the state machine
/// refuses — a seek on a non-seekable source, a play on a disposed player —
/// is an expected outcome, and <see cref="Result.Error"/> carries the
/// <see cref="ErrorCategory"/> the controller produced.
/// </para>
/// <para>
/// Exceptions are still thrown, for the cases that are not expected
/// outcomes: <see cref="ArgumentException"/> and friends for a caller that
/// passed something invalid, and whatever escapes a sink or the decode
/// stack. Failures that arise mid-playback rather than in answer to a
/// command surface on <see cref="ErrorOccurred"/>.
/// </para>
/// </remarks>
public interface IMediaPlayer : IAsyncDisposable
{
    /// <summary>Begin or resume playback.</summary>
    /// <returns>
    /// A successful <see cref="Result"/>, or one whose
    /// <see cref="Result.Error"/> says why the command was refused.
    /// </returns>
    Task<Result> PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>Pause playback at the current position.</summary>
    /// <returns>
    /// A successful <see cref="Result"/>, or one whose
    /// <see cref="Result.Error"/> says why the command was refused.
    /// </returns>
    Task<Result> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Seek to the given position in media time.</summary>
    /// <returns>
    /// A successful <see cref="Result"/>, or one whose
    /// <see cref="Result.Error"/> says why the command was refused —
    /// seeking a non-seekable source is the common case.
    /// </returns>
    Task<Result> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>Change repeat/loop behavior.</summary>
    /// <returns>
    /// A successful <see cref="Result"/>, or one whose
    /// <see cref="Result.Error"/> says why the command was refused.
    /// </returns>
    Task<Result> SetRepeatModeAsync(RepeatMode mode, CancellationToken cancellationToken = default);

    /// <summary>Current primary playback state.</summary>
    PlaybackState State { get; }

    /// <summary>Current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>Total duration of the loaded media.</summary>
    TimeSpan Duration { get; }

    /// <summary>Metadata for the loaded media.</summary>
    MediaInfo MediaInfo { get; }

    /// <summary>Stream of primary playback state transitions.</summary>
    /// <remarks>
    /// Carries the state the player moved <i>to</i>, which is what a badge or a
    /// button row needs. The layer below reports the same transitions as
    /// <see cref="IPlaybackController.PlaybackStateChanged"/>, typed
    /// <c>StateTransition&lt;PlaybackState&gt;</c> so it also carries the state
    /// moved <i>from</i>. The two names differ because the two payloads do;
    /// take the controller's stream if you need the previous state.
    /// </remarks>
    IObservable<PlaybackState> StateChanged { get; }

    /// <summary>Stream of position updates.</summary>
    /// <remarks>
    /// A cadence, not a change notification. The controller samples the
    /// playback clock on a 250 ms <see cref="PeriodicTimer"/> while the player
    /// is in <see cref="PlaybackState.Playing"/> and pushes whatever it reads,
    /// so a value can repeat and the stream is silent while paused. This is
    /// <see cref="IPlaybackController.PositionTick"/> unprojected.
    /// </remarks>
    IObservable<TimeSpan> PositionTick { get; }

    /// <summary>
    /// Fires when a single-item loop appears to have stalled — the position
    /// overran the item duration without a restart (frame delivery stopped while
    /// the clock kept advancing). Hosts can surface this to health/telemetry.
    /// </summary>
    IObservable<LoopStalled> LoopStalled { get; }

    /// <summary>
    /// Fires when a failure arises during playback rather than in answer to a
    /// command. A command's own failure comes back on its <see cref="Result"/>
    /// and is not repeated here.
    /// </summary>
    /// <remarks>
    /// Forwarded from <see cref="IPlaybackController.ErrorOccurred"/>. Without
    /// it a consumer holding only the player surface has no structured error
    /// channel for anything the decode stack reports mid-stream.
    /// </remarks>
    IObservable<PlaybackError> ErrorOccurred { get; }

    /// <summary>Stream of diagnostics snapshots.</summary>
    IObservable<PlaybackDiagnosticsSnapshot> Diagnostics { get; }

    /// <summary>Returns a snapshot of diagnostics on demand.</summary>
    /// <remarks>
    /// Named for the ADR-0034 convention that every diagnostics-bearing surface
    /// in FrameFlow follows — sinks, decoders, the demux session and the
    /// controller all spell it <c>GetDiagnostics()</c>. Cheap enough for a UI
    /// timer at ~2 Hz; not a hot-path call.
    /// </remarks>
    PlaybackDiagnosticsSnapshot GetDiagnostics();

    /// <summary>
    /// Whether the underlying audio sink can actually change output gain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the capability-discovery pattern for the player surface.
    /// A consumer holds an <see cref="IMediaPlayer"/>, never the sink, so it
    /// cannot type-test for <see cref="FrameFlow.Media.IVolumeControl"/>
    /// itself. This property asks that question on its behalf: it is
    /// <see langword="true"/> exactly when an audio sink is attached and that
    /// sink implements <see cref="FrameFlow.Media.IVolumeControl"/>.
    /// </para>
    /// <para>
    /// UI should gate on this rather than writing blind. A volume slider bound
    /// to a player where this is <see langword="false"/> should disable
    /// itself; otherwise it looks live and does nothing. See
    /// <c>FrameFlowVolumeControl</c> for the reference treatment.
    /// </para>
    /// <para>
    /// Future capabilities on this surface should follow the same shape:
    /// a <c>Supports…</c> property backed by a type test on the composed
    /// object, not a capability record handed up from below.
    /// </para>
    /// </remarks>
    bool SupportsVolumeControl { get; }

    /// <summary>
    /// Master output gain. <c>0.0</c> is silent, <c>1.0</c> is unity.
    /// </summary>
    /// <remarks>
    /// When <see cref="SupportsVolumeControl"/> is <see langword="false"/>
    /// the setter is a no-op rather than a throw: a consumer that ignores the
    /// capability should not crash over a cosmetic control. The getter still
    /// round-trips whatever was last written, so a UI reading the value back
    /// to render a label or icon shows what the user chose instead of a
    /// value they never set.
    /// </remarks>
    float Volume { get; set; }

    /// <summary>Master mute.</summary>
    /// <remarks>
    /// Same no-op-and-round-trip behaviour as <see cref="Volume"/> when
    /// <see cref="SupportsVolumeControl"/> is <see langword="false"/>. Mute
    /// needs no capability of its own: a sink with no gain stage cannot mute
    /// either, so one flag covers both.
    /// </remarks>
    bool Muted { get; set; }
}
