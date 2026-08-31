// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Player;

/// <summary>
/// A player that presents an ordered, optionally looping sequence of sources
/// through ONE warm video sink and ONE warm audio sink. Item boundaries swap
/// only the decode source; the presenter (sink + GPU resources) and the playback
/// clock stay warm across the whole playlist, so the per-item present-pipeline
/// rebuild a naive consumer pays today is eliminated.
/// </summary>
/// <remarks>
/// <para>
/// The inherited <see cref="IMediaPlayer"/> transport acts on the <b>current</b>
/// item: <see cref="IMediaPlayer.PlayAsync"/> / <see cref="IMediaPlayer.PauseAsync"/>
/// pause and resume it, <see cref="IMediaPlayer.SeekAsync"/> seeks within its
/// timeline, and <see cref="IMediaPlayer.Position"/> /
/// <see cref="IMediaPlayer.Duration"/> / <see cref="IMediaPlayer.MediaInfo"/>
/// reflect it (and update on <see cref="SourceTransitioned"/>).
/// </para>
/// <para>
/// <see cref="RepeatMode.All"/> loops the whole queue (wrapping at the end);
/// <see cref="RepeatMode.One"/> loops the current item; <see cref="RepeatMode.Off"/>
/// ends after the last queued item. For continuous rotation under
/// <see cref="RepeatMode.Off"/>, enqueue the next item from a
/// <see cref="SourceTransitioned"/> handler so the queue never empties.
/// </para>
/// </remarks>
public interface IMediaPlaylistPlayer : IMediaPlayer
{
    /// <summary>The source currently presenting, or <see langword="null"/> before the first item.</summary>
    IMediaSource? CurrentSource { get; }

    /// <summary>Append a source to the tail of the play queue.</summary>
    Task EnqueueAsync(IMediaSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Make <paramref name="source"/> the very next item to play, ahead of
    /// anything already queued. A <see langword="null"/> value is a no-op.
    /// </summary>
    Task SetNextAsync(IMediaSource? source, CancellationToken cancellationToken = default);

    /// <summary>End the current item now and hand off to the next (no presenter rebuild).</summary>
    Task SkipToNextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires once per hand-off (including the first item) when the presenter
    /// switches from one source to the next. Carries the new
    /// <see cref="CurrentSource"/> and its <see cref="MediaInfo"/> so a consumer
    /// can advance its own model and, under <see cref="RepeatMode.Off"/>, enqueue
    /// the following item to keep a rotation going.
    /// </summary>
    IObservable<PlaylistTransition> SourceTransitioned { get; }
}
