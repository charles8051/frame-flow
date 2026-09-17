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
/// <b>Two terminals.</b> <see cref="IPlayerBuilder.BuildAsync"/> produces a
/// <see cref="PlayerSession"/>, which covers "open a source and play it to
/// end of stream" and nothing more.
/// <see cref="IPlayerBuilder.BuildPlayerAsync"/> produces an
/// <see cref="IMediaPlaylistPlayer"/> — pause, resume, seek, repeat, position
/// and diagnostics, plus the queue. Everything before the terminal is the same
/// chain:
/// <code>
/// await using var player = await FrameFlowPlayer
///     .Open(path)
///     .WithVideoSink(view)
///     .WithAudioSink(audio)
///     .WithRepeatMode(RepeatMode.All)
///     .BuildPlayerAsync();
/// await player.PlayAsync();
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
    public static IPlayerBuilder Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PlayerBuilder([MediaSource.FromFile(path)]);
    }

    /// <summary>
    /// Begins a builder chain for an arbitrary <see cref="IMediaSource"/>.
    /// </summary>
    public static IPlayerBuilder Open(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PlayerBuilder([source]);
    }

    /// <summary>
    /// Begins a builder chain over an ordered queue. The chain starts narrowed to
    /// <see cref="IMediaPlayerBuilder"/>: a <see cref="PlayerSession"/> plays one source, so
    /// <see cref="IPlayerBuilder.BuildAsync"/> is not on offer over a queue.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sources"/> is empty.</exception>
    public static IMediaPlayerBuilder Open(IEnumerable<IMediaSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var initial = sources.ToList();
        if (initial.Count == 0)
            throw new ArgumentException("A player requires at least one source.", nameof(sources));

        return new PlayerBuilder(initial);
    }
}
