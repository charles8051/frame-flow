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
public interface IMediaPlayer : IAsyncDisposable
{
    /// <summary>Begin or resume playback.</summary>
    Task PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>Pause playback at the current position.</summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Seek to the given position in media time.</summary>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>Change repeat/loop behavior.</summary>
    Task SetRepeatModeAsync(RepeatMode mode, CancellationToken cancellationToken = default);

    /// <summary>Current primary playback state.</summary>
    PlaybackState State { get; }

    /// <summary>Current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>Total duration of the loaded media.</summary>
    TimeSpan Duration { get; }

    /// <summary>Metadata for the loaded media.</summary>
    MediaInfo MediaInfo { get; }

    /// <summary>Stream of primary playback state transitions.</summary>
    IObservable<PlaybackState> StateChanged { get; }

    /// <summary>Stream of position updates.</summary>
    IObservable<TimeSpan> PositionChanged { get; }

    /// <summary>
    /// Fires when a single-item loop appears to have stalled — the position
    /// overran the item duration without a restart (frame delivery stopped while
    /// the clock kept advancing). Hosts can surface this to health/telemetry.
    /// </summary>
    IObservable<LoopStalled> LoopStalled { get; }

    /// <summary>Stream of diagnostics snapshots.</summary>
    IObservable<PlaybackDiagnosticsSnapshot> Diagnostics { get; }

    /// <summary>Returns a snapshot of diagnostics on demand.</summary>
    PlaybackDiagnosticsSnapshot PollDiagnostics();

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
