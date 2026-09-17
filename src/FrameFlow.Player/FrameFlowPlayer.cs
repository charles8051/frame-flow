// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Player;

/// <summary>
/// Entry point for the fluent player builder. Standard pattern:
/// <code>
/// await using var player = await FrameFlowPlayer
///     .Create(path)
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
///     .Create(path)
///     .WithVideoSink(view)
///     .WithAudioSink(audio)
///     .WithRepeatMode(RepeatMode.All)
///     .BuildPlayerAsync();
/// await player.PlayAsync();
/// </code>
/// </para>
/// <para>
/// <b>Nothing is opened here.</b> <c>Create</c> records what the player will start
/// with and returns the builder; the demuxer runs in the terminal. A chain can
/// therefore name no source at all, and the player is built with an empty queue:
/// <code>
/// await using var player = await FrameFlowPlayer
///     .Create()
///     .WithVideoSink(view)
///     .BuildPlayerAsync();
///
/// await player.AddAsync(source);   // arrives later
/// await player.PlayAsync();        // starts what the queue holds by now
/// </code>
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
    /// Begins a builder chain for the media at <paramref name="path"/>.
    /// The file is not opened until <see cref="IPlayerBuilder.BuildAsync"/>
    /// resolves.
    /// </summary>
    public static IPlayerBuilder Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PlayerBuilder([MediaSource.FromFile(path)]);
    }

    /// <summary>
    /// Begins a builder chain for an arbitrary <see cref="IMediaSource"/>.
    /// </summary>
    public static IPlayerBuilder Create(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PlayerBuilder([source]);
    }

    /// <summary>
    /// Begins a builder chain over an ordered queue. The chain starts narrowed to
    /// <see cref="IMediaPlayerBuilder"/>: a <see cref="PlayerSession"/> plays one source, so
    /// <see cref="IPlayerBuilder.BuildAsync"/> is not on offer over a queue.
    /// </summary>
    public static IMediaPlayerBuilder Create(IEnumerable<IMediaSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return new PlayerBuilder([.. sources]);
    }

    /// <summary>
    /// Begins a builder chain with nothing queued. The player is built with its sinks warm and
    /// nothing loaded; the first <see cref="IMediaPlayer.PlayAsync"/> starts whatever
    /// <see cref="IMediaPlaylistPlayer.AddAsync"/> and
    /// <see cref="IMediaPlaylistPlayer.EnqueueAsync"/> have put in the queue by then, and is
    /// refused while the queue is still empty.
    /// </summary>
    /// <remarks>
    /// This is the shape for a host that builds its presenter at startup and receives content
    /// afterwards. Building with a placeholder source and replacing it costs a load and a
    /// teardown that this avoids.
    /// </remarks>
    public static IMediaPlayerBuilder Create() => new PlayerBuilder([]);
}
