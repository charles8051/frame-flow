// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Player;

namespace FrameFlow.Avalonia;

/// <summary>
/// Provides <see cref="IPlayerBuilder"/> extension methods for wiring an
/// Avalonia <see cref="FrameFlowVideoView"/> into the fluent FrameFlow player.
/// </summary>
public static class FrameFlowAvaloniaBuilderExtensions
{
    /// <summary>
    /// Wires the supplied <see cref="FrameFlowVideoView"/> to the fluent
    /// <see cref="IPlayerBuilder"/>. The view's owned sink is materialized
    /// eagerly via <see cref="FrameFlowVideoView.EnsureSink"/> so playback
    /// can begin presenting frames the moment
    /// <see cref="IPlayerBuilder.BuildAsync"/> resolves — even if the view
    /// has not yet attached to the visual tree.
    /// </summary>
    /// <param name="builder">The player builder being configured.</param>
    /// <param name="view">The Avalonia control that will render frames.</param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <example>
    /// <code>
    /// _player = await FrameFlowPlayer.Open(path)
    ///     .WithAvaloniaVideoView(VideoView)
    ///     .WithOpenAlAudio()
    ///     .BuildAsync();
    /// </code>
    /// </example>
    public static IPlayerBuilder WithAvaloniaVideoView(
        this IPlayerBuilder builder,
        FrameFlowVideoView view
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(view);
        return builder.WithVideoSink(view.EnsureSink());
    }
}
