// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Player;

/// <summary>
/// Entry point for the fluent player builder. Standard pattern:
/// <code>
/// await using var player = await FrameFlowPlayer
///     .Open(path)
///     .WithVideoSink(view)
///     .WithAudioSink(audio)
///     .BuildAsync();
/// await player.PlayToCompletionAsync(ct);
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope, and when to use the other entry point.</b> This builder
/// produces a <see cref="PlayerSession"/>, which covers "open a source and
/// play it to end of stream". It deliberately does not expose the
/// <see cref="IMediaPlayer"/> state machine — no pause, resume, seek,
/// repeat, or observables.
/// </para>
/// <para>
/// Reach for <see cref="MediaPlayer.CreateAsync"/> when you need any of
/// those. It returns an <see cref="IMediaPlayer"/> and is the entry point
/// most of the examples use. This one is the leaner option for a host that
/// only needs playback to run to completion.
/// </para>
/// </remarks>
public static class FrameFlowPlayer
{
    /// <summary>
    /// Begins a builder chain for the media at <paramref name="path"/>.
    /// The file is not opened until <see cref="IPlayerBuilder.BuildAsync"/>
    /// resolves.
    /// </summary>
    public static IPlayerBuilder Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PlayerBuilder(MediaSource.FromFile(path));
    }

    /// <summary>
    /// Begins a builder chain for an arbitrary <see cref="IMediaSource"/>.
    /// </summary>
    public static IPlayerBuilder Open(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PlayerBuilder(source);
    }
}
