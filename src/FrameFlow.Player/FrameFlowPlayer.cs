// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Player;

/// <summary>
/// Entry point for the fluent player builder: paced playback, for a viewer.
/// <code>
/// await using var player = await FrameFlowPlayer
///     .Create()
///     .WithMedia(path)
///     .WithVideoSink(view)
///     .WithAudioSink(audio)
///     .BuildPlayerAsync();
/// await player.PlayAsync();
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>One terminal.</b> <see cref="IPlayerBuilder.BuildPlayerAsync"/> produces an
/// <see cref="IMediaPlaylistPlayer"/> — pause, resume, seek, repeat, position and diagnostics,
/// plus the queue. Every option on the chain means something to it, so there is nothing to refuse
/// and no narrowing.
/// </para>
/// <para>
/// <b>The other entry point.</b> <see cref="FrameFlowPass"/> runs one source through once at
/// decode speed, waiting on no presentation time. That is what an inference or analysis run
/// wants. A player honours a clock and shows each frame at its presentation time, which is what a
/// viewer wants. The choice is the entry, not the terminal — ADR-0079.
/// </para>
/// <para>
/// <b>Media is an option.</b> Every player is a queue and a queue can be empty, so <c>Create</c>
/// names nothing. A chain with no <c>WithMedia</c> builds a player with its sinks warm and
/// nothing loaded, and the first <see cref="IMediaPlayer.PlayAsync"/> starts whatever
/// <see cref="IMediaPlaylistPlayer.AddAsync"/> has put in the queue by then:
/// <code>
/// await using var player = await FrameFlowPlayer
///     .Create()
///     .WithVideoSink(view)
///     .BuildPlayerAsync();
///
/// await player.AddAsync(source);   // arrives later
/// await player.PlayAsync();
/// </code>
/// </para>
/// <para>
/// Nothing is opened here or by <c>WithMedia</c>. Both record what the player will start with;
/// the demuxer runs in the terminal.
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
