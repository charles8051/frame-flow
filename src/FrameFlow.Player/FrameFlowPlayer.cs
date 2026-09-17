// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Player;

/// <summary>
/// Entry point for the fluent player builder. Standard pattern:
/// <code>
/// await using var player = await FrameFlowPlayer
///     .Create()
///     .WithMedia(path)
///     .WithVideoSink(view)
///     .WithAudioSink(audio)
///     .BuildAsync();
/// await player.PlayToCompletionAsync(ct);
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>Two terminals.</b> <see cref="IPlayerBuilder.BuildAsync"/> produces a
/// <see cref="PlayerSession"/>, which covers "open a source and play it to
/// end of stream" and nothing more.
/// <see cref="IPlayerBuilder.BuildPlayerAsync"/> produces an
/// <see cref="IMediaPlaylistPlayer"/> — pause, resume, seek, repeat, position
/// and diagnostics, plus the queue. Everything before the terminal is the same
/// chain:
/// <code>
/// await using var player = await FrameFlowPlayer
///     .Create()
///     .WithMedia([first, second])
///     .WithVideoSink(view)
///     .WithRepeatMode(RepeatMode.All)
///     .BuildPlayerAsync();
/// await player.PlayAsync();
/// </code>
/// </para>
/// <para>
/// <b>Media is an option, not the entry.</b> Every player is a queue, and a queue can be empty,
/// so <c>Create</c> names nothing. A chain with no <c>WithMedia</c> builds a player with its
/// sinks warm and nothing loaded, and the first <see cref="IMediaPlayer.PlayAsync"/> starts
/// whatever <see cref="IMediaPlaylistPlayer.AddAsync"/> has put in the queue by then. The same
/// chain cannot build a <see cref="PlayerSession"/>, which plays one source: that throws, because
/// <c>WithMedia</c> comes after the entry and the types cannot rule it out.
/// </para>
/// <para>
/// Nothing is opened here or by <c>WithMedia</c>. Both record what the player will start with;
/// the demuxer runs in the terminal.
/// </para>
/// <para>
/// Prefer this builder. <see cref="MediaPlayer"/>'s <c>CreateAsync</c> is the
/// positional form of the second terminal. It runs the same wiring and cannot
/// inject a clock.
/// </para>
/// </remarks>
public static class FrameFlowPlayer
{
    /// <summary>
    /// Begins a builder chain. Name the media with <see cref="IPlayerBuilder.WithMedia(string)"/>
    /// or one of its overloads, or leave it out and add sources to the built player.
    /// </summary>
    public static IPlayerBuilder Create() => new PlayerBuilder();
}
