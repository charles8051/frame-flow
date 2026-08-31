// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Audio.OpenAL;

/// <summary>
/// Provides <see cref="IPlayerBuilder"/> extension methods for attaching the
/// OpenAL audio backend to a fluent FrameFlow player.
/// </summary>
public static class FrameFlowOpenAlBuilderExtensions
{
    /// <summary>
    /// Constructs an <see cref="OpenAlAudioSink"/> and attaches it to the
    /// fluent <see cref="IPlayerBuilder"/>. The sink is the standard
    /// out-of-the-box audio backend for desktop FrameFlow players.
    /// </summary>
    /// <param name="builder">The player builder being configured.</param>
    /// <param name="loggerFactory">
    /// Optional logger factory used to create a logger for the sink. When
    /// <see langword="null"/>, the sink runs without logging.
    /// </param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <example>
    /// <code>
    /// _player = await FrameFlowPlayer.Open(path)
    ///     .WithAvaloniaVideoView(VideoView)
    ///     .WithOpenAlAudio(loggerFactory)
    ///     .BuildAsync();
    /// </code>
    /// </example>
    public static IPlayerBuilder WithOpenAlAudio(
        this IPlayerBuilder builder,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Ownership transfers to the player builder, which holds the sink for
        // the player's lifetime. CA2000 can't see the ownership handoff.
#pragma warning disable CA2000
        var sink = new OpenAlAudioSink(loggerFactory?.CreateLogger<OpenAlAudioSink>());
#pragma warning restore CA2000
        return builder.WithAudioSink(sink);
    }
}
