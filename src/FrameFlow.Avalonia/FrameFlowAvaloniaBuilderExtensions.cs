// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia;

/// <summary>
/// Provides <see cref="IPlayerBuilder"/> and <see cref="IPassBuilder"/> extension methods for
/// wiring an Avalonia video surface into the fluent FrameFlow player.
/// </summary>
public static class FrameFlowAvaloniaBuilderExtensions
{
    /// <summary>
    /// Wires the supplied <see cref="FrameFlowVideoView"/> to the fluent
    /// <see cref="IPlayerBuilder"/>. The view's owned sink is materialized
    /// eagerly via <see cref="FrameFlowVideoView.EnsureSink"/> so playback
    /// can begin presenting frames the moment
    /// <see cref="IPlayerBuilder.BuildPlayerAsync"/> resolves — even if the view
    /// has not yet attached to the visual tree.
    /// </summary>
    /// <remarks>
    /// The view's own sink logs through its <see cref="FrameFlowVideoView.LoggerFactory"/>, which can
    /// be assigned before or after this call; the builder's <c>WithLogger</c> does not reach it.
    /// <c>WithVideoSurface(view, loggerFactory)</c> wires the factory in the same call.
    /// </remarks>
    /// <param name="builder">The player builder being configured.</param>
    /// <param name="view">The Avalonia control that will render frames.</param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <example>
    /// <code>
    /// _player = await FrameFlowPlayer.Create()
    ///     .WithMedia(path)
    ///     .WithAvaloniaVideoView(VideoView)
    ///     .BuildPlayerAsync();
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

    /// <summary>
    /// Wires the supplied <see cref="FrameFlowVideoView"/> to a narrowed
    /// <see cref="IPassBuilder"/> chain — the overload for chains
    /// headed for <see cref="IPassBuilder.BuildAsync"/>. The
    /// view's owned sink is materialized eagerly via
    /// <see cref="FrameFlowVideoView.EnsureSink"/>.
    /// </summary>
    /// <param name="builder">The player builder being configured.</param>
    /// <param name="view">The Avalonia control that will render frames.</param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    public static IPassBuilder WithAvaloniaVideoView(
        this IPassBuilder builder,
        FrameFlowVideoView view
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(view);
        return builder.WithVideoSink(view.EnsureSink());
    }

    /// <summary>
    /// Wires any <see cref="IVideoSurface"/> to the fluent <see cref="IPlayerBuilder"/>: the
    /// surface gets <paramref name="loggerFactory"/> and hands back its sink, through
    /// <see cref="IVideoSurface.AttachSink"/>, whether or not it has joined the visual tree yet.
    /// </summary>
    /// <remarks>
    /// This is the one call that readies the Windows zero-copy presenter
    /// (<c>FrameFlow.Avalonia.Windows.CompositionInteropVideoView</c>) and the CPU
    /// <see cref="FrameFlowVideoView"/> alike. The builder's own <c>WithLogger</c> does not reach
    /// the surface, so pass the same factory here; pass <c>NullLoggerFactory.Instance</c> for none.
    /// Call on the UI thread: the zero-copy view, once on screen, sets up its compositor surface
    /// in this call.
    /// </remarks>
    /// <param name="builder">The player builder being configured.</param>
    /// <param name="surface">The control that will render frames.</param>
    /// <param name="loggerFactory">The logger factory the surface and its sink log through.</param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <example>
    /// <code>
    /// _player = await FrameFlowPlayer.Create()
    ///     .WithMedia(path)
    ///     .WithVideoSurface(VideoView, loggerFactory)
    ///     .WithLogger(loggerFactory)
    ///     .BuildPlayerAsync();
    /// </code>
    /// </example>
    public static IPlayerBuilder WithVideoSurface(
        this IPlayerBuilder builder,
        IVideoSurface surface,
        ILoggerFactory loggerFactory
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return builder.WithVideoSink(surface.AttachSink(loggerFactory));
    }

    /// <summary>
    /// Wires any <see cref="IVideoSurface"/> to a narrowed <see cref="IPassBuilder"/> chain, the
    /// overload for chains headed for <see cref="IPassBuilder.BuildAsync"/>. The surface gets
    /// <paramref name="loggerFactory"/> and hands back its sink, through
    /// <see cref="IVideoSurface.AttachSink"/>.
    /// </summary>
    /// <remarks>Call on the UI thread, as for the <see cref="IPlayerBuilder"/> overload.</remarks>
    /// <param name="builder">The pass builder being configured.</param>
    /// <param name="surface">The control that will render frames.</param>
    /// <param name="loggerFactory">The logger factory the surface and its sink log through.</param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    public static IPassBuilder WithVideoSurface(
        this IPassBuilder builder,
        IVideoSurface surface,
        ILoggerFactory loggerFactory
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return builder.WithVideoSink(surface.AttachSink(loggerFactory));
    }
}
